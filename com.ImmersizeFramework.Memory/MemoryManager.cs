using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text;

namespace com.ImmersizeFramework.Memory {
    public class MemoryManager : IDisposable {
        #region Configuration & Nested Types
        [System.Serializable]
        public readonly struct MemorySettings {
            public readonly int MaxPoolSize, PreloadPoolSize, GCThresholdMB, MaxCacheSize;
            public readonly float CleanupIntervalSeconds;
            public readonly bool EnableProfiling, AutoPoolUnused, EnableCompression;

            public MemorySettings(int maxPool = 1000, int preload = 50, int gcThreshold = 256, int maxCache = 1000,
                                 float cleanup = 30f, bool profiling = true, bool autoPool = true, bool compression = false) {
                MaxPoolSize = maxPool; PreloadPoolSize = preload; GCThresholdMB = gcThreshold; MaxCacheSize = maxCache;
                CleanupIntervalSeconds = cleanup; EnableProfiling = profiling; AutoPoolUnused = autoPool; EnableCompression = compression;
            }
        }

        private sealed class ObjectPool<T> where T : class {
            public readonly ConcurrentQueue<T> Objects = new();
            public readonly Func<T> Factory;
            public readonly Action<T> Reset;
            public readonly Type Type = typeof(T);
            public volatile int TotalCreated, CurrentActive;
            public DateTime LastAccess = DateTime.UtcNow;

