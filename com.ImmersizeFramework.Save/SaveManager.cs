using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Linq;

namespace com.ImmersizeFramework.Save {
    public class SaveManager : IDisposable {
        #region Configuration & Nested Types
        public enum SaveFormat : byte { Binary, JSON, Compressed, Encrypted }
        public enum CompressionLevel : byte { Optimal, Fastest, NoCompression, SmallestSize }

        [System.Serializable]
        public readonly struct SaveSettings {
            public readonly string SaveDirectory;
            public readonly bool EnableEncryption, EnableCompression, EnableAutoSave, EnableIntegrityCheck, EnableBackups, PrettyPrint;
            public readonly float AutoSaveInterval;
            public readonly int MaxBackups, SaveVersion;
            public readonly SaveFormat DefaultFormat;
            public readonly CompressionLevel CompressionLevel;

            public SaveSettings(string saveDir = "Saves", bool encryption = true, bool compression = true,
                              bool autoSave = true, bool integrity = true, bool backups = true, bool prettyPrint = false,
                              float autoInterval = 60f, int maxBackups = 5, int version = 1,
                              SaveFormat format = SaveFormat.Binary, CompressionLevel compressionLevel = CompressionLevel.Optimal) {
                SaveDirectory = saveDir; EnableEncryption = encryption; EnableCompression = compression;
                EnableAutoSave = autoSave; EnableIntegrityCheck = integrity; EnableBackups = backups; PrettyPrint = prettyPrint;
                AutoSaveInterval = autoInterval; MaxBackups = maxBackups; SaveVersion = version;
                DefaultFormat = format; CompressionLevel = compressionLevel;
            }
        }

        [Serializable]
        public readonly struct SaveData {
            public readonly string Key, Type, Checksum;
            public readonly object Data;
            public readonly int Version;
            public readonly DateTime Timestamp;

            public SaveData(string key, object data, string type, int version, string checksum = "") {
                Key = key; Data = data; Type = type; Version = version;
                Checksum = checksum; Timestamp = DateTime.UtcNow;
            }

            public SaveData WithChecksum(string checksum) => new(Key, Data, Type, Version, checksum);
            public SaveData WithVersion(int version) => new(Key, Data, Type, version, Checksum);
        }

        public readonly struct SaveOperation {
            public readonly string Key;
            public readonly SaveFormat Format;
            public readonly DateTime Timestamp;
            public readonly bool Success;
            public readonly Exception Error;

            public SaveOperation(string key, SaveFormat format, bool success, Exception error = null) {
                Key = key; Format = format; Success = success; Error = error; Timestamp = DateTime.UtcNow;
            }
        }

        private sealed class SaveStream : IDisposable {
            private readonly Stream _baseStream;
            private readonly CryptoStream _cryptoStream;
            private readonly GZipStream _compressionStream;
            private readonly Stream _finalStream;

            public SaveStream(string filePath, FileMode mode, bool encrypt, bool compress, byte[] key, byte[] iv) {
                _baseStream = new FileStream(filePath, mode);
                
                Stream stream = _baseStream;
                if (compress && mode == FileMode.Create) {
                    _compressionStream = new GZipStream(stream, System.IO.Compression.CompressionLevel.Optimal);
                    stream = _compressionStream;
                } else if (compress && mode == FileMode.Open) {
                    _compressionStream = new GZipStream(stream, CompressionMode.Decompress);
                    stream = _compressionStream;
                }

                if (encrypt) {
                    var aes = Aes.Create();
                    aes.Key = key;
                    aes.IV = iv;
                    var transform = mode == FileMode.Create ? aes.CreateEncryptor() : aes.CreateDecryptor();
                    _cryptoStream = new CryptoStream(stream, transform, 
                        mode == FileMode.Create ? CryptoStreamMode.Write : CryptoStreamMode.Read);
                    stream = _cryptoStream;
                }

                _finalStream = stream;
            }

            public Stream Stream => _finalStream;

            public void Dispose() {
                _cryptoStream?.Dispose();
                _compressionStream?.Dispose();
                _baseStream?.Dispose();
            }
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public string SaveDirectory { get; private set; }
        public int Priority => 4;

        public event Action<string> OnSaveCompleted;
        public event Action<string> OnLoadCompleted;
        public event Action<string, Exception> OnSaveError;
        public event Action<string, Exception> OnLoadError;

        private readonly SaveSettings _settings;
        private readonly byte[] _encryptionKey = new byte[32];
        private readonly byte[] _encryptionIV = new byte[16];
        private readonly ConcurrentDictionary<string, object> _autoSaveData = new();
        private readonly ConcurrentDictionary<string, DateTime> _dirtyKeys = new();
        private readonly ConcurrentDictionary<int, Func<SaveData, SaveData>> _migrationHandlers = new();
        private readonly ConcurrentQueue<SaveOperation> _recentOperations = new();
        private float _autoSaveTimer;
        private bool _autoSaveRunning;

