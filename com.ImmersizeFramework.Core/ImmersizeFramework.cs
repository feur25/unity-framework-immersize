using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Video;

using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Game;
using com.ImmersizeFramework.Input;
using com.ImmersizeFramework.Media;
using com.ImmersizeFramework.Memory;

namespace com.ImmersizeFramework {
    public static class ImmersizeFramework {
        #region Framework Properties & State
        public static bool IsReady => FrameworkCore.Instance?.IsInitialized == true;
        public static FrameworkCore Core => FrameworkCore.Instance;
        public static string Version => "1.0.0-Ultra";
        public static DateTime InitTime { get; private set; } = DateTime.UtcNow;
        public static TimeSpan Uptime => DateTime.UtcNow - InitTime;
        
        private static readonly Dictionary<Type, object> _cachedServices = new();
        private static readonly Dictionary<string, System.Func<object>> _serviceFactories = new();
        #endregion

        #region Framework Modules - Ultra-Quick Access
        public static class Game {
            public static ModuleManager Modules => GetService<ModuleManager>();
            public static T GetManager<T>() where T : class => 
                FindObjectOfType<T>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Start<T>(T settings = default) where T : struct =>
                GetManager<GameManager<T>>()?.StartGame();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Pause() {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var pauseMethod = manager.GetType().GetMethod("PauseGame");
                    pauseMethod?.Invoke(manager, null);
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Resume() {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var resumeMethod = manager.GetType().GetMethod("ResumeGame");
                    resumeMethod?.Invoke(manager, null);
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Restart() {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var restartMethod = manager.GetType().GetMethod("RestartGame");
                    restartMethod?.Invoke(manager, null);
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Complete(bool success = true) {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var completeMethod = manager.GetType().GetMethod("CompleteGame");
                    completeMethod?.Invoke(manager, new object[] { success });
                }
            }
            
            public static GameState State {
                get {
                    var manager = GetAnyGameManager();
                    if (manager != null) {
                        var stateProperty = manager.GetType().GetProperty("CurrentState");
                        if (stateProperty?.GetValue(manager) is GameState state)
                            return state;
                    }
                    return GameState.Menu;
                }
            }
            
            public static int Score {
                get {
                    var manager = GetAnyGameManager();
                    if (manager != null) {
                        var scoreProperty = manager.GetType().GetProperty("CurrentScore");
                        if (scoreProperty?.GetValue(manager) is int score)
                            return score;
                    }
                    return 0;
                }
            }
            
            public static float Duration {
                get {
                    var manager = GetAnyGameManager();
                    if (manager != null) {
                        var durationProperty = manager.GetType().GetProperty("SessionDuration");
                        if (durationProperty?.GetValue(manager) is float duration)
                            return duration;
                    }
                    return 0f;
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void AddScore(int points) {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var addScoreMethod = manager.GetType().GetMethod("AddScore");
                    addScoreMethod?.Invoke(manager, new object[] { points });
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SetScore(int score) {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var setScoreMethod = manager.GetType().GetMethod("SetScore");
                    setScoreMethod?.Invoke(manager, new object[] { score });
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static object GetSession() {
                var manager = GetAnyGameManager();
                if (manager != null) {
                    var sessionMethod = manager.GetType().GetMethod("GetCurrentSession");
                    return sessionMethod?.Invoke(manager, null);
                }
                return null;
            }
            
            private static MonoBehaviour GetAnyGameManager() {
                var managers = FindObjectsOfType<MonoBehaviour>();
                return managers.FirstOrDefault(m => m.GetType().Name.Contains("GameManager"));
            }
        }

        public static class Input {
            public static InputBindingSystem System => GetService<InputBindingSystem>();
            public static InputManager Manager => GetService<InputManager>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Bind(string action, string display, KeyCode key, System.Action callback) =>
                System?.CreateAction(action, display, InputBindingSystem.InputActionType.ButtonDown, key);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Bind(string action, string display, KeyCode key, System.Action<float> callback) =>
                System?.CreateAction(action, display, InputBindingSystem.InputActionType.Axis, key);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Bind<T>(string action, KeyCode key, System.Action<T> callback) where T : struct =>
                System?.CreateAction(action, action, InputBindingSystem.InputActionType.ButtonDown, key);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Unbind(string action) => System?.RemoveAction(action);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SetProfile(string profile) => System?.SwitchProfile(profile);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SaveProfile(string name) => Core?.TokenizeMethod($"SaveProfile: {name}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void LoadProfile(string name) => Core?.TokenizeMethod($"LoadProfile: {name}");
            
            public static bool IsPressed(KeyCode key) => UnityEngine.Input.GetKey(key);
            public static bool IsDown(KeyCode key) => UnityEngine.Input.GetKeyDown(key);
            public static bool IsUp(KeyCode key) => UnityEngine.Input.GetKeyUp(key);
            public static Vector2 MousePosition => UnityEngine.Input.mousePosition;
            public static Vector2 MouseDelta => new(UnityEngine.Input.GetAxis("Mouse X"), UnityEngine.Input.GetAxis("Mouse Y"));
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void AutoDetect() => System?.AutoDetectInputActions();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Refresh() => Core?.TokenizeMethod("Refresh input actions");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Enable(bool enabled) => Core?.TokenizeMethod($"Input system enabled: {enabled}");
        }

        public static class Media {
            public static MediaManager Manager => GetService<MediaManager>();
            public static MediaStreamer Streamer => GetService<MediaStreamer>();
            public static MediaOptimizer Optimizer => GetService<MediaOptimizer>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<T> Load<T>(string path) where T : UnityEngine.Object {
                try {
                    var request = Resources.LoadAsync<T>(path);
                    while (!request.isDone)
                        await Task.Delay(16);
                    return request.asset as T;
                } catch {
                    return null;
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<Texture2D> LoadImage(string path) => await Load<Texture2D>(path);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<AudioClip> LoadAudio(string path) => await Load<AudioClip>(path);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<VideoClip> LoadVideo(string path) => await Load<VideoClip>(path);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<GameObject> LoadPrefab(string path) => await Load<GameObject>(path);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Unload(string path) => Core?.TokenizeMethod($"Unload asset: {path}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Cache<T>(string key, T asset) where T : UnityEngine.Object => 
                Core?.TokenizeMethod($"Cache asset: {key}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static T GetCached<T>(string key) where T : UnityEngine.Object => null;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ClearCache() => Core?.TokenizeMethod("Clear media cache");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Optimize() => Core?.TokenizeMethod("Optimize media assets");
        }

        public static class Memory {
            public static MemoryManager Manager => GetService<MemoryManager>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static T Get<T>() where T : class => Manager?.Get<T>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return<T>(T obj) where T : class => Manager?.Return(obj);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Pool<T>(System.Func<T> factory, System.Action<T> reset = null) where T : class =>
                Manager?.RegisterPool(factory, reset);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Cache<T>(string key, T value) => Manager?.Cache(key, value);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static T GetCached<T>(string key) => Manager.GetCached<T>(key);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ClearCache() => Manager?.ClearCache();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ForceGC() => Manager?.ForceGarbageCollection();
            
            public static long TotalMemory => Manager?.TotalAllocatedMemory ?? 0;
            public static long ManagedMemory => Manager?.TotalManagedMemory ?? 0;
        }

        public static class Performance {
            public static object Monitor => GetService<object>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void StartProfile(string name) => Core?.TokenizeMethod($"StartProfile: {name}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void EndProfile(string name) => Core?.TokenizeMethod($"EndProfile: {name}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Mark(string description) => Core?.TokenizeMethod(description);
            
            public static float FPS => Application.targetFrameRate;
            public static float FrameTime => Time.deltaTime * 1000f;
            public static long Memory => Core?.CurrentMetrics.MemoryUsage ?? 0;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SetTargetFPS(float fps) => Core?.SetTargetFPS(fps);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void EnableTracking(bool enabled) => Core?.SetMethodTracking(enabled);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void ExportDebug() => Core?.ExportDebugInfo();
        }

        public static class Tasks {
            public static object Manager => GetService<object>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task Run(System.Func<Task> task) => await (task?.Invoke() ?? Task.CompletedTask);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<T> Run<T>(System.Func<Task<T>> task) where T : class => 
                task != null ? await task.Invoke() : null;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Schedule(System.Action action, float delay) => 
                ScheduleCoroutine(action, delay);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Schedule(System.Func<Task> task, float delay) => 
                ScheduleCoroutine(() => task?.Invoke(), delay);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Repeat(System.Action action, float interval) => 
                ScheduleRepeating(action, interval);
            
            private static void ScheduleCoroutine(System.Action action, float delay) {
                var runner = Core?.gameObject;
                if (runner != null && action != null) {
                    runner.GetComponent<MonoBehaviour>()?.StartCoroutine(DelayedAction(action, delay));
                }
            }
            
            private static void ScheduleRepeating(System.Action action, float interval) {
                var runner = Core?.gameObject;
                if (runner != null && action != null) {
                    runner.GetComponent<MonoBehaviour>()?.StartCoroutine(RepeatingAction(action, interval));
                }
            }
            
            private static System.Collections.IEnumerator DelayedAction(System.Action action, float delay) {
                yield return new WaitForSeconds(delay);
                action?.Invoke();
            }
            
            private static System.Collections.IEnumerator RepeatingAction(System.Action action, float interval) {
                while (true) {
                    yield return new WaitForSeconds(interval);
                    action?.Invoke();
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Cancel(string taskId) => Core?.TokenizeMethod($"Cancel task: {taskId}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void CancelAll() => Core?.TokenizeMethod("Cancel all tasks");
            
            public static int ActiveTasks => 0;
            public static int CompletedTasks => 0;
        }

        public static class Camera {
            public static object Service => GetService<object>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SetMain(UnityEngine.Camera camera) {
                if (camera != null) camera.tag = "MainCamera";
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Shake(float intensity, float duration) => 
                Core?.TokenizeMethod($"Camera shake: {intensity} for {duration}s");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Follow(Transform target, float speed = 5f) => 
                Core?.TokenizeMethod($"Camera follow: {target?.name} at speed {speed}");
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void LookAt(Vector3 position) {
                var main = Main;
                if (main != null) main.transform.LookAt(position);
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SetFOV(float fov) {
                var main = Main;
                if (main != null) main.fieldOfView = fov;
            }
            
            public static UnityEngine.Camera Main => UnityEngine.Camera.main;
            public static Vector3 Position => Main?.transform.position ?? Vector3.zero;
            public static Quaternion Rotation => Main?.transform.rotation ?? Quaternion.identity;
        }
        #endregion

        #region Ultra-Quick Service Access & Caching
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetService<T>() where T : class {
            var type = typeof(T);
            
            if (_cachedServices.TryGetValue(type, out var cached))
                return cached as T;
            
            var service = FindObjectOfType<T>();
            if (service != null)
                _cachedServices[type] = service;
            
            return service;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterService<T>(T service) where T : class {
            _cachedServices[typeof(T)] = service;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterFactory<T>(System.Func<T> factory) where T : class =>
            _serviceFactories[typeof(T).Name] = () => factory();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T CreateService<T>() where T : class {
            var typeName = typeof(T).Name;
            return _serviceFactories.TryGetValue(typeName, out var factory) 
                ? factory() as T 
                : System.Activator.CreateInstance<T>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearCache() => _cachedServices.Clear();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RefreshServices() {
            ClearCache();
            var services = FindObjectsOfType<MonoBehaviour>();
            foreach (var service in services) {
                if (service != null)
                    _cachedServices[service.GetType()] = service;
            }
        }
        #endregion

        #region Framework Utilities & Extensions
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task WaitForReady() {
            while (!IsReady)
                await Task.Delay(16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Log(string message) => Core?.LogMessage(message);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LogError(string error) => UnityEngine.Debug.LogError($"[ANFA Framework] {error}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LogWarning(string warning) => UnityEngine.Debug.LogWarning($"[ANFA Framework] {warning}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T FindObjectOfType<T>() where T : class => 
            UnityEngine.Object.FindObjectOfType(typeof(T)) as T;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] FindObjectsOfType<T>() where T : class => 
            UnityEngine.Object.FindObjectsOfType(typeof(T)).Cast<T>().ToArray();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GameObject Create(string name) => new GameObject($"[ANFA] {name}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Create<T>(string name) where T : Component => Create(name).AddComponent<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Destroy(UnityEngine.Object obj, float delay = 0f) => 
            UnityEngine.Object.Destroy(obj, delay);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DontDestroy(UnityEngine.Object obj) => UnityEngine.Object.DontDestroyOnLoad(obj);

        public static class Debug {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void DrawLine(Vector3 start, Vector3 end, Color color, float duration = 0f) =>
                UnityEngine.Debug.DrawLine(start, end, color, duration);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void DrawRay(Vector3 start, Vector3 direction, Color color, float duration = 0f) =>
                UnityEngine.Debug.DrawRay(start, direction, color, duration);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Break() => UnityEngine.Debug.Break();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Assert(bool condition, string message = "") => 
                UnityEngine.Debug.Assert(condition, message);
        }
        #endregion

        #region Advanced Framework Stats & Info
        public static class Stats {
            public static int Services => Core?.ServiceCount ?? 0;
            public static int TickableServices => Core?.TickableCount ?? 0;
            public static bool HasServices => Core?.HasServices ?? false;
            public static string[] ServiceNames => Core?.GetServiceTypes()?.Select(t => t.Name).ToArray() ?? new string[0];
            public static float FrameworkFPS => Performance.FPS;
            public static long FrameworkMemory => Performance.Memory;
            public static TimeSpan FrameworkUptime => Uptime;
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void PrintAll() {
                Log($"ANFA Framework v{Version} - Services: {Services}, Uptime: {FrameworkUptime:hh\\:mm\\:ss}");
                Log($"Performance: {FrameworkFPS:F1} FPS, {FrameworkMemory / 1024f / 1024f:F1} MB");
                Log($"Active Services: {string.Join(", ", ServiceNames)}");
            }
        }

        public static class Config {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SetFPS(float fps) => Performance.SetTargetFPS(fps);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void EnablePerformanceTracking(bool enabled) => Performance.EnableTracking(enabled);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void EnableInputSystem(bool enabled) => Input.Enable(enabled);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void AutoOptimize() {
                Media.Optimize();
                Memory.ForceGC();
                Performance.Mark("Auto-optimization completed");
            }
        }
        #endregion
    }
    public static class IF {
        public static class Game {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Start<T>(T settings = default) where T : struct => 
                ImmersizeFramework.Game.Start(settings);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Pause() => ImmersizeFramework.Game.Pause();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Resume() => ImmersizeFramework.Game.Resume();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Restart() => ImmersizeFramework.Game.Restart();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Complete(bool success = true) => ImmersizeFramework.Game.Complete(success);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void AddScore(int points) => ImmersizeFramework.Game.AddScore(points);
        }

        public static class Input {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Bind(string action, KeyCode key, System.Action callback) => 
                ImmersizeFramework.Input.Bind(action, action, key, callback);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Unbind(string action) => ImmersizeFramework.Input.Unbind(action);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void AutoDetect() => ImmersizeFramework.Input.AutoDetect();
        }

        public static class Media {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static async Task<T> Load<T>(string path) where T : UnityEngine.Object => 
                await ImmersizeFramework.Media.Load<T>(path);
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Cache<T>(string key, T asset) where T : UnityEngine.Object => 
                ImmersizeFramework.Media.Cache(key, asset);
        }

        public static class Memory {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static T Get<T>() where T : class => ImmersizeFramework.Memory.Get<T>();
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Return<T>(T obj) where T : class => ImmersizeFramework.Memory.Return(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Log(string message) => ImmersizeFramework.Log(message);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get<T>() where T : class => ImmersizeFramework.GetService<T>();
    }
    public static class FrameworkExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T GetFrameworkService<T>(this Component component) where T : class => 
            ImmersizeFramework.GetService<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LogFramework(this Component component, string message) => 
            ImmersizeFramework.Log($"[{component.name}] {message}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BindInput(this Component component, string action, KeyCode key, System.Action callback) => 
            ImmersizeFramework.Input.Bind(action, action, key, callback);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<T> LoadAsset<T>(this Component component, string path) where T : UnityEngine.Object => 
            await ImmersizeFramework.Media.Load<T>(path);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddGameScore(this Component component, int points) => 
            ImmersizeFramework.Game.AddScore(points);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Pool<T>(this Component component) where T : class => 
            ImmersizeFramework.Memory.Get<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnToPool<T>(this Component component, T obj) where T : class => 
            ImmersizeFramework.Memory.Return(obj);
    }
}
