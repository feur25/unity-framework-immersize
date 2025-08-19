using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Linq;
using System.IO;
using UnityEngine.Networking;

using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Memory;

namespace com.ImmersizeFramework.Media {
    public sealed class MediaManager : IFrameworkService, IFrameworkTickable {
        #region Advanced Configuration & State
        public enum MediaType : byte { Image, Video, Audio, Model, Animation, Shader, Material, Font, UI }
        public enum LoadPriority : byte { Low, Normal, High, Critical }
        public enum CompressionFormat : byte { None, ASTC, DXT, ETC2, PVRTC }
        public enum ProcessingState : byte { Pending, Loading, Processing, Cached, Failed, Expired }

        [System.Serializable]
        public readonly struct MediaSettings {
            public readonly int MaxCacheSize, PreloadCount, MaxConcurrentLoads, TextureMaxSize;
            public readonly float CacheLifetime, PreloadDistance, QualityScale;
            public readonly bool EnableStreamLoading, EnableCompression, EnableMipMaps, AutoOptimize;
            public readonly CompressionFormat PreferredFormat;
            public readonly LoadPriority DefaultPriority;

            public MediaSettings(int maxCache = 2048, int preload = 100, int concurrent = 8, int texSize = 2048,
                               float lifetime = 300f, float distance = 50f, float quality = 1f,
                               bool streaming = true, bool compression = true, bool mips = true, bool optimize = true,
                               CompressionFormat format = CompressionFormat.ASTC, LoadPriority priority = LoadPriority.Normal) =>
                (MaxCacheSize, PreloadCount, MaxConcurrentLoads, TextureMaxSize, CacheLifetime, PreloadDistance, QualityScale,
                 EnableStreamLoading, EnableCompression, EnableMipMaps, AutoOptimize, PreferredFormat, DefaultPriority) = 
                (maxCache, preload, concurrent, texSize, lifetime, distance, quality, streaming, compression, mips, optimize, format, priority);

            public static implicit operator MediaSettings(int maxCache) => new(maxCache);
            public static implicit operator MediaSettings(float quality) => new(quality: quality);
        }

        public readonly struct MediaRequest {
            public readonly string Path, ID;
            public readonly MediaType Type;
            public readonly LoadPriority Priority;
            public readonly Vector2Int Resolution;
            public readonly DateTime RequestTime;
            public readonly Action<IMediaAsset> OnComplete;
            public readonly Action<float> OnProgress;

            public MediaRequest(string path, string id, MediaType type, LoadPriority priority,
                              Vector2Int resolution, Action<IMediaAsset> onComplete, Action<float> onProgress = null) =>
                (Path, ID, Type, Priority, Resolution, RequestTime, OnComplete, OnProgress) = 
                (path, id, type, priority, resolution, DateTime.UtcNow, onComplete, onProgress);

            public override string ToString() => $"{Type}:{ID} ({Priority}) -> {Path}";
        }

        public readonly struct MediaStatistics {
            public readonly int TotalCached, LoadingCount, FailedCount, MemoryUsageMB;
            public readonly float CacheHitRatio, AverageLoadTime;
            public readonly long TotalRequests, CacheHits, CacheMisses;

            public MediaStatistics(int cached, int loading, int failed, int memory, float hitRatio, float avgTime, long requests, long hits, long misses) =>
                (TotalCached, LoadingCount, FailedCount, MemoryUsageMB, CacheHitRatio, AverageLoadTime, TotalRequests, CacheHits, CacheMisses) = 
                (cached, loading, failed, memory, hitRatio, avgTime, requests, hits, misses);

            public override string ToString() => 
                $"Cache: {TotalCached} assets, {MemoryUsageMB}MB | Loading: {LoadingCount} | Hit Ratio: {CacheHitRatio:P1} | Avg: {AverageLoadTime:F2}ms";
        }

