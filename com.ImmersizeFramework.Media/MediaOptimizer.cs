using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Linq;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Media {
    public sealed class MediaOptimizer : IFrameworkService, IFrameworkTickable {
        #region Advanced Optimization Configuration
        public enum OptimizationLevel : byte { None, Basic, Aggressive, Ultra }
        public enum CompressionAlgorithm : byte { LZ4, ZSTD, BROTLI, LZMA }

        [System.Serializable]
        public readonly struct OptimizationSettings {
            public readonly bool EnableTextureCompression, EnableAudioCompression, EnableMeshOptimization;
            public readonly bool EnableBatching, EnableInstancing, EnableOcclusion, EnableLODGeneration;
            public readonly float CompressionQuality, OptimizationInterval;
            public readonly int MaxBatchSize, InstanceThreshold;
            public readonly OptimizationLevel Level;
            public readonly CompressionAlgorithm Algorithm;

            public OptimizationSettings(bool texCompression = true, bool audioCompression = true, bool meshOpt = true,
                                      bool batching = true, bool instancing = true, bool occlusion = true, bool lodGen = true,
                                      float quality = 0.8f, float interval = 5f, int batchSize = 100, int instanceThreshold = 10,
                                      OptimizationLevel level = OptimizationLevel.Aggressive,
                                      CompressionAlgorithm algorithm = CompressionAlgorithm.ZSTD) =>
                (EnableTextureCompression, EnableAudioCompression, EnableMeshOptimization, EnableBatching, EnableInstancing,
                 EnableOcclusion, EnableLODGeneration, CompressionQuality, OptimizationInterval, MaxBatchSize,
                 InstanceThreshold, Level, Algorithm) = 
                (texCompression, audioCompression, meshOpt, batching, instancing, occlusion, lodGen, quality, interval, 
                 batchSize, instanceThreshold, level, algorithm);
        }

        public readonly struct OptimizationResult {
            public readonly string AssetID;
            public readonly long OriginalSize, OptimizedSize;
            public readonly float CompressionRatio, ProcessingTime;
            public readonly OptimizationLevel Level;
            public readonly DateTime Timestamp;

            public OptimizationResult(string id, long original, long optimized, float ratio, float time, OptimizationLevel level) =>
                (AssetID, OriginalSize, OptimizedSize, CompressionRatio, ProcessingTime, Level, Timestamp) = 
                (id, original, optimized, ratio, time, level, DateTime.UtcNow);

            public override string ToString() => 
                $"{AssetID}: {OriginalSize}B -> {OptimizedSize}B ({CompressionRatio:P1}) in {ProcessingTime:F2}ms";
        }

        private readonly struct BatchInfo {
            public readonly Material Material;
            public readonly Mesh[] Meshes;
            public readonly Matrix4x4[] Transforms;
            public readonly int InstanceCount;

            public BatchInfo(Material material, Mesh[] meshes, Matrix4x4[] transforms, int count) =>
                (Material, Meshes, Transforms, InstanceCount) = (material, meshes, transforms, count);
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public long TotalBytesOptimized { get; private set; }
        public float TotalCompressionRatio { get; private set; }
        public int Priority => 7;

        public event Action<OptimizationResult> OnOptimizationCompleted;
        public event Action<string, Exception> OnOptimizationError;

        private readonly OptimizationSettings _settings;
        private readonly ConcurrentQueue<IMediaAsset> _optimizationQueue = new();
        private readonly Dictionary<string, OptimizationResult> _optimizationHistory = new();
        private readonly Dictionary<Material, List<Renderer>> _renderBatches = new();
        private readonly Dictionary<Mesh, List<Transform>> _instanceGroups = new();

        private MediaManager _mediaManager;
        private float _optimizationTimer, _batchingTimer;
        private bool _isOptimizing;
        #endregion

        #region Constructors & Initialization
        public MediaOptimizer() : this(new OptimizationSettings()) { }
        public MediaOptimizer(OptimizationSettings settings) => _settings = settings;

        public async Task InitializeAsync() {
            if (IsInitialized) return;

            _mediaManager = new MediaManager();
            await _mediaManager.InitializeAsync();

            IsInitialized = true;
            _ = StartOptimizationLoop();
        }

        private async Task StartOptimizationLoop() {
            while (IsInitialized) {
                if (!_isOptimizing && _optimizationQueue.TryDequeue(out var asset)) {
                    _isOptimizing = true;
                    await ProcessOptimization(asset);
                    _isOptimizing = false;
                }
                await Task.Delay(100);
            }
        }
        #endregion

        #region Advanced Asset Optimization
        public async Task<OptimizationResult> OptimizeAssetAsync(IMediaAsset asset) {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            var startTime = Time.realtimeSinceStartup;
            var originalSize = asset.EstimatedSizeBytes;

            try {
                IMediaAsset optimizedAsset = asset.Type switch {
                    MediaManager.MediaType.Image => await OptimizeImageAsset(asset as ImageAsset),
                    MediaManager.MediaType.Audio => await OptimizeAudioAsset(asset as AudioAsset),
                    MediaManager.MediaType.Model => await OptimizeModelAsset(asset as ModelAsset),
                    MediaManager.MediaType.Video => await OptimizeVideoAsset(asset as VideoAsset),
                    _ => asset
                };

                var processingTime = (Time.realtimeSinceStartup - startTime) * 1000f;
                var optimizedSize = optimizedAsset.EstimatedSizeBytes;
                var ratio = originalSize > 0 ? 1f - (float)optimizedSize / originalSize : 0f;

                var result = new OptimizationResult(asset.ID, originalSize, optimizedSize, ratio, processingTime, _settings.Level);
                
                _optimizationHistory[asset.ID] = result;
                TotalBytesOptimized += originalSize - optimizedSize;
                UpdateCompressionRatio();

                OnOptimizationCompleted?.Invoke(result);
                return result;
            } catch (Exception ex) {
                OnOptimizationError?.Invoke(asset.ID, ex);
                throw;
            }
        }

        private async Task<IMediaAsset> OptimizeImageAsset(ImageAsset imageAsset) {
            if (!_settings.EnableTextureCompression || imageAsset?.TypedAsset == null) return imageAsset;

            return await Task.Run(() => {
                var texture = imageAsset.TypedAsset;
                var optimized = OptimizeTexture(texture);
                
                return new ImageAsset(imageAsset.ID, imageAsset.Path, optimized);
            });
        }

        private async Task<IMediaAsset> OptimizeAudioAsset(AudioAsset audioAsset) {
            if (!_settings.EnableAudioCompression || audioAsset?.TypedAsset == null) return audioAsset;

            return await Task.Run(() => {
                var clip = audioAsset.TypedAsset;
                var optimized = OptimizeAudioClip(clip);
                
                return new AudioAsset(audioAsset.ID, audioAsset.Path, optimized);
            });
        }

        private async Task<IMediaAsset> OptimizeModelAsset(ModelAsset modelAsset) {
            if (!_settings.EnableMeshOptimization || modelAsset?.TypedAsset == null) return modelAsset;

            return await Task.Run(() => {
                var model = modelAsset.TypedAsset;
                var optimized = OptimizeMesh(model);
                
                return new ModelAsset(modelAsset.ID, modelAsset.Path, optimized);
            });
        }

        private async Task<IMediaAsset> OptimizeVideoAsset(VideoAsset videoAsset) {
            return await Task.FromResult(videoAsset);
        }
        #endregion

        #region Texture Optimization
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Texture2D OptimizeTexture(Texture2D source) {
            if (source == null) return null;

            var optimized = new Texture2D(source.width, source.height, GetOptimalTextureFormat(source), true);
            
            var pixels = source.GetPixels();
            optimized.SetPixels(pixels);

            ApplyTextureCompression(optimized);
            
            optimized.Apply(true);
            
            return optimized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TextureFormat GetOptimalTextureFormat(Texture2D source) => _settings.Level switch {
            OptimizationLevel.Ultra => HasAlpha(source) ? TextureFormat.ASTC_4x4 : TextureFormat.ASTC_6x6,
            OptimizationLevel.Aggressive => HasAlpha(source) ? TextureFormat.DXT5 : TextureFormat.DXT1,
            OptimizationLevel.Basic => source.format,
            _ => source.format
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasAlpha(Texture2D texture) {
            return texture.format == TextureFormat.RGBA32 || 
                   texture.format == TextureFormat.ARGB32 ||
                   texture.format == TextureFormat.DXT5 ||
                   texture.format == TextureFormat.ASTC_4x4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyTextureCompression(Texture2D texture) {
            if (_settings.Level == OptimizationLevel.None) return;

            if (Application.platform == RuntimePlatform.Android) {
                EditorUtility.CompressTexture(texture, TextureFormat.ASTC_4x4, TextureCompressionQuality.Best);
            } else if (Application.platform == RuntimePlatform.IPhonePlayer) {
                EditorUtility.CompressTexture(texture, TextureFormat.PVRTC_RGBA4, TextureCompressionQuality.Best);
            } else {
                EditorUtility.CompressTexture(texture, TextureFormat.DXT5, TextureCompressionQuality.Best);
            }
        }
        #endregion

        #region Audio Optimization
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private AudioClip OptimizeAudioClip(AudioClip source) {
            if (source == null) return null;

            var optimizationFactor = _settings.Level switch {
                OptimizationLevel.Ultra => 0.5f,
                OptimizationLevel.Aggressive => 0.7f,
                OptimizationLevel.Basic => 0.9f,
                _ => 1f
            };

            return source;
        }
        #endregion

        #region Mesh Optimization
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private GameObject OptimizeMesh(GameObject source) {
            if (source == null) return null;

            var optimized = UnityEngine.Object.Instantiate(source);
            var meshFilters = optimized.GetComponentsInChildren<MeshFilter>();

            foreach (var meshFilter in meshFilters) {
                if (meshFilter.sharedMesh != null) {
                    meshFilter.mesh = OptimizeMeshGeometry(meshFilter.sharedMesh);
                }
            }

            return optimized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Mesh OptimizeMeshGeometry(Mesh source) {
            var optimized = new Mesh();
            
            optimized.vertices = source.vertices;
            optimized.triangles = source.triangles;
            optimized.normals = source.normals;
            optimized.uv = source.uv;

            if (_settings.Level >= OptimizationLevel.Aggressive) {
                optimized = DecimateTriangles(optimized);
            }

            optimized.RecalculateBounds();
            optimized.RecalculateNormals();
            optimized.Optimize();

            return optimized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Mesh DecimateTriangles(Mesh source) {
            var vertices = source.vertices;
            var triangles = source.triangles;
            
            var decimationFactor = _settings.Level switch {
                OptimizationLevel.Ultra => 0.5f,
                OptimizationLevel.Aggressive => 0.7f,
                _ => 1f
            };

            var targetTriCount = Mathf.RoundToInt(triangles.Length * decimationFactor);
            if (targetTriCount < triangles.Length) {
                var step = triangles.Length / targetTriCount;
                var decimatedTriangles = new List<int>();
                
                for (int i = 0; i < triangles.Length; i += step * 3) {
                    if (i + 2 < triangles.Length) {
                        decimatedTriangles.Add(triangles[i]);
                        decimatedTriangles.Add(triangles[i + 1]);
                        decimatedTriangles.Add(triangles[i + 2]);
                    }
                }
                
                source.triangles = decimatedTriangles.ToArray();
            }

            return source;
        }
        #endregion

        #region Automatic Batching System
        public void EnableAutomaticBatching() {
            if (!_settings.EnableBatching) return;

            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            OrganizeRenderBatches(renderers);
            ProcessRenderBatches();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OrganizeRenderBatches(Renderer[] renderers) {
            _renderBatches.Clear();

            foreach (var renderer in renderers) {
                if (renderer.sharedMaterial != null) {
                    if (!_renderBatches.TryGetValue(renderer.sharedMaterial, out var batch)) {
                        batch = new List<Renderer>();
                        _renderBatches[renderer.sharedMaterial] = batch;
                    }
                    batch.Add(renderer);
                }
            }
        }

        private void ProcessRenderBatches() {
            foreach (var (material, renderers) in _renderBatches) {
                if (renderers.Count >= _settings.InstanceThreshold) {
                    CreateStaticBatch(material, renderers);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateStaticBatch(Material material, List<Renderer> renderers) {
            var meshFilters = renderers
                .Select(r => r.GetComponent<MeshFilter>())
                .Where(mf => mf != null && mf.sharedMesh != null)
                .ToArray();

            if (meshFilters.Length >= _settings.InstanceThreshold) {
                StaticBatchingUtility.Combine(meshFilters.Select(mf => mf.gameObject).ToArray(), null);
            }
        }
        #endregion

        #region Processing & Utilities
        private async Task ProcessOptimization(IMediaAsset asset) {
            try {
                await OptimizeAssetAsync(asset);
            } catch (Exception ex) {
                Debug.LogError($"[MediaOptimizer] Failed to optimize {asset.ID}: {ex.Message}");
            }
        }

        public void QueueAssetForOptimization(IMediaAsset asset) {
            if (asset != null && !_optimizationHistory.ContainsKey(asset.ID)) {
                _optimizationQueue.Enqueue(asset);
            }
        }

        public void OptimizeAllCachedAssets() {
            if (_mediaManager == null) {
                Debug.LogWarning("[MediaOptimizer] MediaManager not initialized");
                return;
            }
            
            var cachedAssets = _mediaManager.GetAllCachedAssets();
            foreach (var asset in cachedAssets) {
                if (asset.State == MediaManager.ProcessingState.Cached) {
                    QueueAssetForOptimization(asset);
                }
            }
            
            Debug.Log($"[MediaOptimizer] Queued {cachedAssets.Count()} assets for optimization");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateCompressionRatio() {
            if (_optimizationHistory.Count > 0) {
                var totalOriginal = _optimizationHistory.Values.Sum(r => r.OriginalSize);
                var totalOptimized = _optimizationHistory.Values.Sum(r => r.OptimizedSize);
                TotalCompressionRatio = totalOriginal > 0 ? 1f - (float)totalOptimized / totalOriginal : 0f;
            }
        }

        public OptimizationResult[] GetOptimizationHistory() => _optimizationHistory.Values.ToArray();

        public void LogOptimizationStats() {
            Debug.Log($"[MediaOptimizer] Total Optimized: {TotalBytesOptimized / (1024 * 1024)}MB");
            Debug.Log($"[MediaOptimizer] Compression Ratio: {TotalCompressionRatio:P1}");
            Debug.Log($"[MediaOptimizer] Assets Processed: {_optimizationHistory.Count}");
        }
        #endregion

        #region Framework Integration
        public void Tick(float deltaTime) {
            _optimizationTimer += deltaTime;
            _batchingTimer += deltaTime;

            if (_optimizationTimer >= _settings.OptimizationInterval) _optimizationTimer = 0f;

            if (_batchingTimer >= 10f && _settings.EnableBatching) {
                _batchingTimer = 0f;
                EnableAutomaticBatching();
            }
        }

        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }

        public void Initialize() => _ = InitializeAsync();

        public void Dispose() {
            IsInitialized = false;
            _mediaManager?.Dispose();
            _optimizationHistory.Clear();
            _renderBatches.Clear();
            _instanceGroups.Clear();
        }
        #endregion
    }
}
