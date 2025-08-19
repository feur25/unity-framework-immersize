using System;
using System.Collections.Generic;
using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;

namespace com.ImmersizeFramework.Core {
    public sealed class FrameworkCore : MonoBehaviour {
        #region Advanced Configuration & State
        public readonly struct FrameworkConfig {
            public readonly bool EnablePerformanceMonitoring, EnableMethodTracking, EnableAutoGC;
            public readonly float TargetFPS, MemoryThresholdMB, CleanupInterval;
            public readonly int MaxConcurrentServices, ServiceTimeoutMS;

            public FrameworkConfig(bool perfMonitoring = true, bool methodTracking = true, bool autoGC = true,
                                 float targetFPS = 60f, float memThreshold = 512f, float cleanupInterval = 30f,
                                 int maxServices = 16, int timeoutMS = 5000) =>
                (EnablePerformanceMonitoring, EnableMethodTracking, EnableAutoGC, TargetFPS, MemoryThresholdMB, 
                 CleanupInterval, MaxConcurrentServices, ServiceTimeoutMS) = 
                (perfMonitoring, methodTracking, autoGC, targetFPS, memThreshold, cleanupInterval, maxServices, timeoutMS);

            public static implicit operator FrameworkConfig(bool perfMonitoring) => new(perfMonitoring);
            public static implicit operator FrameworkConfig(float targetFPS) => new(targetFPS: targetFPS);
        }

        public readonly struct ServiceInfo {
            public readonly Type Type;
            public readonly bool IsInitialized, IsTickable, IsPausable;
            public readonly DateTime RegisterTime, InitTime;
            public readonly int Priority;

            public ServiceInfo(Type type, bool initialized, bool tickable, bool pausable, 
                             DateTime registerTime, DateTime initTime, int priority) =>
                (Type, IsInitialized, IsTickable, IsPausable, RegisterTime, InitTime, Priority) = 
                (type, initialized, tickable, pausable, registerTime, initTime, priority);

            public override string ToString() => 
                $"{Type.Name}: {(IsInitialized ? "V" : "X")} P:{Priority} {(IsTickable ? "T" : "")}{(IsPausable ? "P" : "")}";
        }

        public readonly struct PerformanceMetrics {
            public readonly float CurrentFPS, AverageFPS, FrameTime;
            public readonly long MemoryUsage, PeakMemory;
            public readonly int ServiceCount, TickableCount;
            public readonly DateTime Timestamp;

            public PerformanceMetrics(float currentFPS, float avgFPS, float frameTime, long memory, long peak,
                                    int services, int tickables) =>
                (CurrentFPS, AverageFPS, FrameTime, MemoryUsage, PeakMemory, ServiceCount, TickableCount, Timestamp) = 
                (currentFPS, avgFPS, frameTime, memory, peak, services, tickables, DateTime.UtcNow);

            public static PerformanceMetrics operator +(PerformanceMetrics a, PerformanceMetrics b) =>
                new(a.CurrentFPS + b.CurrentFPS, (a.AverageFPS + b.AverageFPS) / 2f, a.FrameTime + b.FrameTime,
                    a.MemoryUsage + b.MemoryUsage, Math.Max(a.PeakMemory, b.PeakMemory),
                    a.ServiceCount + b.ServiceCount, a.TickableCount + b.TickableCount);

            public override string ToString() => 
                $"FPS:{CurrentFPS:F1}({AverageFPS:F1}) Frame:{FrameTime:F2}ms Mem:{MemoryUsage}MB Services:{ServiceCount}";
        }

        public sealed class MethodTokenInfo {
            public string Name { get; set; }
            public string Description { get; set; }
            public string ClassName { get; set; }
            public string Namespace { get; set; }
            public DateTime FirstCall { get; set; } = DateTime.UtcNow;
            public DateTime LastCall { get; set; } = DateTime.UtcNow;
            public int CallCount { get; set; }
            public List<string> Parameters { get; set; } = new();
            public string ReturnType { get; set; } = "void";
            public long TotalExecutionTicks { get; set; }