        private readonly struct LoadingTask {
            public readonly MediaRequest Request;
            public readonly Task<IMediaAsset> Task;
            public readonly DateTime StartTime;
            public readonly CancellationTokenSource CancelToken;

            public LoadingTask(MediaRequest request, Task<IMediaAsset> task, CancellationTokenSource token) =>
                (Request, Task, StartTime, CancelToken) = (request, task, DateTime.UtcNow, token);

            public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public MediaStatistics Statistics { get; private set; } = new();
        public int Priority => 5;

        public event Action<IMediaAsset> OnAssetLoaded;
        public event Action<string, Exception> OnLoadError;
        public event Action OnCacheCleared;

        private readonly MediaSettings _settings;
        private readonly ConcurrentDictionary<string, IMediaAsset> _cache = new();
        private readonly ConcurrentDictionary<string, LoadingTask> _loadingTasks = new();
        private readonly ConcurrentQueue<MediaRequest> _requestQueue = new();
        private readonly Dictionary<MediaType, IMediaLoader> _loaders = new();
        private readonly Queue<MediaRequest> _priorityQueue = new();
        private readonly object _queueLock = new();

        private MemoryManager _memoryManager;
        private int _activeLoads, _totalRequests, _cacheHits, _cacheMisses;
        private float _totalLoadTime, _cleanupTimer;

        public IMediaAsset this[string id] => GetCached(id);
        public MediaStatistics Stats => UpdateStatistics();
        
        public IEnumerable<IMediaAsset> GetAllCachedAssets() => _cache.Values;
        #endregion

        #region Constructors & Initialization
        public MediaManager() : this(new MediaSettings()) { }
        public MediaManager(MediaSettings settings) => _settings = settings;

        public async Task InitializeAsync() {
            if (IsInitialized) return;

            RegisterMediaLoaders();
            _memoryManager = new MemoryManager();
            await _memoryManager.InitializeAsync();

            IsInitialized = true;
            _ = StartLoadingLoop();
        }

        private void RegisterMediaLoaders() {
            _loaders[MediaType.Image] = new ImageLoader(_settings);
            _loaders[MediaType.Video] = new VideoLoader(_settings);
            _loaders[MediaType.Audio] = new AudioLoader(_settings);
            _loaders[MediaType.Model] = new ModelLoader(_settings);
            _loaders[MediaType.Animation] = new AnimationLoader(_settings);
            _loaders[MediaType.Shader] = new ShaderLoader(_settings);
            _loaders[MediaType.Material] = new MaterialLoader(_settings);
            _loaders[MediaType.Font] = new FontLoader(_settings);
            _loaders[MediaType.UI] = new UILoader(_settings);
        }

        private async Task StartLoadingLoop() {
            while (IsInitialized) {
                await ProcessLoadingQueue();
                await Task.Delay(16);
            }
        }
        #endregion

        #region Advanced Loading System
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task<T> LoadAsync<T>(string path, LoadPriority priority = LoadPriority.Normal) where T : class, IMediaAsset =>
            await LoadAsync<T>(path, Path.GetFileNameWithoutExtension(path), priority);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task<T> LoadAsync<T>(string path, string id, LoadPriority priority = LoadPriority.Normal) where T : class, IMediaAsset {
            _totalRequests++;

            if (TryGetCached(id, out var cached) && cached is T typedCached) {
                _cacheHits++;
                return typedCached;
            }

            _cacheMisses++;
            var mediaType = GetMediaType(path);
            var request = new MediaRequest(path, id, mediaType, priority, Vector2Int.zero, null);

            return await LoadMediaAsset<T>(request);
        }

        public void LoadAsyncCallback<T>(string path, string id, Action<T> onComplete, LoadPriority priority = LoadPriority.Normal) where T : class, IMediaAsset {
            var mediaType = GetMediaType(path);
            var request = new MediaRequest(path, id, mediaType, priority, Vector2Int.zero, 
                asset => onComplete?.Invoke(asset as T));

            QueueRequest(request);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueRequest(MediaRequest request) {
            lock (_queueLock) {
                _priorityQueue.Enqueue(request);
            }
        }

        private async Task ProcessLoadingQueue() {
            if (_activeLoads >= _settings.MaxConcurrentLoads) return;

            MediaRequest request;
            lock (_queueLock) {
                if (_priorityQueue.Count == 0) return;
                request = _priorityQueue.Dequeue();
            }

            if (TryGetCached(request.ID, out var cached)) {
                request.OnComplete?.Invoke(cached);
                return;
            }

            if (_loadingTasks.ContainsKey(request.ID)) return;

            var cancelToken = new CancellationTokenSource();
            var loadTask = LoadMediaAssetInternal(request, cancelToken.Token);
            var loadingTask = new LoadingTask(request, loadTask, cancelToken);

            _loadingTasks.TryAdd(request.ID, loadingTask);
            _activeLoads++;

            _ = ProcessLoadingTask(loadingTask);
        }

        private async Task ProcessLoadingTask(LoadingTask loadingTask) {
            try {
                var asset = await loadingTask.Task;
                if (asset != null) {
                    CacheAsset(loadingTask.Request.ID, asset);
                    loadingTask.Request.OnComplete?.Invoke(asset);
                    OnAssetLoaded?.Invoke(asset);

                    _totalLoadTime += (float)loadingTask.ElapsedTime.TotalMilliseconds;
                }
            } catch (Exception ex) {
                OnLoadError?.Invoke(loadingTask.Request.ID, ex);
            } finally {
                _loadingTasks.TryRemove(loadingTask.Request.ID, out _);
                _activeLoads--;
                loadingTask.CancelToken?.Dispose();
            }
        }

        private async Task<T> LoadMediaAsset<T>(MediaRequest request) where T : class, IMediaAsset {
            var loadingTask = new LoadingTask(request, LoadMediaAssetInternal(request, CancellationToken.None), null);
            var asset = await loadingTask.Task;

            if (asset is T typedAsset) {
                CacheAsset(request.ID, asset);
                return typedAsset;
            }

            throw new InvalidCastException($"Asset {request.ID} is not of type {typeof(T).Name}");
        }

        private async Task<IMediaAsset> LoadMediaAssetInternal(MediaRequest request, CancellationToken token) {
            if (!_loaders.TryGetValue(request.Type, out var loader))
                throw new NotSupportedException($"No loader for media type: {request.Type}");

            return await loader.LoadAsync(request, token);
        }
        #endregion

        #region Advanced Cache Management
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetCached(string id, out IMediaAsset asset) => _cache.TryGetValue(id, out asset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IMediaAsset GetCached(string id) => _cache.GetValueOrDefault(id);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CacheAsset(string id, IMediaAsset asset) {
            if (_cache.Count >= _settings.MaxCacheSize) EvictLeastRecentlyUsed();

            asset.LastAccessed = DateTime.UtcNow;
            _cache[id] = asset;
        }

        private void EvictLeastRecentlyUsed() {
            var lruAsset = _cache.Values
                .Where(a => !a.IsLocked)
                .OrderBy(a => a.LastAccessed)
                .FirstOrDefault();

            if (lruAsset != null) {
                _cache.TryRemove(lruAsset.ID, out _);
                lruAsset.Dispose();
            }
        }

        public void PreloadAssets(IEnumerable<string> paths, LoadPriority priority = LoadPriority.Normal) {
            foreach (var path in paths.Take(_settings.PreloadCount)) {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!_cache.ContainsKey(id)) {
                    var mediaType = GetMediaType(path);
                    var request = new MediaRequest(path, id, mediaType, priority, Vector2Int.zero, null);
                    QueueRequest(request);
                }
            }
        }

        public async Task WarmupCache(string[] assetPaths) {
            var warmupTasks = assetPaths
                .Take(_settings.PreloadCount)
                .Select(path => LoadAsync<IMediaAsset>(path, LoadPriority.Low));

            await Task.WhenAll(warmupTasks);
        }

        public void ClearCache(bool forceAll = false) {
            var toClear = forceAll 
                ? _cache.Values.ToArray()
                : _cache.Values.Where(a => !a.IsLocked).ToArray();

            foreach (var asset in toClear) {
                _cache.TryRemove(asset.ID, out _);
                asset.Dispose();
            }

            OnCacheCleared?.Invoke();
        }

        public void LockAsset(string id) {
            if (_cache.TryGetValue(id, out var asset))
                asset.IsLocked = true;
        }

        public void UnlockAsset(string id) {
            if (_cache.TryGetValue(id, out var asset))
                asset.IsLocked = false;
        }
        #endregion

        #region Advanced Media Processing
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MediaType GetMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
            ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".gif" => MediaType.Image,
            ".mp4" or ".avi" or ".mov" or ".webm" or ".mkv" => MediaType.Video,
            ".mp3" or ".wav" or ".ogg" or ".aac" or ".flac" => MediaType.Audio,
            ".fbx" or ".obj" or ".dae" or ".blend" or ".3ds" => MediaType.Model,
            ".anim" or ".controller" or ".mask" => MediaType.Animation,
            ".shader" or ".hlsl" or ".glsl" => MediaType.Shader,
            ".mat" or ".material" => MediaType.Material,
            ".ttf" or ".otf" or ".fnt" => MediaType.Font,
            ".prefab" or ".asset" => MediaType.UI,
            _ => MediaType.Image
        };

        public async Task<Texture2D> OptimizeTexture(Texture2D source, CompressionFormat format = CompressionFormat.ASTC) {
            return await Task.Run(() => {
                var optimized = _memoryManager.Get<Texture2D>();
                
                var maxSize = Mathf.Min(source.width, source.height, _settings.TextureMaxSize);
                var resized = ResizeTexture(source, maxSize, maxSize);
                
                if (_settings.EnableCompression) ApplyCompression(resized, format);
                if (_settings.EnableMipMaps) resized.Apply(true);

                return resized;
            });
        }

        private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight) {
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight);
            Graphics.Blit(source, renderTexture);

