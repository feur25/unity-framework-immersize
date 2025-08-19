using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Linq;
using System.IO;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Media {
    public sealed class MediaStreamer : IFrameworkService, IFrameworkTickable {
        #region Advanced Streaming Configuration
        public enum StreamingStrategy : byte { Proximity, Priority, Predictive, Adaptive }
        public enum StreamingQuality : byte { Low, Medium, High, Ultra }

        [System.Serializable]
        public readonly struct StreamingSettings {
            public readonly float StreamingRadius, PreloadDistance, UnloadDistance;
            public readonly int MaxStreaming, ChunkSize, BufferSize;
            public readonly bool EnablePredictive, EnableAdaptive, EnableLOD;
            public readonly StreamingStrategy Strategy;
            public readonly StreamingQuality Quality;

            public StreamingSettings(float radius = 100f, float preload = 150f, float unload = 200f,
                                   int maxStreaming = 16, int chunkSize = 1024, int bufferSize = 8192,
                                   bool predictive = true, bool adaptive = true, bool lod = true,
                                   StreamingStrategy strategy = StreamingStrategy.Adaptive,
                                   StreamingQuality quality = StreamingQuality.High) =>
                (StreamingRadius, PreloadDistance, UnloadDistance, MaxStreaming, ChunkSize, BufferSize,
                 EnablePredictive, EnableAdaptive, EnableLOD, Strategy, Quality) = 
                (radius, preload, unload, maxStreaming, chunkSize, bufferSize, predictive, adaptive, lod, strategy, quality);
        }

        public readonly struct StreamingRegion {
            public readonly Vector3 Center;
            public readonly float Radius;
            public readonly string[] AssetPaths;
            public readonly MediaManager.LoadPriority Priority;

            public StreamingRegion(Vector3 center, float radius, string[] paths, MediaManager.LoadPriority priority) =>
                (Center, Radius, AssetPaths, Priority) = (center, radius, paths, priority);

            public bool Contains(Vector3 position) => Vector3.Distance(Center, position) <= Radius;
        }

        private readonly struct StreamingEntry {
            public readonly string AssetPath;
            public readonly Vector3 Position;
            public readonly float Distance;
            public readonly MediaManager.LoadPriority Priority;
            public readonly DateTime QueueTime;

            public StreamingEntry(string path, Vector3 pos, float dist, MediaManager.LoadPriority priority) =>
                (AssetPath, Position, Distance, Priority, QueueTime) = (path, pos, dist, priority, DateTime.UtcNow);
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public Vector3 ViewerPosition { get; set; } = Vector3.zero;
        public Vector3 ViewerVelocity { get; private set; } = Vector3.zero;
        public int Priority => 6;

        public event Action<string> OnAssetStreamed;
        public event Action<string> OnAssetUnloaded;

        private readonly StreamingSettings _settings;
        private readonly List<StreamingRegion> _regions = new();
        private readonly ConcurrentDictionary<string, StreamingEntry> _streamingQueue = new();
        private readonly HashSet<string> _streamedAssets = new();
        private readonly Dictionary<StreamingQuality, MediaManager.MediaSettings> _qualitySettings = new();

        private MediaManager _mediaManager;
        private Vector3 _lastPosition = Vector3.zero;
        private float _updateTimer;
        #endregion

        #region Constructors & Initialization
        public MediaStreamer() : this(new StreamingSettings()) { }
        public MediaStreamer(StreamingSettings settings) => _settings = settings;

        public async Task InitializeAsync() {
            if (IsInitialized) return;

            _mediaManager = new MediaManager();
            await _mediaManager.InitializeAsync();

            SetupQualitySettings();
            IsInitialized = true;
        }

        private void SetupQualitySettings() {
            _qualitySettings[StreamingQuality.Low] = new MediaManager.MediaSettings(512, 25, 4, 512, 60f, 25f, 0.5f);
            _qualitySettings[StreamingQuality.Medium] = new MediaManager.MediaSettings(1024, 50, 6, 1024, 120f, 50f, 0.75f);
            _qualitySettings[StreamingQuality.High] = new MediaManager.MediaSettings(2048, 100, 8, 2048, 300f, 100f, 1f);
            _qualitySettings[StreamingQuality.Ultra] = new MediaManager.MediaSettings(4096, 200, 12, 4096, 600f, 150f, 1f);
        }
        #endregion

        #region Advanced Streaming Management
        public void RegisterStreamingRegion(Vector3 center, float radius, string[] assetPaths, MediaManager.LoadPriority priority = MediaManager.LoadPriority.Normal) {
            var region = new StreamingRegion(center, radius, assetPaths, priority);
            _regions.Add(region);
        }

        public void UpdateViewerPosition(Vector3 position) {
            ViewerVelocity = (position - _lastPosition) / Time.deltaTime;
            _lastPosition = ViewerPosition;
            ViewerPosition = position;
        }

        public void UpdateViewerPosition(Transform viewer) => UpdateViewerPosition(viewer.position);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task<T> StreamAssetAsync<T>(string path, Vector3 position) where T : class, IMediaAsset {
            var distance = Vector3.Distance(ViewerPosition, position);
            var priority = GetPriorityByDistance(distance);

            return await _mediaManager.LoadAsync<T>(path, priority);
        }

        public void QueueAssetForStreaming(string path, Vector3 position, MediaManager.LoadPriority priority = MediaManager.LoadPriority.Normal) {
            var distance = Vector3.Distance(ViewerPosition, position);
            var entry = new StreamingEntry(path, position, distance, priority);

            _streamingQueue.TryAdd(path, entry);
        }

        private void ProcessStreamingQueue() {
            if (_streamingQueue.Count == 0) return;

            var activeStreaming = _streamedAssets.Count;
            if (activeStreaming >= _settings.MaxStreaming) return;

            var entriesToProcess = _streamingQueue.Values
                .Where(ShouldStreamEntry)
                .OrderBy(GetStreamingPriority)
                .Take(_settings.MaxStreaming - activeStreaming)
                .ToArray();

            foreach (var entry in entriesToProcess) {
                _streamingQueue.TryRemove(entry.AssetPath, out _);
                _ = StreamAssetInternal(entry);
            }
        }

        private async Task StreamAssetInternal(StreamingEntry entry) {
            try {
                var asset = await _mediaManager.LoadAsync<IMediaAsset>(entry.AssetPath, entry.Priority);
                if (asset != null) {
                    _streamedAssets.Add(entry.AssetPath);
                    OnAssetStreamed?.Invoke(entry.AssetPath);
                }
            } catch (Exception ex) {
                Debug.LogError($"[MediaStreamer] Failed to stream {entry.AssetPath}: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ShouldStreamEntry(StreamingEntry entry) {
            if (_streamedAssets.Contains(entry.AssetPath)) return false;
            
            return _settings.Strategy switch {
                StreamingStrategy.Proximity => entry.Distance <= _settings.StreamingRadius,
                StreamingStrategy.Priority => entry.Priority >= MediaManager.LoadPriority.High,
                StreamingStrategy.Predictive => ShouldStreamPredictive(entry),
                StreamingStrategy.Adaptive => ShouldStreamAdaptive(entry),
                _ => entry.Distance <= _settings.StreamingRadius
            };
        }

        private bool ShouldStreamPredictive(StreamingEntry entry) {
            if (!_settings.EnablePredictive) return entry.Distance <= _settings.StreamingRadius;

            var predictedPosition = ViewerPosition + ViewerVelocity * 2f;
            var predictedDistance = Vector3.Distance(predictedPosition, entry.Position);

            return predictedDistance <= _settings.PreloadDistance;
        }

        private bool ShouldStreamAdaptive(StreamingEntry entry) {
            if (!_settings.EnableAdaptive) return entry.Distance <= _settings.StreamingRadius;

            var performanceScale = GetPerformanceScale();
            var adaptiveRadius = _settings.StreamingRadius * performanceScale;

            return entry.Distance <= adaptiveRadius;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetPerformanceScale() {
            var fps = 1f / Time.deltaTime;
            return fps switch {
                >= 60f => 1.2f,
                >= 45f => 1.0f,
                >= 30f => 0.8f,
                >= 20f => 0.6f,
                _ => 0.4f
            };
        }

        private float GetStreamingPriority(StreamingEntry entry) {
            var distanceWeight = 1f - (entry.Distance / _settings.StreamingRadius);
            var priorityWeight = (int)entry.Priority * 0.1f;
            var timeWeight = (float)(DateTime.UtcNow - entry.QueueTime).TotalSeconds * 0.01f;

            return distanceWeight + priorityWeight + timeWeight;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MediaManager.LoadPriority GetPriorityByDistance(float distance) => distance switch {
            <= 25f => MediaManager.LoadPriority.Critical,
            <= 50f => MediaManager.LoadPriority.High,
            <= 100f => MediaManager.LoadPriority.Normal,
            _ => MediaManager.LoadPriority.Low
        };
        #endregion

        #region Automatic Region Detection
        private void UpdateStreamingRegions() {
            foreach (var region in _regions) {
                if (region.Contains(ViewerPosition)) {
                    foreach (var assetPath in region.AssetPaths) {
                        if (!_streamedAssets.Contains(assetPath)) {
                            QueueAssetForStreaming(assetPath, region.Center, region.Priority);
                        }
                    }
                }
            }
        }

        private void UnloadDistantAssets() {
            var toUnload = _streamedAssets
                .Where(path => {
                    if (_mediaManager.TryGetCached(Path.GetFileNameWithoutExtension(path), out var asset)) {
                        var nearestRegion = _regions
                            .Where(r => r.AssetPaths.Contains(path))
                            .OrderBy(r => Vector3.Distance(ViewerPosition, r.Center))
                            .FirstOrDefault();

                        if (nearestRegion.AssetPaths != null) {
                            var distance = Vector3.Distance(ViewerPosition, nearestRegion.Center);
                            return distance > _settings.UnloadDistance;
                        }
                    }
                    return true;
                })
                .ToArray();

            foreach (var path in toUnload) {
                _streamedAssets.Remove(path);
                var id = Path.GetFileNameWithoutExtension(path);
                _mediaManager.UnlockAsset(id);
                OnAssetUnloaded?.Invoke(path);
            }
        }
        #endregion

        #region Level-of-Detail System
        public async Task<T> StreamLODAsset<T>(string basePath, Vector3 position) where T : class, IMediaAsset {
            if (!_settings.EnableLOD) return await StreamAssetAsync<T>(basePath, position);

            var distance = Vector3.Distance(ViewerPosition, position);
            var lodLevel = GetLODLevel(distance);
            var lodPath = GetLODPath(basePath, lodLevel);

            return await _mediaManager.LoadAsync<T>(lodPath, GetPriorityByDistance(distance));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetLODLevel(float distance) => distance switch {
            <= 25f => 0,
            <= 50f => 1,
            <= 100f => 2,
            <= 200f => 3,
            _ => 4
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string GetLODPath(string basePath, int lodLevel) {
            if (lodLevel == 0) return basePath;

            var directory = Path.GetDirectoryName(basePath);
            var filename = Path.GetFileNameWithoutExtension(basePath);
            var extension = Path.GetExtension(basePath);

            return Path.Combine(directory, $"{filename}_LOD{lodLevel}{extension}");
        }
        #endregion

        #region Framework Integration
        public void Tick(float deltaTime) {
            _updateTimer += deltaTime;

            if (_updateTimer >= 0.1f) {
                _updateTimer = 0f;

                UpdateStreamingRegions();
                ProcessStreamingQueue();
                UnloadDistantAssets();
            }
        }

        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }

        public void Initialize() => _ = InitializeAsync();

        public void Dispose() {
            IsInitialized = false;
            _mediaManager?.Dispose();
            _regions.Clear();
            _streamingQueue.Clear();
            _streamedAssets.Clear();
        }
        #endregion

        #region Advanced Utilities
        public void SetStreamingQuality(StreamingQuality quality) {
            if (_qualitySettings.TryGetValue(quality, out var settings)) {
                _mediaManager?.Dispose();
                _mediaManager = new MediaManager(settings);
                _ = _mediaManager.InitializeAsync();
            }
        }

        public void PreloadRegion(Vector3 center, float radius, MediaManager.LoadPriority priority = MediaManager.LoadPriority.Normal) {
            var region = _regions.FirstOrDefault(r => Vector3.Distance(r.Center, center) <= radius);
            if (region.AssetPaths != null) {
                _mediaManager.PreloadAssets(region.AssetPaths, priority);
            }
        }

        public void ClearStreamingCache() {
            _streamedAssets.Clear();
            _streamingQueue.Clear();
            _mediaManager?.ClearCache();
        }

        public StreamingRegion[] GetActiveRegions() => 
            _regions.Where(r => r.Contains(ViewerPosition)).ToArray();

        public string[] GetStreamedAssets() => _streamedAssets.ToArray();

        public void LogStreamingStats() {
            Debug.Log($"[MediaStreamer] Position: {ViewerPosition}, Velocity: {ViewerVelocity.magnitude:F1}");
            Debug.Log($"[MediaStreamer] Streamed: {_streamedAssets.Count}, Queued: {_streamingQueue.Count}");
            Debug.Log($"[MediaStreamer] Active Regions: {GetActiveRegions().Length}");
        }
        #endregion
    }
}
