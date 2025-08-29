using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Save {
    public sealed class GameAssetDownloader : IDisposable {
        public struct AssetInfo {
            public string Name { get; }
            public string Url { get; }
            public string Hash { get; }
            public long Size { get; }
            public DateTime LastModified { get; }
            public AssetInfo(string name, string url, string hash, long size, DateTime lastModified) => (Name, Url, Hash, Size, LastModified) = (name, url, hash, size, lastModified);
        }

        public struct DownloadResult {
            public string AssetName { get; }
            public string LocalPath { get; }
            public bool IsNew { get; }
            public bool WasUpdated { get; }
            public DownloadResult(string name, string path, bool isNew, bool updated) => (AssetName, LocalPath, IsNew, WasUpdated) = (name, path, isNew, updated);
        }

        public enum DownloadStatus { Idle, CheckingManifest, Downloading, Completed, Error }

        public float Progress { get; private set; }
        public DownloadStatus Status { get; private set; }
        public int TotalAssets => _manifestAssets.Count;
        public int DownloadedAssets => _downloadResults.Count;
        public IReadOnlyList<DownloadResult> Results => _downloadResults;
        public event Action<float> OnProgressChanged;
        public event Action<DownloadResult> OnAssetDownloaded;
        public event Action<DownloadStatus> OnStatusChanged;

        readonly ConcurrentDictionary<string, string> _localHashes = new();
        readonly List<AssetInfo> _manifestAssets = new();
        readonly List<DownloadResult> _downloadResults = new();
        readonly string _baseLocalPath;
        readonly int _maxConcurrency;
        bool _disposed;

        public GameAssetDownloader(string baseLocalPath = null, int maxConcurrency = 4) {
            _baseLocalPath = baseLocalPath ?? Path.Combine(Application.persistentDataPath, "GameAssets");

            _maxConcurrency = Math.Max(1, maxConcurrency);
            Directory.CreateDirectory(_baseLocalPath);

            LoadLocalHashes();
        }

        public AssetInfo this[int index] => index >= 0 && index < _manifestAssets.Count ? _manifestAssets[index] : default;
        public AssetInfo this[string name] {
            get {
                for (int i = 0; i < _manifestAssets.Count; i++) if (_manifestAssets[i].Name == name) return _manifestAssets[i];
                return default;
            }
        }

        public async Task<bool> CheckAndDownloadAssetsAsync(string userRole, Func<string, Task<string>> manifestFetcher = null) {
            if (_disposed || string.IsNullOrEmpty(userRole)) return false;
            manifestFetcher ??= DefaultGoogleDriveManifestFetcher;
            try {
                SetStatus(DownloadStatus.CheckingManifest);

                var manifestContent = await manifestFetcher(userRole);

                if (string.IsNullOrEmpty(manifestContent)) return false;

                ParseManifest(manifestContent);

                if (_manifestAssets.Count == 0) { SetStatus(DownloadStatus.Completed); return true; }

                SetStatus(DownloadStatus.Downloading);

                await DownloadAssetsAsync();

                SetStatus(DownloadStatus.Completed);
                SaveLocalHashes();

                return true;
            } catch (Exception ex) {
                Debug.LogError($"Asset download failed: {ex.Message}");
                SetStatus(DownloadStatus.Error);

                return false;
            }
        }

        async Task<string> DefaultGoogleDriveManifestFetcher(string role) {
            var manifestUrl = $"https://drive.google.com/uc?export=download&id=MANIFEST_FILE_ID_FOR_{role.ToUpperInvariant()}";
            using var request = UnityWebRequest.Get(manifestUrl);

            await SendWebRequestAsync(request);

            return request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : string.Empty;
        }

        void ParseManifest(string content) {
            _manifestAssets.Clear();
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines) {
                var parts = line.Split('|');
                if (parts.Length >= 4) {
                    var name = parts[0].Trim();
                    var url = parts[1].Trim();
                    var hash = parts[2].Trim();
                    
                    if (long.TryParse(parts[3].Trim(), out var size)) {
                        var lastMod = parts.Length > 4 && DateTime.TryParse(parts[4].Trim(), out var dt) ? dt : DateTime.MinValue;
                        _manifestAssets.Add(new AssetInfo(name, url, hash, size, lastMod));
                    }
                }
            }
        }

        async Task DownloadAssetsAsync() {
            _downloadResults.Clear();
            var toDownload = new List<AssetInfo>();
            
            foreach (var asset in _manifestAssets) {
                if (!_localHashes.TryGetValue(asset.Name, out var localHash) || localHash != asset.Hash) toDownload.Add(asset);
            }
            if (toDownload.Count == 0) { Progress = 1f; OnProgressChanged?.Invoke(Progress); return; }
            var semaphore = new System.Threading.SemaphoreSlim(_maxConcurrency);

            var tasks = toDownload.Select(async asset => {
                await semaphore.WaitAsync();
                try {
                    var localPath = Path.Combine(_baseLocalPath, asset.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath));

                    var wasExisting = File.Exists(localPath);

                    await DownloadFileAsync(asset.Url, localPath);

                    var downloadedHash = ComputeFileHash(localPath);
                    if (downloadedHash == asset.Hash) {
                        _localHashes[asset.Name] = asset.Hash;

                        var result = new DownloadResult(asset.Name, localPath, !wasExisting, wasExisting);
                        lock (_downloadResults) _downloadResults.Add(result);

                        OnAssetDownloaded?.Invoke(result);
                        Progress = (float)_downloadResults.Count / toDownload.Count;
                        OnProgressChanged?.Invoke(Progress);
                    }
                } finally { semaphore.Release(); }
            });
            await Task.WhenAll(tasks);
        }

        async Task DownloadFileAsync(string url, string localPath) {
            using var request = UnityWebRequest.Get(url);
            await SendWebRequestAsync(request);

            if (request.result == UnityWebRequest.Result.Success) File.WriteAllBytes(localPath, request.downloadHandler.data);
            else throw new IOException($"Download failed: {url} -> {request.error}");
        }

        async Task SendWebRequestAsync(UnityWebRequest request) {
            var operation = request.SendWebRequest();
            for (; !operation.isDone; await Task.Yield()) ;
        }

        string ComputeFileHash(string filePath) {
            if (!File.Exists(filePath)) return string.Empty;

            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);

            return BitConverter.ToString(hash).Replace("-", "");
        }

        void LoadLocalHashes() {
            var hashFile = Path.Combine(_baseLocalPath, ".hashes");

            if (!File.Exists(hashFile)) return;
            
            try {
                var lines = File.ReadAllLines(hashFile);
                foreach (var line in lines) {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2) _localHashes[parts[0]] = parts[1];
                }
            } catch { }
        }

        void SaveLocalHashes() {
            var hashFile = Path.Combine(_baseLocalPath, ".hashes");
            try {
                var lines = _localHashes.Select(kvp => $"{kvp.Key}={kvp.Value}");
                File.WriteAllLines(hashFile, lines);
            } catch { }
        }

        void SetStatus(DownloadStatus status) {
            if (Status != status) {
                Status = status;
                OnStatusChanged?.Invoke(status);
            }
        }

        public void Dispose() {
            if (!_disposed) {
                SaveLocalHashes();
                _disposed = true;
            }
        }
    }

    public sealed class AutoAssetUpdater : MonoBehaviour {
        [Header("Configuration")]
        [SerializeField] bool updateOnStart = true;
        [SerializeField] float checkInterval = 300f;
        [SerializeField] string customManifestUrl = "";

        GameAssetDownloader _downloader;
        float _lastCheckTime;

        public GameAssetDownloader Downloader => _downloader ??= new GameAssetDownloader();
        public bool IsUpdating => Downloader.Status == GameAssetDownloader.DownloadStatus.Downloading;
        public float Progress => Downloader.Progress;

        void Start() {
            if (updateOnStart) CheckForUpdates();
        }

        void Update() {
            if (Time.time - _lastCheckTime > checkInterval) CheckForUpdates();
        }

        async void CheckForUpdates() {
            _lastCheckTime = Time.time;
            var firebaseScript = GameObject.FindObjectOfType(typeof(MonoBehaviour)) as MonoBehaviour;

            if (firebaseScript == null) return;

            var roleProperty = firebaseScript.GetType().GetProperty("userRole");
            var loginProperty = firebaseScript.GetType().GetProperty("isLoggedIn");
            var mappingsField = firebaseScript.GetType().GetField("roleSceneMappings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (loginProperty?.GetValue(firebaseScript) is true) {
                var role = roleProperty?.GetValue(firebaseScript) as string ?? "guest";
                var manifestUrl = customManifestUrl;
                
                if (string.IsNullOrEmpty(manifestUrl) && mappingsField?.GetValue(firebaseScript) is Array mappings) {
                    foreach (var mapping in mappings) {
                        var roleField = mapping.GetType().GetField("Role");
                        var urlProperty = mapping.GetType().GetProperty("DownloadUrl");
                        
                        if (roleField?.GetValue(mapping) as string == role && urlProperty?.GetValue(mapping) is string url && !string.IsNullOrEmpty(url)) {
                            manifestUrl = url;
                            break;
                        }
                    }
                }
                await Downloader.CheckAndDownloadAssetsAsync(role, string.IsNullOrEmpty(manifestUrl) ? null : _ => Task.FromResult(manifestUrl));
            }
        }

        void OnDestroy() {
            _downloader?.Dispose();
        }
    }
}