            public long AverageExecutionTicks => CallCount > 0 ? TotalExecutionTicks / CallCount : 0;
            public double AverageExecutionMS => AverageExecutionTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            public override string ToString() => 
                $"{ClassName}.{Name}: {CallCount} calls, {AverageExecutionMS:F3}ms avg";
        }
        #endregion
        #region Advanced Singleton & Properties
        private static FrameworkCore _instance;
        public static FrameworkCore Instance {
            get {
                if (_instance == null) {
                    _instance = FindObjectOfType<FrameworkCore>();
                    if (_instance == null) {
                        var go = new GameObject("[Immersize Framework Core]");

                        _instance = go.AddComponent<FrameworkCore>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        public bool IsInitialized { get; private set; }
        public PerformanceMetrics CurrentMetrics { get; private set; }
        public FrameworkConfig Config { get; private set; } = new();

        public event Action OnFrameworkInitialized;
        public event Action OnFrameworkShutdown;
        public event Action<PerformanceMetrics> OnPerformanceUpdated;
        public event Action<string, Exception> OnServiceError;

        private readonly ConcurrentDictionary<Type, IFrameworkService> _services = new();
        private readonly ConcurrentDictionary<Type, ServiceInfo> _serviceInfos = new();
        private readonly List<IFrameworkService> _serviceInitOrder = new();
        private readonly List<IFrameworkTickable> _tickableServices = new();
        private readonly ConcurrentDictionary<string, MethodTokenInfo> _methodTokens = new();
        
        private float _frameTimer, _fpsAccumulator, _memoryTimer;
        private int _frameCount;
        private long _peakMemory;

        public object this[Type serviceType] => GetService(serviceType);
        public IFrameworkService this[string serviceName] => 
            _services.Values.FirstOrDefault(s => s.GetType().Name == serviceName);

        public MethodTokenInfo GetMethodToken(string methodKey) => 
            _methodTokens.GetValueOrDefault(methodKey);

        public ServiceInfo GetServiceInfo(Type serviceType) => 
            _serviceInfos.GetValueOrDefault(serviceType);

        public int ServiceCount => _services.Count;
        public int TickableCount => _tickableServices.Count;
        public bool HasServices => !_services.IsEmpty;
        #endregion

        #region Advanced Constructors & Initialization
        public FrameworkCore() => Config = new FrameworkConfig();
        public FrameworkCore(FrameworkConfig config) => Config = config;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async void Awake() {
            if (_instance != null && _instance != this) {
                Destroy(gameObject);
                return;
            }

            _instance = this;

            DontDestroyOnLoad(gameObject);
            await InitializeFramework();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task InitializeFramework() {
            TokenizeMethod("Framework initialization started");
            
            try {
                await RegisterCoreServices();
                await InitializeAllServices();
                SetupPerformanceSettings();
                
                IsInitialized = true;
                OnFrameworkInitialized?.Invoke();
                TokenizeMethod("Framework initialization completed");
            } catch (Exception ex) {
                TokenizeMethod($"Framework initialization failed: {ex.Message}");
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task RegisterCoreServices() {
            RegisterService<Tasks.TaskManager>();
            RegisterService<Performance.PerformanceMonitor>();
            RegisterService<Input.InputManager>();
            RegisterService<Camera.CameraService>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task InitializeAllServices() {
            var initTasks = _serviceInitOrder
                .Select(async service => {
                    try {
                        await service.InitializeAsync();
                        UpdateServiceInfo(service.GetType(), true);
                    } catch (Exception ex) {
                        OnServiceError?.Invoke(service.GetType().Name, ex);
                        throw;
                    }
                });
            
            await Task.WhenAll(initTasks);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetupPerformanceSettings() {
            Application.targetFrameRate = (int)Config.TargetFPS;
            QualitySettings.vSyncCount = 0;
            
            if (Config.EnableAutoGC) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
        #endregion

        #region Advanced Service Management
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterService<T>() where T : class, IFrameworkService, new() => 
            RegisterService(new T());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterService<T>(T service) where T : class, IFrameworkService {
            var type = typeof(T);
            
            if (_services.TryAdd(type, service)) {
                _serviceInitOrder.Add(service);
                if (service is IFrameworkTickable tickable) _tickableServices.Add(tickable);
                
                var info = new ServiceInfo(type, false, service is IFrameworkTickable, 
                                          service is IFrameworkPausable, DateTime.UtcNow, 
                                          DateTime.MinValue, GetServicePriority(service));
                _serviceInfos.TryAdd(type, info);
                
                TokenizeMethod($"Service registered: {type.Name}");
            } else TokenizeMethod($"Service already registered: {type.Name}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetService<T>() where T : class, IFrameworkService => 
            _services.TryGetValue(typeof(T), out var service) ? service as T : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IFrameworkService GetService(Type type) => 
            _services.GetValueOrDefault(type);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasService<T>() where T : class, IFrameworkService => 
            _services.ContainsKey(typeof(T));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasService(Type type) => _services.ContainsKey(type);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnregisterService<T>() where T : class, IFrameworkService {
            var type = typeof(T);
            
            if (_services.TryRemove(type, out var service)) {
                _serviceInitOrder.Remove(service);
                if (service is IFrameworkTickable tickable) _tickableServices.Remove(tickable);
                _serviceInfos.TryRemove(type, out _);
                
                service.Dispose();
                TokenizeMethod($"Service unregistered: {type.Name}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateServiceInfo(Type type, bool initialized) {
            if (_serviceInfos.TryGetValue(type, out var info)) {
                var updated = new ServiceInfo(info.Type, initialized, info.IsTickable, info.IsPausable,
                                            info.RegisterTime, DateTime.UtcNow, info.Priority);
                _serviceInfos.TryUpdate(type, updated, info);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetServicePriority(IFrameworkService service) => service switch {
            Tasks.TaskManager => 1,
            Performance.PerformanceMonitor => 2,
            Input.InputManager => 3,
            Camera.CameraService => 4,
            _ => 10
        };

        public ServiceInfo[] GetAllServiceInfos() => _serviceInfos.Values.ToArray();
        public Type[] GetServiceTypes() => _services.Keys.ToArray();
        #endregion

        #region Advanced Performance & Method Tracking
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Update() {
            if (!Config.EnablePerformanceMonitoring) return;

            UpdatePerformanceMetrics();
            UpdateTickableServices();
            CheckMemoryThresholds();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdatePerformanceMetrics() {
            _frameCount++;
            _frameTimer += Time.unscaledDeltaTime;
            _fpsAccumulator += 1f / Time.unscaledDeltaTime;

            if (_frameTimer >= 1f) {
                var currentFPS = _frameCount / _frameTimer;
                var avgFPS = _fpsAccumulator / _frameCount;
                var frameTime = _frameTimer / _frameCount * 1000f;
                var memory = GC.GetTotalMemory(false) / (1024 * 1024);
                
                _peakMemory = Math.Max(_peakMemory, memory);
                
                CurrentMetrics = new PerformanceMetrics(currentFPS, avgFPS, frameTime, memory, _peakMemory,
                                                       ServiceCount, TickableCount);
                
                OnPerformanceUpdated?.Invoke(CurrentMetrics);
                
                if (currentFPS < Config.TargetFPS * 0.8f) 
                    TokenizeMethod($"Low FPS detected: {currentFPS:F1}");
                
                _frameCount = 0;
                _frameTimer = 0f;
                _fpsAccumulator = 0f;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateTickableServices() {
            var deltaTime = Time.deltaTime;
            var fixedDelta = Time.fixedDeltaTime;

            foreach (var tickable in _tickableServices) {
                try {
                    tickable.Tick(deltaTime);
                    tickable.FixedTick(fixedDelta);
                    tickable.LateTick(deltaTime);
                } catch (Exception ex) {
                    OnServiceError?.Invoke(tickable.GetType().Name, ex);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckMemoryThresholds() {
            _memoryTimer += Time.unscaledDeltaTime;
            
            if (_memoryTimer >= Config.CleanupInterval) {
                _memoryTimer = 0f;
                var memory = CurrentMetrics.MemoryUsage;
                
                if (memory > Config.MemoryThresholdMB) {
                    TokenizeMethod($"High memory usage: {memory}MB");
                    if (Config.EnableAutoGC) {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TokenizeMethod(string description, [CallerMemberName] string methodName = "",
                                  [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0) {
            if (!Config.EnableMethodTracking) return;

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var methodKey = $"{fileName}.{methodName}";
            var start = System.Diagnostics.Stopwatch.GetTimestamp();

            if (!_methodTokens.TryGetValue(methodKey, out var token)) {
                token = new MethodTokenInfo {
                    Name = methodName,
                    Description = description,
                    ClassName = fileName,
                    Namespace = ExtractNamespace(filePath)
                };
                _methodTokens[methodKey] = token;
            }

            token.CallCount++;
            token.LastCall = DateTime.UtcNow;
            token.TotalExecutionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;

            Debug.Log($"[Framework] {methodKey}: {token.CallCount} calls, {token.AverageExecutionMS:F3}ms avg");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ExtractNamespace(string filePath) {
            try {
                var parts = Path.GetDirectoryName(filePath).Split(Path.DirectorySeparatorChar);
                var idx = Array.IndexOf(parts, "Scripts");
                return idx >= 0 && idx + 1 < parts.Length 
                    ? "ImmersizeFramework." + string.Join(".", parts, idx + 1, parts.Length - idx - 1)
                    : "ImmersizeFramework.Core";
            } catch {
                return "ImmersizeFramework.Core";
            }
        }

        public MethodTokenInfo[] GetMethodStats() => _methodTokens.Values.ToArray();
        public void ClearMethodStats() => _methodTokens.Clear();
        public void SetMethodTracking(bool enabled) => Config = new FrameworkConfig(Config.EnablePerformanceMonitoring, enabled, Config.EnableAutoGC, Config.TargetFPS, Config.MemoryThresholdMB, Config.CleanupInterval, Config.MaxConcurrentServices, Config.ServiceTimeoutMS);
        #endregion

        #region Advanced Lifecycle & Utilities
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async void OnApplicationPause(bool pauseStatus) {
            if (pauseStatus) await PauseAllServices();
            else await ResumeAllServices();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async void OnApplicationFocus(bool hasFocus) {
            if (!hasFocus) await PauseAllServices();
            else await ResumeAllServices();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task PauseAllServices() {
            var pauseTasks = _services.Values
                .OfType<IFrameworkPausable>()
                .Select(s => s.PauseAsync());
            
            await Task.WhenAll(pauseTasks);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task ResumeAllServices() {
            var resumeTasks = _services.Values
                .OfType<IFrameworkPausable>()
                .Select(s => s.ResumeAsync());
            
            await Task.WhenAll(resumeTasks);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async void OnDestroy() {
            OnFrameworkShutdown?.Invoke();

            var disposeTasks = _services.Values
                .OfType<IAsyncDisposable>()
                .Select(s => s.DisposeAsync().AsTask());

            foreach (var service in _services.Values.Except(disposeTasks.Select(t => t.AsyncState as IFrameworkService)))
                service?.Dispose();

            if (disposeTasks.Any()) await Task.WhenAll(disposeTasks);

            _services.Clear();
            _serviceInitOrder.Clear();
            _tickableServices.Clear();
            _serviceInfos.Clear();
            _methodTokens.Clear();

            TokenizeMethod("Framework shutdown complete");
        }

        [ContextMenu("Force GC")][MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForceGC() {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TokenizeMethod("Manual garbage collection triggered");
        }

        [ContextMenu("Print Stats")][MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PrintStats() => TokenizeMethod($"Framework Stats: {CurrentMetrics}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetFPS(float fps) {
            Config = new FrameworkConfig(Config.EnablePerformanceMonitoring, Config.EnableMethodTracking, 
                                       Config.EnableAutoGC, fps, Config.MemoryThresholdMB, Config.CleanupInterval,
                                       Config.MaxConcurrentServices, Config.ServiceTimeoutMS);
            Application.targetFrameRate = (int)fps;
        }

        [ContextMenu("Export Debug Info")][MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async void ExportDebugInfo() {
            var debugData = new {
                Timestamp = DateTime.UtcNow,
                Config,
                Metrics = CurrentMetrics,
                Services = GetAllServiceInfos(),
                Methods = GetMethodStats().OrderByDescending(m => m.CallCount).Take(20).ToArray()
            };

            var json = JsonUtility.ToJson(debugData, true);
            await File.WriteAllTextAsync(Path.Combine(Application.persistentDataPath, "framework_debug.json"), json);
            TokenizeMethod("Debug info exported to framework_debug.json");
        }

        public void LogMessage(string message) => Debug.Log($"[Framework] {message}");
        #endregion
    }

    #region Advanced Service Interfaces
    public interface IFrameworkService : IDisposable {
        bool IsInitialized { get; }
        Task InitializeAsync();
    }

    public interface IFrameworkPausable {
        Task PauseAsync();
        Task ResumeAsync();
    }

    public interface IFrameworkTickable {
        int Priority => 10;
        void Tick(float deltaTime);
        void FixedTick(float fixedDeltaTime);
        void LateTick(float deltaTime);
    }
    #endregion
}
