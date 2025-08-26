using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Tasks;

namespace com.ImmersizeFramework.Loader {
    public enum LoadableType : byte { Scene, Prefab, Texture, Audio, Material, Mesh, Animation, Video, Data, Script }
    public enum LoadPriority : byte { Critical = 5, High = 4, Normal = 3, Low = 2, Background = 1 }
    public enum LoadState : byte { Unloaded, Loading, Loaded, Failed, Cached }
    
    [Flags]
    public enum LoadableFlags : byte { 
        None = 0, Persistent = 1, Preload = 2, StreamingAsset = 4, 
        Compressed = 8, Encrypted = 16, NetworkAsset = 32 
    }
    
    public readonly struct LoaderConfig {
        public readonly LoadPriority Priority;
        public readonly byte Transition;
        public readonly bool Parallel, Cache, PreferCache, AsyncMode;
        public readonly float Timeout;
        public readonly LoadableFlags Flags;
        
        public LoaderConfig(LoadPriority priority = LoadPriority.Normal, byte transition = 1, 
            bool parallel = true, bool cache = true, bool preferCache = false, 
            bool asyncMode = true, float timeout = 30f, LoadableFlags flags = LoadableFlags.None) 
            => (Priority, Transition, Parallel, Cache, PreferCache, AsyncMode, Timeout, Flags) = 
               (priority, transition, parallel, cache, preferCache, asyncMode, timeout, flags);
        
        public static LoaderConfig Default => new();
        public static LoaderConfig Fast => new(LoadPriority.High, 0, true, true, true, true, 10f);
        public static LoaderConfig Background => new(LoadPriority.Background, 0, true, true, false, true, 60f);
        public static LoaderConfig Critical => new(LoadPriority.Critical, 1, false, true, false, false, 5f, LoadableFlags.Persistent);
        public static LoaderConfig Streaming => new(LoadPriority.Normal, 0, true, false, false, true, 120f, LoadableFlags.StreamingAsset);
    }

    public readonly struct LoadableItem {
        public readonly string Name, Path, Description;
        public readonly LoadableType Type;
        public readonly LoadState State;
        public readonly LoadPriority Priority;
        public readonly LoadableFlags Flags;
        public readonly string[] Roles, Tags, Dependencies;
        public readonly long Size, CompressedSize;
        public readonly DateTime LastAccess, RegisterTime;
        public readonly float LoadTime;
        public readonly int AccessCount;
        
        public LoadableItem(string name, string path, LoadableType type, LoadState state = LoadState.Unloaded, 
            LoadPriority priority = LoadPriority.Normal, LoadableFlags flags = LoadableFlags.None,
            string[] roles = null, string[] tags = null, string[] dependencies = null, 
            long size = 0, long compressedSize = 0, string description = null) 
            => (Name, Path, Description, Type, State, Priority, Flags, Roles, Tags, Dependencies, 
                Size, CompressedSize, LastAccess, RegisterTime, LoadTime, AccessCount) = 
               (name, path, description ?? name, type, state, priority, flags,
                roles ?? Array.Empty<string>(), tags ?? Array.Empty<string>(), 
                dependencies ?? Array.Empty<string>(), size, compressedSize > 0 ? compressedSize : size, 
                DateTime.UtcNow, DateTime.UtcNow, 0f, 0);
        
        public bool HasRole(string role) => Roles.Length == 0 || Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
        public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        public bool HasFlag(LoadableFlags flag) => (Flags & flag) == flag;
        public bool IsValid => !string.IsNullOrEmpty(Name);
        public bool IsLoaded => State == LoadState.Loaded || State == LoadState.Cached;
        public bool IsLoading => State == LoadState.Loading;
        public bool IsFailed => State == LoadState.Failed;
        public bool IsPersistent => HasFlag(LoadableFlags.Persistent);
        public bool IsPreloadable => HasFlag(LoadableFlags.Preload);
        public bool IsCompressed => HasFlag(LoadableFlags.Compressed);
        public bool IsStreamingAsset => HasFlag(LoadableFlags.StreamingAsset);
        public string Category => Type.ToString();
        public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);
        public float CompressionRatio => Size > 0 ? (float)CompressedSize / Size : 1f;
        public string StatusInfo => $"{State} | {Size / 1024f:F1}KB | {AccessCount} accesses";
        