            var resized = new Texture2D(targetWidth, targetHeight);
            RenderTexture.active = renderTexture;
            resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            resized.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(renderTexture);

            return resized;
        }

        private void ApplyCompression(Texture2D texture, CompressionFormat format) {
            var compressionFormat = format switch {
                CompressionFormat.ASTC => TextureFormat.ASTC_4x4,
                CompressionFormat.DXT => TextureFormat.DXT5,
                CompressionFormat.ETC2 => TextureFormat.ETC2_RGBA8,
                CompressionFormat.PVRTC => TextureFormat.PVRTC_RGBA4,
                _ => texture.format
            };

            EditorUtility.CompressTexture(texture, compressionFormat, TextureCompressionQuality.Best);
        }
        #endregion

        #region Statistics & Monitoring
        private MediaStatistics UpdateStatistics() {
            var hitRatio = _totalRequests > 0 ? (float)_cacheHits / _totalRequests : 0f;
            var avgLoadTime = _totalRequests > 0 ? _totalLoadTime / _totalRequests : 0f;
            var memoryUsage = CalculateMemoryUsage();

            return new MediaStatistics(_cache.Count, _activeLoads, 0, memoryUsage, hitRatio, avgLoadTime, 
                                     _totalRequests, _cacheHits, _cacheMisses);
        }

