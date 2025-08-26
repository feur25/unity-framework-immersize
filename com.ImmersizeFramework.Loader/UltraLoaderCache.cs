using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Loader {
    
    public sealed class UltraLoaderCache : IDisposable {
        
        readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();
        readonly ConcurrentDictionary<string, WeakReference<UnityEngine.Object>> _weakRefs = new();
        readonly Timer _cleanupTimer;
        readonly SemaphoreSlim _semaphore = new(1, 1);
        
        volatile bool _disposed;
        long _hits, _misses;
        
        public int Count => _cache.Count;
        public long TotalMemory => _cache.Values.AsParallel().Sum(e => e.Size);
        public float HitRatio { get; private set; }
        
        public UltraLoaderCache(TimeSpan cleanupInterval = default) {
            var interval = cleanupInterval == default ? TimeSpan.FromMinutes(5) : cleanupInterval;
            _cleanupTimer = new Timer(CleanupCallback, null, (int)interval.TotalMilliseconds, (int)interval.TotalMilliseconds);
        }
        
        public TValue GetValue<TValue>(string key) where TValue : class => 
            TryGet<TValue>(key, out var result) ? result : default;
        
        public void SetValue<TValue>(string key, TValue value) where TValue : class => _ = Set(key, value);
        
        public TValue GetValueByType<TValue>(string key, LoadableType type) where TValue : class {
            var cacheKey = new CacheKey(key, typeof(TValue));
            if (!_cache.TryGetValue(cacheKey, out var entry) || !(entry.Value is TValue result)) {
                Interlocked.Increment(ref _misses);
                UpdateHitRatio();
                
                return default;
            }
            
            Interlocked.Increment(ref _hits);
            entry.UpdateAccess();

            return result;
        }
        
        public async Task<TValue> GetOrCreateAsync<TValue>(string key, Func<Task<TValue>> factory, TimeSpan? expiry = null) 
            where TValue : class {
            
            if (TryGet<TValue>(key, out var cached)) return cached;
            
            await _semaphore.WaitAsync();
            try {
                if (TryGet<TValue>(key, out cached)) return cached;
                var value = await factory();
                await Set(key, value, expiry);

                return value;
            } finally {
                _semaphore.Release();
            }
        }
        
        public bool TryGet<TValue>(string key, out TValue result) where TValue : class {
            result = default;
            var cacheKey = new CacheKey(key, typeof(TValue));
            
            if (!_cache.TryGetValue(cacheKey, out var entry)) {
                RecordMiss();

                return false;
            }
            
            if (entry.IsExpired) {
                _ = _cache.TryRemove(cacheKey, out _);
                RecordMiss();

                return false;
            }
            
            if (entry.Value is TValue value)
            {
                result = value;
                RecordHit();
                entry.UpdateAccess();

                return true;
            }
            
            RecordMiss();
            return false;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RecordHit() {
            Interlocked.Increment(ref _hits);
            UpdateHitRatio();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RecordMiss() {
            Interlocked.Increment(ref _misses);
            UpdateHitRatio();
        }
        
        public async Task<bool> Set<TValue>(string key, TValue value, TimeSpan? expiry = null) where TValue : class {
            if (_disposed) return false;
            
            var entry = new CacheEntry(value, CalculateSize(value), expiry);
            var cacheKey = new CacheKey(key, typeof(TValue));
            
            _cache.AddOrUpdate(cacheKey, entry, (_, _) => entry);
            
            if (value is UnityEngine.Object unityObj)
                _weakRefs.AddOrUpdate(key, new WeakReference<UnityEngine.Object>(unityObj), 
                    (_, _) => new WeakReference<UnityEngine.Object>(unityObj));
            
            await PerformMemoryCheck();
            return true;
        }
        
        public bool Remove(string key) =>
            _cache.Keys
                .Where(k => k.Key == key)
                .Aggregate(false, (removed, k) => _cache.TryRemove(k, out _) || removed);
        
        public void Clear() => new Action(() => {
            _cache.Clear();
            _weakRefs.Clear();
            _hits = _misses = 0;
            HitRatio = 0f;
        })();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void UpdateHitRatio() {
            var total = _hits + _misses;
            HitRatio = total > 0 ? (float)_hits / total : 0f;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long CalculateSize<TValue>(TValue value) => value switch {
            string s => s.Length * 2L,
            byte[] bytes => bytes.LongLength,
            Texture2D tex => tex.width * tex.height * 4L,
            Mesh mesh => mesh.vertexCount * 32L,
            AudioClip audio => audio.samples * audio.channels * 4L,
            _ => 1024L
        };
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        async Task PerformMemoryCheck() {
            if (GC.GetTotalMemory(false) > 512 * 1024 * 1024) 
                await CheckMemoryPressure();
        }
        
        async Task CheckMemoryPressure() {
            const long maxMemory = 500 * 1024 * 1024;
            
            if (TotalMemory <= maxMemory) return;
            
            await _semaphore.WaitAsync();
            try {
                var toRemove = _cache
                    .OrderBy(kvp => kvp.Value.LastAccess)
                    .Take(_cache.Count / 4)
                    .Select(kvp => kvp.Key)
                    .ToArray();
                
                toRemove.AsParallel().ForAll(key => _cache.TryRemove(key, out _));
                
                Debug.Log($"[UltraLoaderCache] Memory cleanup: removed {toRemove.Length} entries");
            } finally {
                _semaphore.Release();
            }
        }
        
        void CleanupCallback(object state) {
            if (_disposed) return;
            
            var (expired, deadRefs) = (
                _cache.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToArray(),
                _weakRefs.Where(kvp => !kvp.Value.TryGetTarget(out _)).Select(kvp => kvp.Key).ToArray()
            );
            
            expired.AsParallel().ForAll(key => _cache.TryRemove(key, out _));
            deadRefs.AsParallel().ForAll(key => _weakRefs.TryRemove(key, out _));
            
            if ((expired.Length | deadRefs.Length) > 0)
                Debug.Log($"[UltraLoaderCache] Cleanup: {expired.Length} expired, {deadRefs.Length} dead refs");
        }
        
        public void Dispose() {
            if (_disposed) return;
            
            _disposed = true;
            _weakRefs.Values.AsParallel()
                .Select(wr => wr.TryGetTarget(out var obj) ? obj : null)
                .Where(obj => obj != null && obj)
                .ToList()
                .ForEach(obj => UnityEngine.Object.DestroyImmediate(obj));
            
            new IDisposable[] { _cleanupTimer, _semaphore }.AsParallel().ForAll(disposable => disposable?.Dispose());
            Clear();
        }
        
        sealed class CacheEntry {
            public readonly object Value;
            public readonly long Size;
            public readonly DateTime? Expiry;
            
            DateTime _lastAccess;
            
            public DateTime LastAccess => _lastAccess;
            public bool IsExpired => Expiry.HasValue && DateTime.UtcNow > Expiry.Value;
            
            public CacheEntry(object value, long size, TimeSpan? expiry) =>
                (Value, Size, _lastAccess, Expiry) = (value, size, DateTime.UtcNow, 
                    expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void UpdateAccess() => _lastAccess = DateTime.UtcNow;
        }
        
        readonly struct CacheKey : IEquatable<CacheKey> {
            public readonly string Key;
            public readonly Type Type;
            readonly int _hashCode;
            
            public CacheKey(string key, Type type) => 
                (Key, Type, _hashCode) = (key, type, HashCode.Combine(key, type));
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override int GetHashCode() => _hashCode;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(CacheKey other) => Key == other.Key && Type == other.Type;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override bool Equals(object obj) => obj is CacheKey other && Equals(other);
        }
    }
    
    public static class UltraLoaderCacheExtensions {
        
        static readonly ConcurrentDictionary<LoaderCore, UltraLoaderCache> _cacheRegistry = new();
        
        public static UltraLoaderCache GetUltraCache(this LoaderCore core) =>
            _cacheRegistry.GetOrAdd(core, static _ => new UltraLoaderCache());
        
        public static async Task<TAsset> LoadWithUltraCache<TAsset>(this LoaderCore core, string itemName, 
            LoaderConfig config = default, TimeSpan? cacheExpiry = null) where TAsset : UnityEngine.Object {
            
            var cache = core.GetUltraCache();
            return await cache.GetOrCreateAsync($"{itemName}:{typeof(TAsset).Name}", 
                async () => await core.LoadAsset<TAsset>(itemName, config), cacheExpiry);
        }
    }
    
    public static class TimeSpanExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TOut Let<TOut>(this TimeSpan? value, Func<TimeSpan, TOut> selector) =>
            value.HasValue ? selector(value.Value) : default;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DateTime? Let(this TimeSpan? value, Func<TimeSpan, DateTime> selector) =>
            value.HasValue ? selector(value.Value) : null;
    }
}