        public LoadableItem WithState(LoadState newState) => new(Name, Path, Type, newState, Priority, Flags, 
            Roles, Tags, Dependencies, Size, CompressedSize, Description);
        
        public LoadableItem WithAccessUpdate() => new(Name, Path, Type, State, Priority, Flags, 
            Roles, Tags, Dependencies, Size, CompressedSize, Description);
    }

    public readonly struct LoadRegistry {
        readonly ConcurrentDictionary<string, LoadableItem> _items;
        readonly ConcurrentDictionary<LoadableType, string[]> _typeCache;
        readonly ConcurrentDictionary<string, string[]> _roleCache;
        readonly ConcurrentDictionary<string, string[]> _tagCache;
        readonly ConcurrentDictionary<LoadPriority, string[]> _priorityCache;
        readonly ConcurrentDictionary<LoadableFlags, string[]> _flagsCache;
        
        public LoadRegistry(bool initialize = true) =>
            (_items, _typeCache, _roleCache, _tagCache, _priorityCache, _flagsCache) = (
                new ConcurrentDictionary<string, LoadableItem>(),
                new ConcurrentDictionary<LoadableType, string[]>(),
                new ConcurrentDictionary<string, string[]>(),
                new ConcurrentDictionary<string, string[]>(),
                new ConcurrentDictionary<LoadPriority, string[]>(),
                new ConcurrentDictionary<LoadableFlags, string[]>()
            );

        public LoadableItem this[string name] => _items.TryGetValue(name, out var item) ? item : default;
        public string[] All => _items.Keys.ToArray();
        public LoadableItem[] AllItems => _items.Values.ToArray();
        public int Count => _items.Count;
        public long TotalSize => _items.Values.Sum(i => i.Size);
        public long TotalCompressedSize => _items.Values.Sum(i => i.CompressedSize);
        public int LoadedCount => _items.Values.Count(i => i.IsLoaded);
        public int LoadingCount => _items.Values.Count(i => i.IsLoading);
        public int FailedCount => _items.Values.Count(i => i.IsFailed);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Register(LoadableItem item) {
            if (!item.IsValid) return false;
            
            _items[item.Name] = item;
            InvalidateCaches();
            return true;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool UpdateState(string name, LoadState state) {
            if (_items.TryGetValue(name, out var item)) {
                _items[name] = item.WithState(state);
                return true;
            }
            return false;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string[] GetByType(LoadableType type) {
            var items = _items;
            var typeCache = _typeCache;
            return typeCache.GetOrAdd(type, t => items.Values.Where(i => i.Type == t).Select(i => i.Name).ToArray());
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string[] GetByRole(string role) {
            var items = _items;
            var roleCache = _roleCache;
            return roleCache.GetOrAdd(role, r => items.Values.Where(i => i.HasRole(r)).Select(i => i.Name).ToArray());
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string[] GetByTag(string tag) {
            var items = _items;
            var tagCache = _tagCache;
            return tagCache.GetOrAdd(tag, t => items.Values.Where(i => i.HasTag(t)).Select(i => i.Name).ToArray());
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string[] GetByPriority(LoadPriority priority) {
            var items = _items;
            var priorityCache = _priorityCache;
            return priorityCache.GetOrAdd(priority, p => items.Values.Where(i => i.Priority == p).Select(i => i.Name).ToArray());
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string[] GetByFlags(LoadableFlags flags) {
            var items = _items;
            var flagsCache = _flagsCache;
            return flagsCache.GetOrAdd(flags, f => items.Values.Where(i => i.HasFlag(f)).Select(i => i.Name).ToArray());
        }
        
        public LoadableItem[] Search(string query, LoadableType? type = null, LoadPriority? priority = null) {
            if (string.IsNullOrEmpty(query)) return Array.Empty<LoadableItem>();
            
            return _items.Values.Where(i => 
                (i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                 i.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 i.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))) &&
                (type == null || i.Type == type) &&
                (priority == null || i.Priority == priority)).ToArray();
        }
        
        public Dictionary<LoadableType, int> GetStatistics() => _items.Values
            .GroupBy(i => i.Type)
            .ToDictionary(g => g.Key, g => g.Count());
        
        public Dictionary<LoadState, int> GetStateStatistics() => _items.Values
            .GroupBy(i => i.State)
            .ToDictionary(g => g.Key, g => g.Count());
        
        public LoadableItem[] GetPreloadable() => _items.Values.Where(i => i.IsPreloadable).ToArray();
        public LoadableItem[] GetPersistent() => _items.Values.Where(i => i.IsPersistent).ToArray();
        public LoadableItem[] GetFailed() => _items.Values.Where(i => i.IsFailed).ToArray();
        public LoadableItem[] GetDependencies(string itemName) {
            var item = GetItem(itemName);
            return item.Dependencies.Select(GetItem).Where(i => i.IsValid).ToArray();
        }
        
        readonly void InvalidateCaches() {
            var typeCache = _typeCache;
            var roleCache = _roleCache;
            var tagCache = _tagCache;
            var priorityCache = _priorityCache;
            var flagsCache = _flagsCache;
            
            new Action[] {
                () => typeCache.Clear(),
                () => roleCache.Clear(),
                () => tagCache.Clear(),
                () => priorityCache.Clear(),
                () => flagsCache.Clear()
            }.AsParallel().ForAll(action => action());
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        LoadableItem GetItem(string name) => _items.TryGetValue(name, out var item) ? item : default;
    }

    public sealed class LoaderCore : LoreMonoBehaviour, IFrameworkService, IFrameworkTickable, IDisposable {
        readonly struct LoadTask {
            public readonly string ID;
            public readonly Func<CancellationToken, Task<bool>> Action;
            public readonly LoadPriority Priority;
            public readonly TaskCompletionSource<bool> TCS;
            public readonly DateTime StartTime;
            
            public LoadTask(string id, Func<CancellationToken, Task<bool>> action, LoadPriority priority) =>
                (ID, Action, Priority, TCS, StartTime) = (id, action, priority, new TaskCompletionSource<bool>(), DateTime.UtcNow);
            
            public float ElapsedTime => (float)(DateTime.UtcNow - StartTime).TotalSeconds;
        }

        LoadRegistry _registry = new(true);
        readonly ConcurrentDictionary<string, LoadTask> _tasks = new();
        readonly ConcurrentQueue<LoadTask> _queue = new();
        readonly ConcurrentDictionary<string, UnityEngine.Object> _cache = new();
        readonly ConcurrentDictionary<string, float> _loadTimes = new();
        readonly ConcurrentDictionary<string, int> _accessCounts = new();
        
        CancellationTokenSource _cts = new();
        volatile bool _initialized, _loading;
        string _current;
        float _totalLoadTime;
        int _totalLoads;

        public bool IsInitialized => _initialized;
        public bool IsLoading => _loading;
        public int Priority => 1;
        public string Current => _current ??= SceneManager.GetActiveScene().name;
        public LoadableItem this[string name] => _registry[name];
        public string[] Available => _registry.All;
        public LoadableItem[] AllItems => _registry.AllItems;
        public int TotalItems => _registry.Count;
        public long TotalSize => _registry.TotalSize;
        public long TotalCompressedSize => _registry.TotalCompressedSize;
        public int LoadedCount => _registry.LoadedCount;
        public int LoadingCount => _registry.LoadingCount;
        public int FailedCount => _registry.FailedCount;
        public int CacheSize => _cache.Count;
        public float AverageLoadTime => _totalLoads > 0 ? _totalLoadTime / _totalLoads : 0f;
        public float CompressionRatio => TotalSize > 0 ? (float)TotalCompressedSize / TotalSize : 1f;
        
        public string[] GetScenes() => _registry.GetByType(LoadableType.Scene);
        public string[] GetPrefabs() => _registry.GetByType(LoadableType.Prefab);
        public string[] GetTextures() => _registry.GetByType(LoadableType.Texture);
        public string[] GetAudio() => _registry.GetByType(LoadableType.Audio);
        public string[] GetMaterials() => _registry.GetByType(LoadableType.Material);
        public string[] GetMeshes() => _registry.GetByType(LoadableType.Mesh);
        public string[] GetAnimations() => _registry.GetByType(LoadableType.Animation);
        public string[] GetVideos() => _registry.GetByType(LoadableType.Video);
        public string[] GetData() => _registry.GetByType(LoadableType.Data);
        public string[] GetScripts() => _registry.GetByType(LoadableType.Script);
        public string[] GetByRole(string role) => _registry.GetByRole(role);
        public string[] GetByTag(string tag) => _registry.GetByTag(tag);
        public string[] GetByPriority(LoadPriority priority) => _registry.GetByPriority(priority);
        public string[] GetByFlags(LoadableFlags flags) => _registry.GetByFlags(flags);
        public LoadableItem[] Search(string query, LoadableType? type = null, LoadPriority? priority = null) => _registry.Search(query, type, priority);
        public Dictionary<LoadableType, int> GetStatistics() => _registry.GetStatistics();
        public Dictionary<LoadState, int> GetStateStatistics() => _registry.GetStateStatistics();
        public LoadableItem[] GetPreloadable() => _registry.GetPreloadable();
        public LoadableItem[] GetPersistent() => _registry.GetPersistent();
        public LoadableItem[] GetFailed() => _registry.GetFailed();
        public LoadableItem[] GetDependencies(string itemName) => _registry.GetDependencies(itemName);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanAccess(string role, string item) => string.IsNullOrEmpty(role) || this[item].HasRole(role);

        public event Action<string> OnSceneChanged;
        public event Action<string, float> OnProgress;
        public event Action<LoadableItem> OnItemRegistered;
        public event Action<LoadableItem> OnItemLoaded;
        public event Action<LoadableItem> OnItemFailed;
        public event Action<string> OnCacheCleared;
        public event Action<Dictionary<LoadableType, int>> OnStatisticsUpdated;

        protected override void Awake() {
            base.Awake();
            FrameworkCore.Instance.RegisterService<LoaderCore>(this);
        }

        public async Task InitializeAsync() {
            if (_initialized) return;
            
            await ScanAndRegisterAssets();
            await PreloadCritical();
            
            _initialized = true;
            _ = ProcessQueue();
            
            Debug.Log($"[LoaderCore] Initialized with {TotalItems} items ({TotalSize / 1024f:F1} KB)");
        }

        public void Initialize() => _ = InitializeAsync();

        [Lore("RegisterItem", "Enregistre un élément loadable avec métadonnées étendues")]
        public bool RegisterItem(string name, string path, LoadableType type, LoadPriority priority = LoadPriority.Normal,
            LoadableFlags flags = LoadableFlags.None, string[] roles = null, string[] tags = null, 
            string[] dependencies = null, string description = null) {
            var size = GetAssetSize(path);
            var compressedSize = flags.HasFlag(LoadableFlags.Compressed) ? size / 2 : size;
            var item = new LoadableItem(name, path, type, LoadState.Unloaded, priority, flags, 
                roles, tags, dependencies, size, compressedSize, description);
            var registered = _registry.Register(item);
            
            if (registered) {
                OnItemRegistered?.Invoke(item);
                Debug.Log($"[LoaderCore] Registered {type}: {name} ({size / 1024f:F1} KB)");
            }
            
            return registered;
        }

        [Lore("LoadScene", "Charge une scène avec validation avancée et monitoring")]
        public async Task<bool> LoadScene(string name, string role = null, LoaderConfig config = default) {
            if (!_initialized) await InitializeAsync();
            
            var item = _registry[name];
            if (!item.IsValid || item.Type != LoadableType.Scene) return false;
            if (!string.IsNullOrEmpty(role) && !item.HasRole(role)) return false;

            var dependencies = GetDependencies(name);
            foreach (var dep in dependencies) {
                if (!dep.IsLoaded) {
                    Debug.LogWarning($"[LoaderCore] Dependency '{dep.Name}' not loaded for scene '{name}'");
                    if (config.Priority == LoadPriority.Critical) {
                        await LoadAsset<UnityEngine.Object>(dep.Name, config);
                    }
                }
            }

            var taskId = $"scene_{name}_{DateTime.UtcNow.Ticks}";
            var task = new LoadTask(taskId, token => ExecuteSceneLoad(name, config, token), config.Priority);
            
            _tasks[taskId] = task;
            _queue.Enqueue(task);
            
            _registry.UpdateState(name, LoadState.Loading);
            return await task.TCS.Task;
        }

        [Lore("LoadAsset", "Charge un asset via MediaManager ou cache")]
        public async Task<T> LoadAsset<T>(string name, LoaderConfig config = default) where T : UnityEngine.Object {
            if (!_initialized) await InitializeAsync();
            
            var item = _registry[name];
            if (!item.IsValid) return null;
            
            if (_cache.TryGetValue(name, out var cached) && cached is T cachedAsset) return cachedAsset;
            
            var taskId = $"asset_{name}_{DateTime.UtcNow.Ticks}";
            var task = new LoadTask(taskId, async token => {
                var asset = Resources.Load<T>(item.Path);
                if (asset != null && config.Cache) _cache[name] = asset;
                return asset != null;
            }, config.Priority);
            
            _tasks[taskId] = task;
            _queue.Enqueue(task);
            
            await task.TCS.Task;
            return _cache.TryGetValue(name, out var result) && result is T finalAsset ? finalAsset : null;
        }

        [Lore("Configure", "Configure les permissions de rôles")]
        public void Configure(string role, params string[] items) {
            if (string.IsNullOrEmpty(role)) return;
            
            foreach (var itemName in items.Where(i => _registry[i].IsValid)) {
                var item = _registry[itemName];
                var roles = item.Roles.Concat(new[] { role }).Distinct().ToArray();
                var newItem = new LoadableItem(item.Name, item.Path, item.Type, item.State, item.Priority, item.Flags, roles, item.Tags, item.Dependencies, item.Size, item.CompressedSize, item.Description);
                _registry.Register(newItem);
            }
            
            Debug.Log($"[LoaderCore] Configured role '{role}' for {items.Length} items");
        }

        [Lore("PrintRegistry", "Affiche le registre complet avec statistiques avancées")]
        [ContextMenu("Print Full Registry")]
        public void PrintRegistry() {
            LogLore();
            
            var stats = GetStatistics();
            var stateStats = GetStateStatistics();
            
            Debug.Log("=== LOADER REGISTRY ANALYSIS ===");
            Debug.Log($"Total Items: {TotalItems} | Size: {TotalSize / (1024f * 1024f):F1} MB | Compressed: {TotalCompressedSize / (1024f * 1024f):F1} MB");
            Debug.Log($"Compression Ratio: {CompressionRatio:P1} | Cache Size: {CacheSize} | Average Load Time: {AverageLoadTime:F2}s");
            Debug.Log($"Loaded: {LoadedCount} | Loading: {LoadingCount} | Failed: {FailedCount}");
            
            Debug.Log("\n=== BY TYPE ===");
            foreach (var stat in stats.OrderByDescending(s => s.Value)) {
                var items = _registry.GetByType(stat.Key);
                var totalSize = items.Sum(i => _registry[i].Size) / (1024f * 1024f);
                Debug.Log($"{stat.Key}: {stat.Value} items ({totalSize:F1} MB)");
                
                foreach (var itemName in items.Take(3)) {
                    var item = _registry[itemName];
                    Debug.Log($"  ◦ {item.DisplayName} - {item.StatusInfo}");
                }
                if (items.Length > 3) Debug.Log($"  ... and {items.Length - 3} more");
            }
            
            Debug.Log("\n=== BY STATE ===");
            foreach (var stat in stateStats) 
                Debug.Log($"{stat.Key}: {stat.Value} items");
            
            var failed = GetFailed();
            if (failed.Length > 0) {
                Debug.Log("\n=== FAILED ITEMS ===");
                foreach (var item in failed.Take(5)) 
                    Debug.Log($"✗ {item.DisplayName} ({item.Type})");
            }
            
            var preloadable = GetPreloadable();
            if (preloadable.Length > 0) {
                Debug.Log($"\n=== PRELOADABLE ({preloadable.Length}) ===");
                foreach (var item in preloadable.Take(5)) 
                    Debug.Log($"⚡ {item.DisplayName} - {item.Priority}");
            }
            
            Debug.Log("=== END REGISTRY ANALYSIS ===");
        }

        [Lore("AnalyzePerformance", "Analyse les performances de chargement")]
        [ContextMenu("Analyze Performance")]
        public void AnalyzePerformance() {
            LogLore();
            
            Debug.Log("=== PERFORMANCE ANALYSIS ===");
            Debug.Log($"Total Loads: {_totalLoads} | Total Time: {_totalLoadTime:F2}s | Average: {AverageLoadTime:F2}s");
            
            var slowItems = _loadTimes.Where(kv => kv.Value > 1f).OrderByDescending(kv => kv.Value).Take(10);
            Debug.Log("\n=== SLOWEST LOADS ===");
            foreach (var item in slowItems) {
                var loadableItem = _registry[item.Key];
                Debug.Log($"🐌 {loadableItem.DisplayName}: {item.Value:F2}s ({loadableItem.Size / 1024f:F1} KB)");
            }
            
            var frequentItems = _accessCounts.Where(kv => kv.Value > 5).OrderByDescending(kv => kv.Value).Take(10);
            Debug.Log("\n=== MOST ACCESSED ===");
            foreach (var item in frequentItems) {
                var loadableItem = _registry[item.Key];
                Debug.Log($"🔥 {loadableItem.DisplayName}: {item.Value} accesses");
            }
            
            Debug.Log($"\n=== MEMORY USAGE ===");
            Debug.Log($"Cache Memory: {System.GC.GetTotalMemory(false) / (1024f * 1024f):F1} MB");
            
            Debug.Log("=== END PERFORMANCE ANALYSIS ===");
        }

        [Lore("OptimizeCache", "Optimise le cache en supprimant les éléments peu utilisés")]
        [ContextMenu("Optimize Cache")]
        public void OptimizeCache() {
            LogLore();
            
            var itemsToRemove = new List<string>();
            var currentTime = DateTime.UtcNow;
            
            foreach (var cacheItem in _cache.ToArray()) {
                var item = _registry[cacheItem.Key];
                var accessCount = _accessCounts.GetValueOrDefault(cacheItem.Key, 0);
                var timeSinceAccess = (currentTime - item.LastAccess).TotalMinutes;
                
                if (accessCount < 3 && timeSinceAccess > 30 && !item.IsPersistent)
                    itemsToRemove.Add(cacheItem.Key);
            }
            
            foreach (var itemName in itemsToRemove) {
                _cache.TryRemove(itemName, out _);
                _accessCounts.TryRemove(itemName, out _);
            }
            
            Debug.Log($"[LoaderCore] Cache optimized: removed {itemsToRemove.Count} items");
            OnCacheCleared?.Invoke($"Optimized: {itemsToRemove.Count} items removed");
            
            System.GC.Collect();
        }

        [Lore("ExportRegistry", "Exporte le registre pour analyse externe")]
        [ContextMenu("Export Registry")]
        public void ExportRegistry() {
            LogLore();
            
            var exportData = new {
                timestamp = DateTime.UtcNow,
                totalItems = TotalItems,
                totalSize = TotalSize,
                compressionRatio = CompressionRatio,
                averageLoadTime = AverageLoadTime,
                statistics = GetStatistics(),
                stateStatistics = GetStateStatistics(),
                items = AllItems.Select(i => new {
                    i.Name,
                    i.Type,
                    i.State,
                    i.Priority,
                    i.Size,
                    i.CompressionRatio,
                    accessCount = _accessCounts.GetValueOrDefault(i.Name, 0),
                    loadTime = _loadTimes.GetValueOrDefault(i.Name, 0f)
                }).ToArray()
            };
            
            var json = JsonUtility.ToJson(exportData, true);
            System.IO.File.WriteAllText($"LoaderRegistry_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json", json);
            
            Debug.Log($"[LoaderCore] Registry exported with {TotalItems} items");
        }

        [Lore("BenchmarkLoading", "Lance un benchmark de chargement")]
        [ContextMenu("Benchmark Loading")]
        public async void BenchmarkLoading() {
            LogLore();
            
            Debug.Log("=== LOADING BENCHMARK START ===");
            var startTime = Time.realtimeSinceStartup;
            
            var testItems = GetScenes().Concat(GetPrefabs()).Take(10).ToArray();
            var results = new List<(string name, float time, bool success)>();
            
            foreach (var itemName in testItems) {
                var itemStart = Time.realtimeSinceStartup;
                try {
                    var success = await LoadAsset<UnityEngine.Object>(itemName, LoaderConfig.Fast);
                    var itemTime = Time.realtimeSinceStartup - itemStart;
                    results.Add((itemName, itemTime, success != null));
                } catch {
                    var itemTime = Time.realtimeSinceStartup - itemStart;
                    results.Add((itemName, itemTime, false));
                }
            }
            
            var totalTime = Time.realtimeSinceStartup - startTime;
            var successCount = results.Count(r => r.success);
            
            Debug.Log($"=== BENCHMARK RESULTS ===");
            Debug.Log($"Total Time: {totalTime:F2}s | Success Rate: {successCount}/{testItems.Length} ({successCount * 100f / testItems.Length:F1}%)");
            
            foreach (var result in results.OrderByDescending(r => r.time)) {
                var status = result.success ? "✓" : "✗";
                Debug.Log($"{status} {result.name}: {result.time:F3}s");
            }
            
            Debug.Log("=== BENCHMARK END ===");
        }

        [Lore("ListScenes", "Liste toutes les scènes disponibles")]
        [ContextMenu("List Scenes")]
        public void ListScenes() {
            LogLore();
            var scenes = GetScenes();
            Debug.Log($"=== AVAILABLE SCENES ({scenes.Length}) ===");
            foreach (var sceneName in scenes) {
                var scene = _registry[sceneName];
                var rolesStr = scene.Roles.Length > 0 ? $" [Roles: {string.Join(", ", scene.Roles)}]" : "";
                var tagsStr = scene.Tags.Length > 0 ? $" [Tags: {string.Join(", ", scene.Tags)}]" : "";
                Debug.Log($"• {scene.DisplayName}{rolesStr}{tagsStr}");
            }
        }

        [Lore("ListAssetsByType", "Liste les assets par type")]
        public void ListAssetsByType(LoadableType type) {
            LogLore();
            var items = _registry.GetByType(type);
            Debug.Log($"=== {type.ToString().ToUpper()} ASSETS ({items.Length}) ===");
            foreach (var itemName in items) {
                var item = _registry[itemName];
                Debug.Log($"• {item.DisplayName} ({item.Size / 1024f:F1} KB)");
            }
        }

        async Task ScanAndRegisterAssets() {
            await RegisterScenes();
            await RegisterPrefabs();
            await RegisterTextures();
            await RegisterAudio();
        }

        async Task RegisterScenes() {
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++) {
                var path = SceneUtility.GetScenePathByBuildIndex(i);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                RegisterItem(name, path, LoadableType.Scene, LoadPriority.Normal, LoadableFlags.None, new[] { "scene", "level" });
            }
        }

        async Task RegisterPrefabs() {
            try {
                var prefabs = Resources.LoadAll<GameObject>("");
                foreach (var prefab in prefabs) {
                    if (prefab != null)
                        RegisterItem(prefab.name, $"Prefabs/{prefab.name}", LoadableType.Prefab, LoadPriority.Normal, LoadableFlags.None, null, new[] { "prefab", "gameobject" });
                }
            } catch { }
        }

        async Task RegisterTextures() {
            try {
                var textures = Resources.LoadAll<Texture2D>("");
                foreach (var texture in textures) {
                    if (texture != null)
                        RegisterItem(texture.name, $"Textures/{texture.name}", LoadableType.Texture, LoadPriority.Normal, LoadableFlags.None, null, new[] { "texture", "image" });
                }
            } catch { }
        }

        async Task RegisterAudio() {
            try {
                var clips = Resources.LoadAll<AudioClip>("");
                foreach (var clip in clips) {
                    if (clip != null)
                        RegisterItem(clip.name, $"Audio/{clip.name}", LoadableType.Audio, LoadPriority.Normal, LoadableFlags.None, null, new[] { "audio", "sound" });
                }
            } catch { }
        }

        long GetAssetSize(string path) {
            try {
#if UNITY_EDITOR
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                return asset != null ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(asset) : 0;
#else
                return 0;
#endif
            } catch { return 0; }
        }

        async Task PreloadCritical() {
            var critical = new[] { "UI/LoadingScreen", "Effects/Transition" };
            foreach (var asset in critical) {
                try { await LoadAsset<GameObject>(asset, LoaderConfig.Background); }
                catch { }
            }
        }

        async Task ProcessQueue() {
            while (!_cts.Token.IsCancellationRequested) {
                if (_queue.TryDequeue(out var task)) {
                    _ = ExecuteTask(task);
                    await Task.Delay(16, _cts.Token);
                } else await Task.Delay(100, _cts.Token);
            }
        }

        async Task ExecuteTask(LoadTask task) {
            try {
                _loading = true;
                var result = await task.Action(_cts.Token);
                _tasks.TryRemove(task.ID, out _);
                task.TCS.SetResult(result);
            } catch (Exception ex) {
                task.TCS.SetException(ex);
            } finally {
                _loading = _tasks.Count > 0;
            }
        }

        async Task<bool> ExecuteSceneLoad(string name, LoaderConfig config, CancellationToken token) {
            try {
                OnProgress?.Invoke(name, 0f);
                
                if (config.Transition > 0) await Transition(true);
                
                var operation = SceneManager.LoadSceneAsync(name);
                operation.allowSceneActivation = false;
                
                while (operation.progress < 0.9f && !token.IsCancellationRequested) {
                    OnProgress?.Invoke(name, operation.progress);
                    await Task.Yield();
                }
                
                operation.allowSceneActivation = true;
                await Task.Yield();
                
                if (config.Transition > 0) await Transition(false);
                
                _current = name;
                OnSceneChanged?.Invoke(name);
                OnProgress?.Invoke(name, 1f);
                
                return true;
            } catch {
                return false;
            }
        }

        async Task Transition(bool fadeIn) => await Task.Delay(fadeIn ? 250 : 500, _cts.Token);

        [ContextMenu("Load First Scene")]
        void LoadFirstScene() => _ = LoadScene(GetScenes().FirstOrDefault());

        [ContextMenu("Configure Admin")]
        void ConfigureAdmin() => Configure("admin", Available);

        [ContextMenu("Configure User")]
        void ConfigureUser() => Configure("user", Available.Where(s => !s.Contains("admin", StringComparison.OrdinalIgnoreCase)).ToArray());

        [ContextMenu("Clear Cache")]
        void ClearCache() {
            _cache.Clear();
            Debug.Log("[LoaderCore] Cache cleared");
        }

        public void Tick(float deltaTime) {
            if (_loading && _tasks.Count == 0) _loading = false;
        }

        public void FixedTick(float deltaTime) { }

        public void LateTick(float deltaTime) {
            if (Time.frameCount % 300 == 0) {
                var memoryPressure = System.GC.GetTotalMemory(false) / (1024f * 1024f * 1024f);
                if (memoryPressure > 0.8f) OptimizeCache();
            }
        }

        public void Dispose() {
            _cts?.Cancel();
            _cts?.Dispose();
            _cache.Clear();
            _tasks.Clear();
        }

        void OnDestroy() => Dispose();
    }

    public static class LoaderExtensions {
        static LoaderCore _core => FrameworkCore.Instance?.GetService<LoaderCore>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Task<bool> LoadScene(this string scene, string role = null) => _core?.LoadScene(scene, role) ?? Task.FromResult(false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Task<T> LoadAsset<T>(this string name) where T : UnityEngine.Object => _core?.LoadAsset<T>(name) ?? Task.FromResult<T>(null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CanAccess(this string item, string role) => _core?.CanAccess(role, item) ?? false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string[] GetByRole(this string role) => _core?.GetByRole(role) ?? Array.Empty<string>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string[] GetByTag(this string tag) => _core?.GetByTag(tag) ?? Array.Empty<string>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LoadableItem[] Search(this string query) => _core?.Search(query) ?? Array.Empty<LoadableItem>();
    }
}
