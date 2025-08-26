using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using com.ImmersizeFramework.Memory;
using com.ImmersizeFramework.Performance;
using System.Threading.Tasks;
using System.Linq;

namespace com.ImmersizeFramework.Core {
    
    public sealed class PersistentDataManager : MonoBehaviour {
        private static PersistentDataManager _instance;
        private static readonly object _lock = new();
        
        public static PersistentDataManager Instance {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                if (_instance == null) {
                    lock (_lock) {
                        if (_instance == null) {
                            var go = new GameObject(nameof(PersistentDataManager));
                            _instance = go.AddComponent<PersistentDataManager>();
                            DontDestroyOnLoad(go);
                        }
                    }
                }
                return _instance;
            }
        }

        private readonly ConcurrentDictionary<string, object> _globalData = new();
        private readonly ConcurrentDictionary<string, SceneDataContainer> _sceneData = new();
        private readonly ConcurrentDictionary<Type, IDataProcessor> _processors = new();
        private readonly Dictionary<string, DataWatcher> _watchers = new();
        private volatile bool _isInitialized;
        
        public object this[string key] {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Get<object>(key);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Set(key, value);
        }

        public object this[string scene, string key] {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetSceneData<object>(scene, key);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetSceneData(scene, key, value);
        }

        private void Awake() {
            if (_instance != null && _instance != this) {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeProcessors();
            _isInitialized = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeProcessors() {
            RegisterProcessor<string>(new StringProcessor());
            RegisterProcessor<int>(new NumericProcessor<int>());
            RegisterProcessor<float>(new NumericProcessor<float>());
            RegisterProcessor<bool>(new BooleanProcessor());
            RegisterProcessor<Vector3>(new Vector3Processor());
            RegisterProcessor<Quaternion>(new QuaternionProcessor());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterProcessor<T>(IDataProcessor processor) => _processors[typeof(T)] = processor;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(string key, T defaultValue = default) {
            if (!_globalData.TryGetValue(key, out var value)) return defaultValue;
            
            return _processors.TryGetValue(typeof(T), out var processor) 
                ? (T)processor.Process(value) 
                : (T)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(string key, T value) {
            _globalData[key] = value;
            NotifyWatchers(key, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetSceneData<T>(string sceneName, string key, T defaultValue = default) {
            if (!_sceneData.TryGetValue(sceneName, out var container)) return defaultValue;
            return container.Get(key, defaultValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSceneData<T>(string sceneName, string key, T value) {
            var container = _sceneData.GetOrAdd(sceneName, _ => new SceneDataContainer(sceneName));
            container.Set(key, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(string key) => _globalData.ContainsKey(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasSceneData(string sceneName, string key) => 
            _sceneData.TryGetValue(sceneName, out var container) && container.Has(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(string key) {
            _globalData.TryRemove(key, out _);
            RemoveWatcher(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearSceneData(string sceneName) {
            if (_sceneData.TryRemove(sceneName, out var container))
                container.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DataWatcher Watch<T>(string key, Action<T> callback) {
            var watcher = new DataWatcher<T>(key, callback);
            _watchers[key] = watcher;
            return watcher;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NotifyWatchers<T>(string key, T value) {
            if (_watchers.TryGetValue(key, out var watcher) && watcher is DataWatcher<T> typedWatcher)
                typedWatcher.Notify(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveWatcher(string key) => _watchers.Remove(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder CreateBuilder() => new(this);

        public void OptimizeMemory() {
            var keysToRemove = _globalData.Keys.Where(k => _globalData[k] == null).ToArray();
            foreach (var key in keysToRemove)
                _globalData.TryRemove(key, out _);
            var scenesToRemove = _sceneData.Keys.Where(s => _sceneData[s].IsEmpty).ToArray();
            foreach (var scene in scenesToRemove)
                ClearSceneData(scene);
        }

        private void OnDestroy() {
            foreach (var container in _sceneData.Values)
                container.Dispose();
            
            _sceneData.Clear();
            _globalData.Clear();
            _watchers.Clear();
        }
    }

    public sealed class SceneDataContainer : IDisposable {
        private readonly ConcurrentDictionary<string, object> _data = new();
        private readonly string _sceneName;
        private volatile bool _disposed;

        public SceneDataContainer(string sceneName) => _sceneName = sceneName;

        public bool IsEmpty => _data.IsEmpty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(string key, T defaultValue = default) =>
            _data.TryGetValue(key, out var value) ? (T)value : defaultValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(string key, T value) {
            if (_disposed) return;
            _data[key] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(string key) => _data.ContainsKey(key);

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _data.Clear();
        }
    }

    public interface IDataProcessor {
        object Process(object value);
    }

    public sealed class StringProcessor : IDataProcessor {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Process(object value) => value?.ToString() ?? string.Empty;
    }

    public sealed class NumericProcessor<T> : IDataProcessor where T : struct {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Process(object value) => Convert.ChangeType(value, typeof(T));
    }

    public sealed class BooleanProcessor : IDataProcessor {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Process(object value) => value switch {
            bool b => b,
            string s => bool.TryParse(s, out var result) && result,
            int i => i != 0,
            _ => false
        };
    }

    public sealed class Vector3Processor : IDataProcessor {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Process(object value) => value is Vector3 v ? v : Vector3.zero;
    }

    public sealed class QuaternionProcessor : IDataProcessor {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Process(object value) => value is Quaternion q ? q : Quaternion.identity;
    }

    public abstract class DataWatcher {
        public readonly string Key;
        protected DataWatcher(string key) => Key = key;
        public abstract void Dispose();
    }

    public sealed class DataWatcher<T> : DataWatcher {
        private readonly Action<T> _callback;
        private volatile bool _disposed;

        public DataWatcher(string key, Action<T> callback) : base(key) => _callback = callback;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Notify(T value) {
            if (!_disposed) _callback?.Invoke(value);
        }

        public override void Dispose() => _disposed = true;
    }

    public sealed class PersistentDataBuilder {
        private readonly PersistentDataManager _manager;
        private readonly Dictionary<string, object> _batch = new();
        private string _targetScene = string.Empty;

        internal PersistentDataBuilder(PersistentDataManager manager) => _manager = manager;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder ForScene(string sceneName) {
            _targetScene = sceneName;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder Set<T>(string key, T value) {
            _batch[key] = value;
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder SetUser(string name, string role, string company) =>
            Set("userName", name).Set("userRole", role).Set("userCompany", company);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder SetAuth(bool isLoggedIn, List<string> tokens) =>
            Set("isLoggedIn", isLoggedIn).Set("userTokens", tokens);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder SetPosition(Vector3 position, Quaternion rotation) =>
            Set("lastPosition", position).Set("lastRotation", rotation);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentDataBuilder SetConfig<T>(string configKey, T config) where T : class =>
            Set($"config_{configKey}", config);

        public void Apply() {
            if (string.IsNullOrEmpty(_targetScene)) {
                foreach (var kvp in _batch)
                    _manager.Set(kvp.Key, kvp.Value);
            } else {
                foreach (var kvp in _batch)
                    _manager.SetSceneData(_targetScene, kvp.Key, kvp.Value);
            }
            
            _batch.Clear();
            _targetScene = string.Empty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task ApplyAsync() {
            await Task.Run(Apply);
        }
    }

    public static class PersistentDataExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetUserData<T>(this PersistentDataManager manager, string key, T defaultValue = default) =>
            manager.Get($"user_{key}", defaultValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetUserData<T>(this PersistentDataManager manager, string key, T value) =>
            manager.Set($"user_{key}", value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetSessionData<T>(this PersistentDataManager manager, string key, T defaultValue = default) =>
            manager.Get($"session_{key}", defaultValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetSessionData<T>(this PersistentDataManager manager, string key, T value) =>
            manager.Set($"session_{key}", value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsUserLoggedIn(this PersistentDataManager manager) =>
            manager.Get("isLoggedIn", false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string GetUserRole(this PersistentDataManager manager) =>
            manager.Get("userRole", "guest");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<string> GetUserTokens(this PersistentDataManager manager) =>
            manager.Get("userTokens", new List<string>());
    }
}