            public ObjectPool(Func<T> factory, Action<T> reset = null) {
                Factory = factory;
                Reset = reset ?? (_ => { });
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryGet(out T item) {
                LastAccess = DateTime.UtcNow;
                
                if (Objects.TryDequeue(out item)) {
                    CurrentActive++;
                    return true;
                }

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public T GetOrCreate() {
                if (TryGet(out var item)) return item;
                
                item = Factory();

                TotalCreated++;
                CurrentActive++;

                return item;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryReturn(T item, int maxSize) {
                if (item == null) return false;
                
                CurrentActive--;
                Reset(item);
                
                if (Objects.Count < maxSize) {
                    Objects.Enqueue(item);
                    return true;
                }

                return false;
            }
        }

        public readonly struct PoolMetrics {
            public readonly int TotalCreated, CurrentActive, PooledCount;
            public readonly DateTime LastAccess;
            public readonly Type Type;

            public PoolMetrics(int created, int active, int pooled, DateTime access, Type type) {
                TotalCreated = created; CurrentActive = active; PooledCount = pooled;
                LastAccess = access; Type = type;
            }

            public override string ToString() => $"{Type.Name}: {CurrentActive}A/{PooledCount}P/{TotalCreated}T";
        }

        private readonly struct CacheEntry<T> {
            public readonly T Value;
            public readonly DateTime AccessTime;
            public readonly int AccessCount;
            public readonly LinkedListNode<string> OrderNode;

            public CacheEntry(T value, DateTime accessTime, int accessCount, LinkedListNode<string> orderNode) {
                Value = value; AccessTime = accessTime; AccessCount = accessCount; OrderNode = orderNode;
            }

            public CacheEntry<T> WithAccess(DateTime newTime) =>
                new(Value, newTime, AccessCount + 1, OrderNode);
        }

        public readonly struct MemoryProfile {
            public readonly long TotalManagedMB, TotalAllocatedMB;
            public readonly int ActivePooledObjects, TotalPoolTypes, CacheSize;
            public readonly int GCGen0, GCGen1, GCGen2;

            public MemoryProfile(long managed, long allocated, int active, int types, int cache, int gc0, int gc1, int gc2) {
                TotalManagedMB = managed; TotalAllocatedMB = allocated; ActivePooledObjects = active;
                TotalPoolTypes = types; CacheSize = cache; GCGen0 = gc0; GCGen1 = gc1; GCGen2 = gc2;
            }

            public static MemoryProfile operator +(MemoryProfile a, MemoryProfile b) =>
                new(a.TotalManagedMB + b.TotalManagedMB, a.TotalAllocatedMB + b.TotalAllocatedMB,
                    a.ActivePooledObjects + b.ActivePooledObjects, a.TotalPoolTypes + b.TotalPoolTypes,
                    a.CacheSize + b.CacheSize, a.GCGen0 + b.GCGen0, a.GCGen1 + b.GCGen1, a.GCGen2 + b.GCGen2);

            public override string ToString() =>
                $"Memory: {TotalManagedMB}MB managed, {TotalAllocatedMB}MB allocated | Pools: {ActivePooledObjects} active, {TotalPoolTypes} types | Cache: {CacheSize} | GC: {GCGen0}/{GCGen1}/{GCGen2}";
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public long TotalAllocatedMemory => GC.GetTotalMemory(false);
        public long TotalManagedMemory => GC.GetTotalMemory(true);
        public int Priority => 2;

        private readonly MemorySettings _settings;
        private readonly ConcurrentDictionary<Type, object> _typedPools = new();
        private readonly ConcurrentDictionary<object, Type> _activeObjects = new();
        private readonly Dictionary<string, object> _cache = new();
        private readonly LinkedList<string> _cacheOrder = new();
        private readonly object _cacheLock = new();
        private float _cleanupTimer;

        public PoolMetrics this[Type type] => _typedPools.TryGetValue(type, out var pool) && pool is ObjectPool<object> objPool
            ? new PoolMetrics(objPool.TotalCreated, objPool.CurrentActive, objPool.Objects.Count, objPool.LastAccess, type)
            : default;

        public T GetCached<T>(string key) {
            lock (_cacheLock) {
                if (_cache.TryGetValue(key, out var entry) && entry is CacheEntry<T> typedEntry) {
                    var updated = typedEntry.WithAccess(DateTime.UtcNow);
                    _cache[key] = updated;
                    
                    _cacheOrder.Remove(updated.OrderNode);
                    var newNode = _cacheOrder.AddLast(key);
                    _cache[key] = new CacheEntry<T>(updated.Value, updated.AccessTime, updated.AccessCount, newNode);
                    
                    return updated.Value;
                }
                return default;
            }
        }

        public bool HasCached(string key) {
            lock (_cacheLock) return _cache.ContainsKey(key);
        }
        #endregion

        #region Constructors & Initialization
        public MemoryManager() : this(new MemorySettings()) { }
        
        public MemoryManager(MemorySettings settings) => _settings = settings;

        public async Task InitializeAsync() {
            if (IsInitialized) return;
            
            await PreloadCommonPools();
            ConfigureGarbageCollection();
            
            IsInitialized = true;
        }

        private async Task PreloadCommonPools() {
            RegisterPool(() => new List<object>(), list => list.Clear());
            RegisterPool(() => new Dictionary<string, object>(), dict => dict.Clear());
            RegisterPool(() => new StringBuilder(), sb => sb.Clear());
            RegisterPool(() => new Queue<object>(), queue => queue.Clear());
            RegisterPool(() => new Stack<object>(), stack => stack.Clear());

            await Task.Run(() => {
                foreach (var poolObj in _typedPools.Values) {
                    if (poolObj.GetType().IsGenericType) {
                        var getOrCreateMethod = poolObj.GetType().GetMethod(nameof(ObjectPool<object>.GetOrCreate));
                        for (int i = 0; i < _settings.PreloadPoolSize; i++)
                            getOrCreateMethod?.Invoke(poolObj, null);
                    }
                }
            });
        }

        private void ConfigureGarbageCollection() {
            if (Application.isMobilePlatform)
                GC.Collect(0, GCCollectionMode.Optimized);
        }
        #endregion

        #region Advanced Pool Management
        public void RegisterPool<T>(Func<T> factory, Action<T> reset = null) where T : class {
            var pool = new ObjectPool<T>(factory, reset);
            _typedPools.TryAdd(typeof(T), pool);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>() where T : class {
            if (!_typedPools.TryGetValue(typeof(T), out var poolObj) || poolObj is not ObjectPool<T> pool)
                throw new InvalidOperationException($"No pool registered for {typeof(T).Name}");

            var obj = pool.GetOrCreate();
            _activeObjects.TryAdd(obj, typeof(T));
            return obj;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return<T>(T obj) where T : class {
            if (obj == null || !_activeObjects.TryRemove(obj, out var type)) return;
            
            if (_typedPools.TryGetValue(type, out var poolObj) && poolObj is ObjectPool<T> pool)
                pool.TryReturn(obj, _settings.MaxPoolSize);
        }

        public async void ReturnDelayed<T>(T obj, float delay) where T : class {
            if (delay <= 0f) {
                Return(obj);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(delay));
            Return(obj);
        }

        public async Task<T> GetAsync<T>() where T : class => await Task.FromResult(Get<T>());
        
        public void RegisterBulkPool<T>(Func<T> factory, int preloadCount, Action<T> reset = null) where T : class {
            RegisterPool(factory, reset);
            for (int i = 0; i < preloadCount; i++) Return(Get<T>());
        }
        #endregion

        #region Smart Cache System
        public void Cache<T>(string key, T value) {
            lock (_cacheLock) {
                if (_cache.Count >= _settings.MaxCacheSize) EvictLeastRecentlyUsed();

                if (_cache.TryGetValue(key, out var existing) && existing is CacheEntry<T> existingEntry) {
                    _cacheOrder.Remove(existingEntry.OrderNode);
                    var newNode = _cacheOrder.AddLast(key);
                    _cache[key] = new CacheEntry<T>(value, DateTime.UtcNow, existingEntry.AccessCount + 1, newNode);
                } else {
                    var node = _cacheOrder.AddLast(key);
                    _cache[key] = new CacheEntry<T>(value, DateTime.UtcNow, 1, node);
                }
            }
        }

        public async Task<T> GetCachedAsync<T>(string key, Func<Task<T>> factory) {
            if (HasCached(key)) return GetCached<T>(key);
            
            var value = await factory();
            Cache(key, value);
            return value;
        }

        public void ClearCache() {
            lock (_cacheLock) {
                _cache.Clear();
                _cacheOrder.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EvictLeastRecentlyUsed() {
            if (_cacheOrder.Count == 0) return;
            
            var lruKey = _cacheOrder.First.Value;
            _cache.Remove(lruKey);
            _cacheOrder.RemoveFirst();
        }

        public void EvictCacheByPattern(Func<string, bool> predicate) {
            lock (_cacheLock) {
                var keysToRemove = _cache.Keys.Where(predicate).ToArray();
                foreach (var key in keysToRemove) {
                    if (_cache.TryGetValue(key, out var entry)) {
                        if (entry.GetType().IsGenericType && entry.GetType().GetGenericTypeDefinition() == typeof(CacheEntry<>)) {
                            var orderNodeProperty = entry.GetType().GetProperty(nameof(CacheEntry<object>.OrderNode));
                            if (orderNodeProperty?.GetValue(entry) is LinkedListNode<string> orderNode)
                                _cacheOrder.Remove(orderNode);
                        }
                        _cache.Remove(key);
                    }
                }
            }
        }
        #endregion

        #region Memory Profiling & Analysis
        public MemoryProfile GetMemoryProfile() => new(
            TotalManagedMemory / (1024 * 1024), TotalAllocatedMemory / (1024 * 1024),
            _activeObjects.Count, _typedPools.Count, _cache.Count,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)
        );

        public Dictionary<Type, PoolMetrics> GetPoolMetrics() =>
            _typedPools.ToDictionary(kvp => kvp.Key, kvp => this[kvp.Key]);

        public async Task<MemoryProfile> GetDetailedMemoryProfileAsync() {
            return await Task.Run(() => {
                GC.Collect(); 
                GC.WaitForPendingFinalizers();
                return GetMemoryProfile();
            });
        }

        public void LogMemoryStats() {
            var profile = GetMemoryProfile();
            Debug.Log($"[MemoryManager] {profile}");
            
            if (_settings.EnableProfiling) {
                var pools = GetPoolMetrics().Values.OrderByDescending(p => p.CurrentActive).Take(5);
                foreach (var pool in pools) Debug.Log($"[MemoryManager] {pool}");
            }
        }
        #endregion

        #region Advanced Garbage Collection
        public void ForceGarbageCollection() {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public async Task OptimizeMemoryAsync() {
            await Task.Run(() => {
                CleanupUnusedPools();
                CleanupCache();
                ForceGarbageCollection();
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CleanupUnusedPools() {
            var threshold = DateTime.UtcNow.AddMinutes(-5);
            var toRemove = _typedPools.Where(kvp => {
                if (kvp.Value.GetType().IsGenericType) {
                    var lastAccessProperty = kvp.Value.GetType().GetProperty(nameof(ObjectPool<object>.LastAccess));
                    var currentActiveProperty = kvp.Value.GetType().GetProperty(nameof(ObjectPool<object>.CurrentActive));
                    
                    var lastAccess = (DateTime)(lastAccessProperty?.GetValue(kvp.Value) ?? DateTime.UtcNow);
                    var currentActive = (int)(currentActiveProperty?.GetValue(kvp.Value) ?? 1);
                    
                    return lastAccess < threshold && currentActive == 0;
                }
                return false;
            }).Select(kvp => kvp.Key).ToArray();

            foreach (var type in toRemove) _typedPools.TryRemove(type, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CleanupCache() {
            lock (_cacheLock) {
                var threshold = DateTime.UtcNow.AddMinutes(-10);

                var keysToRemove = _cache.Where(kvp => {
                    if (kvp.Value.GetType().IsGenericType && kvp.Value.GetType().GetGenericTypeDefinition() == typeof(CacheEntry<>)) {
                        var accessTimeProperty = kvp.Value.GetType().GetProperty(nameof(CacheEntry<object>.AccessTime));
                        if (accessTimeProperty?.GetValue(kvp.Value) is DateTime accessTime)
                            return accessTime < threshold;
                    }
                    return false;
                }).Select(kvp => kvp.Key).ToArray();

                foreach (var key in keysToRemove) {
                    if (_cache.TryGetValue(key, out var entry)) {
                        if (entry.GetType().IsGenericType && entry.GetType().GetGenericTypeDefinition() == typeof(CacheEntry<>)) {
                            var orderNodeProperty = entry.GetType().GetProperty(nameof(CacheEntry<object>.OrderNode));
                            if (orderNodeProperty?.GetValue(entry) is LinkedListNode<string> orderNode)
                                _cacheOrder.Remove(orderNode);
                        }
                        _cache.Remove(key);
                    }
                }
            }
        }
        #endregion

        #region Standalone Implementation
        public void Initialize() => _ = InitializeAsync();

        public void Update(float deltaTime) {
            _cleanupTimer += deltaTime;
            
            if (_cleanupTimer >= _settings.CleanupIntervalSeconds) {
                _cleanupTimer = 0f;
                _ = Task.Run(() => { CleanupUnusedPools(); CleanupCache(); });
            }

            if (TotalAllocatedMemory / (1024 * 1024) > _settings.GCThresholdMB)
                ForceGarbageCollection();
        }
        #endregion

        #region Dispose & Utilities
        public void Dispose() {
            ClearCache();

            _typedPools.Clear();
            _activeObjects.Clear();
        }

        public void SetMaxCacheSize(int size) {
            lock (_cacheLock) {
                for (; _cache.Count > size ;) EvictLeastRecentlyUsed();
            }
        }

        public async Task WarmupPoolsAsync<T>(int count) where T : class {
            await Task.Run(() => {
                var items = new T[count];

                if (!_typedPools.TryGetValue(typeof(T), out var poolObj) || poolObj is not ObjectPool<T> pool)
                    throw new InvalidOperationException($"No pool registered for {typeof(T).Name}");

                for (int i = 0; i < count; i++) items[i] = Get<T>();
                for (int i = 0; i < count; i++) Return(items[i]);
            });
        }

        public void FlushPool<T>() where T : class {
            if (_typedPools.TryGetValue(typeof(T), out var poolObj) && poolObj is ObjectPool<T> pool) {
                while (pool.Objects.TryDequeue(out _)) { }
            }
        }
        #endregion
    }
}
