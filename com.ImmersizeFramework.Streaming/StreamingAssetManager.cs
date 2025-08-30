using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using com.ImmersizeFramework.Save;
using com.ImmersizeFramework.Core;

using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace com.ImmersizeFramework.Streaming {
    public sealed class StreamingAssetManager : MonoBehaviour {
        [Header("Configuration")]
        [SerializeField] bool useStreamingAssets = true;
        [SerializeField] string streamingPath = "RoleContent";
        [SerializeField] bool autoDownloadOnRoleChange = true;
        [SerializeField] float downloadProgressUpdateRate = 0.1f;
        
        public float DownloadProgress { get; private set; }
        public bool IsDownloading => _downloader?.Status == GameAssetDownloader.DownloadStatus.Downloading;
        public event Action<float> OnDownloadProgress;
        public event Action<string> OnAssetLoaded;
        
        readonly Dictionary<string, UnityEngine.Object> _cachedAssets = new();
        GameAssetDownloader _downloader;
        string _currentRole = "guest";
        
        static StreamingAssetManager _instance;
        public static StreamingAssetManager Instance => _instance;
        
        void Awake() {
            if (_instance != null && _instance != this) {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeDownloader();
        }
        
        void InitializeDownloader() {
            _downloader = new GameAssetDownloader(Path.Combine(Application.persistentDataPath, "DownloadedAssets"));
            _downloader.OnProgressChanged += progress => {
                DownloadProgress = progress;
                OnDownloadProgress?.Invoke(progress);
            };
        }
        
        public async Task<bool> DownloadRoleAssetsAsync(string role, string manifestUrl) {
            if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(manifestUrl)) return false;

            _currentRole = role;
            return await _downloader.CheckAndDownloadAssetsAsync(role, _ => Task.FromResult(manifestUrl));
        }
        
        public async Task<T> LoadAssetAsync<T>(string assetPath, string fallbackResourcePath = "") where T : UnityEngine.Object {
            var cacheKey = $"{_currentRole}/{assetPath}";
            if (_cachedAssets.TryGetValue(cacheKey, out var cached) && cached is T) return cached as T;
            
            var persistentFile = Path.Combine(Application.persistentDataPath, "DownloadedAssets", assetPath);
            var streamingFile = Path.Combine(Application.streamingAssetsPath, streamingPath, _currentRole, assetPath);
            
            string targetPath = null;
            if (File.Exists(persistentFile)) targetPath = persistentFile;
            else if (useStreamingAssets && File.Exists(streamingFile)) targetPath = streamingFile;
            
            T asset = null;
            if (targetPath != null)
                asset = await LoadAssetFromFileAsync<T>(targetPath);
            
            if (asset == null && !string.IsNullOrEmpty(fallbackResourcePath)) 
                asset = Resources.Load<T>(fallbackResourcePath);
            
            if (asset != null) {
                _cachedAssets[cacheKey] = asset;
                OnAssetLoaded?.Invoke(assetPath);
            }
            
            return asset;
        }
        
        async Task<T> LoadAssetFromFileAsync<T>(string filePath) where T : UnityEngine.Object {
            try {
                if (typeof(T) == typeof(Texture2D)) {
                    var bytes = File.ReadAllBytes(filePath);
                    var texture = new Texture2D(2, 2);

                    texture.LoadImage(bytes);

                    return texture as T;
                } else if (typeof(T) == typeof(AudioClip)) {
                    var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip($"file://{filePath}", AudioType.UNKNOWN);
                    request.SendWebRequest();
                    for (; !request.isDone; await Task.Yield()) ;
                    
                    if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success) {
                        var clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
                        request.Dispose();

                        return clip as T;
                    }
                    request.Dispose();
                } else if (typeof(T) == typeof(Sprite)) {
                    var bytes = File.ReadAllBytes(filePath);
                    var texture = new Texture2D(2, 2);
                    
                    texture.LoadImage(bytes);

                    var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.one * 0.5f);
                    return sprite as T;
                }
            } catch (Exception ex) {
                Debug.LogError($"Failed to load asset from {filePath}: {ex.Message}");
            }
            return null;
        }
        
        public void UnloadAsset(string assetPath) {
            var cacheKey = $"{_currentRole}/{assetPath}";
            if (_cachedAssets.TryGetValue(cacheKey, out var asset)) {
                if (asset != null) DestroyImmediate(asset);
                _cachedAssets.Remove(cacheKey);
            }
        }
        
        public void UnloadAllAssets() {
            foreach (var asset in _cachedAssets.Values) {
                if (asset != null) DestroyImmediate(asset);
            }
            _cachedAssets.Clear();
        }
        
        public void ChangeRole(string newRole) {
            if (_currentRole != newRole) {
                UnloadAllAssets();
                _currentRole = newRole;
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAsset(string assetPath) => _cachedAssets.ContainsKey($"{_currentRole}/{assetPath}");
        
        void OnDestroy() {
            UnloadAllAssets();
            _downloader?.Dispose();
            if (_instance == this) _instance = null;
        }
    }
}