        private int CalculateMemoryUsage() {
            var totalBytes = _cache.Values.Sum(asset => asset.EstimatedSizeBytes);
            return (int)(totalBytes / (1024 * 1024));
        }

        public void LogStatistics() {
            var stats = UpdateStatistics();
            Debug.Log($"[MediaManager] {stats}");
            
            var topAssets = _cache.Values
                .OrderByDescending(a => a.EstimatedSizeBytes)
                .Take(5)
                .ToArray();

            foreach (var asset in topAssets)
                Debug.Log($"[MediaManager] Large Asset: {asset.ID} ({asset.EstimatedSizeBytes / (1024 * 1024)}MB)");
        }
        #endregion

        #region Framework Integration
        public void Tick(float deltaTime) {
            _cleanupTimer += deltaTime;
            
            if (_cleanupTimer >= _settings.CacheLifetime) {
                _cleanupTimer = 0f;
                CleanupExpiredAssets();
            }

            Statistics = UpdateStatistics();
        }

        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }

        private void CleanupExpiredAssets() {
            var threshold = DateTime.UtcNow.AddSeconds(-_settings.CacheLifetime);
            var expired = _cache.Values
                .Where(a => !a.IsLocked && a.LastAccessed < threshold)
                .ToArray();

            foreach (var asset in expired) {
                _cache.TryRemove(asset.ID, out _);
                asset.Dispose();
            }
        }

        public void Initialize() => _ = InitializeAsync();

        public void Dispose() {
            IsInitialized = false;
            ClearCache(true);
            _memoryManager?.Dispose();

            foreach (var task in _loadingTasks.Values)
                task.CancelToken?.Cancel();

            _loadingTasks.Clear();
        }
        #endregion
    }

    #region Static Utility
    public static class EditorUtility {
        public static void CompressTexture(Texture2D texture, TextureFormat format, TextureCompressionQuality quality) {
    #if UNITY_EDITOR
            UnityEditor.TextureImporter importer = UnityEditor.AssetImporter.GetAtPath(UnityEditor.AssetDatabase.GetAssetPath(texture)) as UnityEditor.TextureImporter;
            if (importer != null) {
                importer.textureCompression = UnityEditor.TextureImporterCompression.Compressed;
                
                var importerFormat = ConvertToImporterFormat(format);
                importer.SetPlatformTextureSettings(new UnityEditor.TextureImporterPlatformSettings {
                    format = importerFormat,
                    textureCompression = UnityEditor.TextureImporterCompression.Compressed,
                    compressionQuality = (int)quality
                });
                UnityEditor.AssetDatabase.ImportAsset(UnityEditor.AssetDatabase.GetAssetPath(texture));
            }
    #endif
        }
        
    #if UNITY_EDITOR
        private static UnityEditor.TextureImporterFormat ConvertToImporterFormat(TextureFormat format) => format switch {
            TextureFormat.ASTC_4x4 => UnityEditor.TextureImporterFormat.ASTC_4x4,
            TextureFormat.DXT5 => UnityEditor.TextureImporterFormat.DXT5,
            TextureFormat.ETC2_RGBA8 => UnityEditor.TextureImporterFormat.ETC2_RGBA8,
            TextureFormat.PVRTC_RGBA4 => UnityEditor.TextureImporterFormat.PVRTC_RGBA4,
            _ => UnityEditor.TextureImporterFormat.Automatic
        };
    #endif
    }

    public enum TextureCompressionQuality { Fast, Normal, Best }
    #endregion
}