        public bool this[string key] => Exists(key);
        public object this[string key, SaveFormat format] {
            get => LoadAsync<object>(key, format).Result;
            set => _ = SaveAsync(key, value, format);
        }
        
        public SaveOperation[] RecentOperations => _recentOperations.ToArray();
        public string[] SaveKeys => GetAllSaveKeys();
        public bool HasDirtyData => !_dirtyKeys.IsEmpty;
        #endregion

        #region Constructors & Initialization
        public SaveManager() : this(new SaveSettings()) { }
        
        public SaveManager(SaveSettings settings) => _settings = settings;

        public async Task InitializeAsync() {
            if (IsInitialized) return;
            
            SaveDirectory = Path.Combine(Application.persistentDataPath, _settings.SaveDirectory);
            if (!Directory.Exists(SaveDirectory)) Directory.CreateDirectory(SaveDirectory);
            
            if (_settings.EnableEncryption) GenerateEncryptionKeys();
            SetupMigrationHandlers();
            
            IsInitialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GenerateEncryptionKeys() {
            var deviceId = SystemInfo.deviceUniqueIdentifier;
            var keyData = Encoding.UTF8.GetBytes(deviceId + "IMMERSIZE_FRAMEWORK_KEY");
            
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(keyData);
            Array.Copy(hash, _encryptionKey, 32);
            Array.Copy(hash, 0, _encryptionIV, 0, 16);
        }

        private void SetupMigrationHandlers() {
            // _migrationHandlers[2] = MigrateFromV1ToV2;
        }
        #endregion

        #region Advanced Save Operations
        public async Task<bool> SaveAsync<T>(string key, T data, SaveFormat format = SaveFormat.Binary) {
            try {
                var saveData = new SaveData(key, data, typeof(T).AssemblyQualifiedName, _settings.SaveVersion);
                var filePath = GetSaveFilePath(key, format);
                
                if (_settings.EnableBackups) await CreateBackupIfExists(filePath);
                
                await (format switch {
                    SaveFormat.Binary => SaveBinaryAsync(filePath, saveData),
                    SaveFormat.JSON => SaveJsonAsync(filePath, saveData),
                    SaveFormat.Compressed => SaveCompressedAsync(filePath, saveData),
                    SaveFormat.Encrypted => SaveEncryptedAsync(filePath, saveData),
                    _ => throw new ArgumentException($"Unsupported format: {format}")
                });

                LogOperation(new SaveOperation(key, format, true));
                OnSaveCompleted?.Invoke(key);
                return true;
            } catch (Exception ex) {
                LogOperation(new SaveOperation(key, format, false, ex));
                OnSaveError?.Invoke(key, ex);
                return false;
            }
        }

        public async Task<T> LoadAsync<T>(string key, SaveFormat format = SaveFormat.Binary, T defaultValue = default) {
            try {
                var filePath = GetSaveFilePath(key, format);
                if (!File.Exists(filePath)) return defaultValue;

                var saveData = await (format switch {
                    SaveFormat.Binary => LoadBinaryAsync(filePath),
                    SaveFormat.JSON => LoadJsonAsync(filePath),
                    SaveFormat.Compressed => LoadCompressedAsync(filePath),
                    SaveFormat.Encrypted => LoadEncryptedAsync(filePath),
                    _ => throw new ArgumentException($"Unsupported format: {format}")
                });

                if (_settings.EnableIntegrityCheck && !VerifyChecksum(saveData)) return defaultValue;
                
                saveData = MigrateIfNeeded(saveData);
                OnLoadCompleted?.Invoke(key);

                return (T)saveData.Data;
            } catch (Exception ex) {
                OnLoadError?.Invoke(key, ex);
                return defaultValue;
            }
        }

        public async Task<bool> SaveBatchAsync<T>(IEnumerable<(string key, T data)> items, SaveFormat format = SaveFormat.Binary) {
            var tasks = items.Select(item => SaveAsync(item.key, item.data, format));
            var results = await Task.WhenAll(tasks);

            return results.All(success => success);
        }

        public async Task<Dictionary<string, T>> LoadBatchAsync<T>(IEnumerable<string> keys, SaveFormat format = SaveFormat.Binary, T defaultValue = default) {
            var tasks = keys.Select(async key => new { key, value = await LoadAsync(key, format, defaultValue) });
            var results = await Task.WhenAll(tasks);

            return results.ToDictionary(r => r.key, r => r.value);
        }

        public async Task<bool> DeleteAsync(string key) {
            try {
                var formats = (SaveFormat[])Enum.GetValues(typeof(SaveFormat));
                var deleted = false;

                foreach (var format in formats) {
                    var filePath = GetSaveFilePath(key, format);
                    if (File.Exists(filePath)) {
                        File.Delete(filePath);
                        deleted = true;
                    }
                }

                _autoSaveData.TryRemove(key, out _);
                _dirtyKeys.TryRemove(key, out _);

                return deleted;
            } catch {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(string key, SaveFormat format = SaveFormat.Binary) =>
            File.Exists(GetSaveFilePath(key, format));

        public async Task<bool> ExistsAsync(string key, SaveFormat format = SaveFormat.Binary) =>
            await Task.FromResult(Exists(key, format));
        #endregion

        #region Advanced Format Operations
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task SaveBinaryAsync(string filePath, SaveData saveData) {
            using var stream = new SaveStream(filePath, FileMode.Create, _settings.EnableEncryption, _settings.EnableCompression, _encryptionKey, _encryptionIV);
            using var writer = new BinaryWriter(stream.Stream);
            
            var jsonData = JsonUtility.ToJson(saveData);
            writer.Write(jsonData);
            
            if (_settings.EnableIntegrityCheck) {
                var checksum = ComputeChecksum(jsonData);
                writer.Write(checksum);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task<SaveData> LoadBinaryAsync(string filePath) {
            using var stream = new SaveStream(filePath, FileMode.Open, _settings.EnableEncryption, _settings.EnableCompression, _encryptionKey, _encryptionIV);
            using var reader = new BinaryReader(stream.Stream);
            
            var jsonData = reader.ReadString();
            var saveData = JsonUtility.FromJson<SaveData>(jsonData);
            
            if (_settings.EnableIntegrityCheck && stream.Stream.Position < stream.Stream.Length) {
                var checksum = reader.ReadString();
                if (ComputeChecksum(jsonData) != checksum) throw new InvalidDataException("Checksum mismatch");
            }
            
            return saveData;
        }
        #endregion

        #region Advanced JSON Operations
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task SaveJsonAsync(string filePath, SaveData saveData) {
            var jsonData = JsonUtility.ToJson(saveData, _settings.PrettyPrint);
            if (_settings.EnableEncryption) jsonData = Convert.ToBase64String(EncryptData(Encoding.UTF8.GetBytes(jsonData)));
            await File.WriteAllTextAsync(filePath, jsonData);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task<SaveData> LoadJsonAsync(string filePath) {
            var jsonData = await File.ReadAllTextAsync(filePath);

            if (_settings.EnableEncryption) jsonData = Encoding.UTF8.GetString(DecryptData(Convert.FromBase64String(jsonData)));
            return JsonUtility.FromJson<SaveData>(jsonData);
        }
        #endregion

        #region Advanced Compression Operations
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task SaveCompressedAsync(string filePath, SaveData saveData) {
            var jsonData = JsonUtility.ToJson(saveData);
            var compressedData = CompressData(Encoding.UTF8.GetBytes(jsonData));

            if (_settings.EnableEncryption) compressedData = EncryptData(compressedData);
            await File.WriteAllBytesAsync(filePath, compressedData);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task<SaveData> LoadCompressedAsync(string filePath) {
            var compressedData = await File.ReadAllBytesAsync(filePath);
            
            if (_settings.EnableEncryption) compressedData = DecryptData(compressedData);

            var jsonData = Encoding.UTF8.GetString(DecompressData(compressedData));
            return JsonUtility.FromJson<SaveData>(jsonData);
        }
        #endregion

        #region Encryption & Compression Utilities
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task SaveEncryptedAsync(string filePath, SaveData saveData) {
            var jsonData = JsonUtility.ToJson(saveData);
            var encryptedData = EncryptData(Encoding.UTF8.GetBytes(jsonData));

            await File.WriteAllBytesAsync(filePath, encryptedData);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task<SaveData> LoadEncryptedAsync(string filePath) {
            var encryptedData = await File.ReadAllBytesAsync(filePath);
            var jsonData = Encoding.UTF8.GetString(DecryptData(encryptedData));
            return JsonUtility.FromJson<SaveData>(jsonData);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[] CompressData(byte[] data) {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal)) {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[] DecompressData(byte[] data) {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            gzip.CopyTo(output);

            return output.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[] EncryptData(byte[] data) {
            if (!_settings.EnableEncryption) return data;
            
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = _encryptionIV;
            
            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
            
            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();
            
            return ms.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[] DecryptData(byte[] data) {
            if (!_settings.EnableEncryption) return data;
            
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = _encryptionIV;
            
            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(data);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var result = new MemoryStream();
            
            cs.CopyTo(result);
            return result.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string ComputeChecksum(string data) {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool VerifyChecksum(SaveData saveData) => string.IsNullOrEmpty(saveData.Checksum) || ComputeChecksum(JsonUtility.ToJson(saveData)) == saveData.Checksum;
        #endregion

        #region Advanced Auto-Save System
        public void RegisterAutoSave<T>(string key, T data) {
            _autoSaveData[key] = data;
            _dirtyKeys.TryAdd(key, DateTime.UtcNow);
            
            if (_settings.EnableAutoSave && !_autoSaveRunning) {
                _autoSaveRunning = true;
                _ = StartAutoSaveAsync();
            }
        }

        public void UnregisterAutoSave(string key) {
            _autoSaveData.TryRemove(key, out _);
            _dirtyKeys.TryRemove(key, out _);
        }

        private async Task StartAutoSaveAsync() {
            while (_autoSaveRunning && _settings.EnableAutoSave) {
                await Task.Delay(TimeSpan.FromSeconds(_settings.AutoSaveInterval));
                await ProcessAutoSave();
            }
        }

        private async Task ProcessAutoSave() {
            if (!_settings.EnableAutoSave || _dirtyKeys.IsEmpty) return;

            var keysToProcess = _dirtyKeys.Keys.ToArray();
            var saveTasks = keysToProcess
                .Where(key => _autoSaveData.ContainsKey(key))
                .Select(async key => {
                    if (_autoSaveData.TryGetValue(key, out var data)) {
                        await SaveAsync(key, data, _settings.DefaultFormat);
                        _dirtyKeys.TryRemove(key, out _);
                    }
                });

            await Task.WhenAll(saveTasks);
        }
        #endregion

        #region Advanced Backup System
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task CreateBackupIfExists(string filePath) {
            if (!File.Exists(filePath)) return;

            var backupDir = Path.Combine(SaveDirectory, "Backups");
            Directory.CreateDirectory(backupDir);

            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupDir, $"{fileName}.{timestamp}.backup");

            await Task.Run(() => File.Copy(filePath, backupPath));
            await CleanupOldBackups(backupDir, fileName);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task CleanupOldBackups(string backupDir, string fileName) {
            await Task.Run(() => {
                var backupFiles = Directory.GetFiles(backupDir, $"{fileName}.*.backup")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .Skip(_settings.MaxBackups)
                    .ToArray();

                foreach (var file in backupFiles) File.Delete(file);
            });
        }
        #endregion

        #region Advanced Utilities
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string GetSaveFilePath(string key, SaveFormat format) {
            var extension = format switch {
                SaveFormat.Binary => ".save",
                SaveFormat.JSON => ".json", 
                SaveFormat.Compressed => ".sav.gz",
                SaveFormat.Encrypted => ".enc",
                _ => ".save"
            };
            return Path.Combine(SaveDirectory, $"{key}{extension}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string ComputeChecksum(object data) {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(data)));
            return Convert.ToBase64String(hash);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SaveData MigrateIfNeeded(SaveData saveData) {
            var currentVersion = saveData.Version;

            for (; currentVersion < _settings.SaveVersion && _migrationHandlers.TryGetValue(currentVersion + 1, out var migrationHandler) ;) {
                saveData = migrationHandler(saveData);
                currentVersion = saveData.Version;
            }

            return saveData;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LogOperation(SaveOperation operation, [CallerMemberName] string caller = "") {
            if (!operation.Success) {
                Debug.LogError($"[SaveManager] Failed {caller}: {operation.Key} ({operation.Format}) - {operation.Error?.Message}");
            }
        }

        public async Task<long> GetSaveDirectorySizeAsync() => 
            await Task.Run(() => new DirectoryInfo(SaveDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length));

        public string[] GetAllSaveKeys() => 
            Directory.GetFiles(SaveDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => name.EndsWith(".sav") ? name[..^4] : name)
                .Distinct()
                .ToArray();
        #endregion

        #region Standalone Integration
        public void Initialize() => _ = InitializeAsync();

        public void Update(float deltaTime) {
            if (!_settings.EnableAutoSave) return;

            _autoSaveTimer += deltaTime;
            if (_autoSaveTimer >= _settings.AutoSaveInterval) {
                _autoSaveTimer = 0f;
                _ = ProcessAutoSave();
            }
        }

        public void Dispose() {
            _autoSaveRunning = false;
            
            if (_settings.EnableAutoSave && !_dirtyKeys.IsEmpty) {
                try {
                    ProcessAutoSave().Wait(5000);
                } catch (Exception ex) {
                    Debug.LogError($"[SaveManager] Failed to save dirty data during disposal: {ex.Message}");
                }
            }
            
            _autoSaveData.Clear();
            _dirtyKeys.Clear();
        }
        #endregion
    }

    #region Save Data Structure
    [Serializable]
    public class SaveData {
        public string Key;
        public object Data;
        public string Type;
        public int Version;
        public DateTime Timestamp;
        public string Checksum;
    }
    #endregion
}
