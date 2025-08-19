using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using System.Runtime.CompilerServices;
using System.Linq;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Performance {
    public class PerformanceMonitor : IFrameworkService, IFrameworkTickable, IDisposable {
        #region Configuration & Nested Types
        public enum AlertType : byte { LowFPS, HighMemory, MemoryLeak, HighGPU, LowBattery, HighTemperature, Custom }
        public enum ThermalState : byte { Nominal, Fair, Serious, Critical }

        [System.Serializable]
        public readonly struct PerformanceSettings {
            public readonly bool EnableFPS, EnableMemory, EnableGPU, EnableBattery, EnableThermal, EnableProfiling;
            public readonly float UpdateInterval, LowFPSThreshold;
            public readonly int HistorySize;
            public readonly long HighMemoryThresholdMB;

            public PerformanceSettings(bool fps = true, bool memory = true, bool gpu = true, bool battery = true,
                                     bool thermal = true, bool profiling = true, float updateInterval = 0.1f,
                                     float lowFPS = 30f, int historySize = 300, long highMemoryMB = 512) {
                EnableFPS = fps; EnableMemory = memory; EnableGPU = gpu; EnableBattery = battery;
                EnableThermal = thermal; EnableProfiling = profiling; UpdateInterval = updateInterval;
                LowFPSThreshold = lowFPS; HistorySize = historySize; HighMemoryThresholdMB = highMemoryMB * 1024 * 1024;
            }
        }

        public readonly struct PerformanceAlert {
            public readonly AlertType Type;
            public readonly string Message;
            public readonly float Value, Threshold;
            public readonly DateTime Timestamp;

            public PerformanceAlert(AlertType type, string message, float value, float threshold) {
                Type = type; Message = message; Value = value; Threshold = threshold; Timestamp = DateTime.UtcNow;
            }

            public override string ToString() => $"[{Type}] {Message} ({Value:F2}/{Threshold:F2})";
        }

        public readonly struct PerformanceMetrics {
            public readonly float CurrentFPS, AverageFPS, MinFPS, MaxFPS;
            public readonly long UsedMemory, AverageMemory, UnityUsedMemory, UnityReservedMemory;
            public readonly int GCGen0, GCGen1, GCGen2;
            public readonly float GPUTime, AverageGPUTime;
            public readonly float BatteryLevel;
            public readonly string BatteryStatus;
            public readonly ThermalState ThermalState;

            public PerformanceMetrics(float currentFPS, float avgFPS, float minFPS, float maxFPS,
                                    long usedMem, long avgMem, long unityUsed, long unityReserved,
                                    int gc0, int gc1, int gc2, float gpuTime, float avgGPU,
                                    float battery, string batteryStatus, ThermalState thermal) {
                CurrentFPS = currentFPS; AverageFPS = avgFPS; MinFPS = minFPS; MaxFPS = maxFPS;
                UsedMemory = usedMem; AverageMemory = avgMem; UnityUsedMemory = unityUsed; UnityReservedMemory = unityReserved;
                GCGen0 = gc0; GCGen1 = gc1; GCGen2 = gc2; GPUTime = gpuTime; AverageGPUTime = avgGPU;
                BatteryLevel = battery; BatteryStatus = batteryStatus; ThermalState = thermal;
            }

            public static PerformanceMetrics operator +(PerformanceMetrics a, PerformanceMetrics b) =>
                new(a.CurrentFPS + b.CurrentFPS, a.AverageFPS + b.AverageFPS, Math.Min(a.MinFPS, b.MinFPS), Math.Max(a.MaxFPS, b.MaxFPS),
                    a.UsedMemory + b.UsedMemory, a.AverageMemory + b.AverageMemory, a.UnityUsedMemory + b.UnityUsedMemory,
                    a.UnityReservedMemory + b.UnityReservedMemory, a.GCGen0 + b.GCGen0, a.GCGen1 + b.GCGen1, a.GCGen2 + b.GCGen2,
                    a.GPUTime + b.GPUTime, a.AverageGPUTime + b.AverageGPUTime, Math.Min(a.BatteryLevel, b.BatteryLevel),
                    a.BatteryStatus ?? b.BatteryStatus, (ThermalState)Math.Max((int)a.ThermalState, (int)b.ThermalState));

            public override string ToString() =>
                $"FPS: {CurrentFPS:F1} (avg:{AverageFPS:F1}) | Memory: {UsedMemory / (1024 * 1024)}MB | GC: {GCGen0}/{GCGen1}/{GCGen2} | GPU: {GPUTime:F2}ms | Battery: {BatteryLevel:P0} | Thermal: {ThermalState}";
        }

        public sealed class ProfileData {
            public long TotalTicks, MinTicks = long.MaxValue, MaxTicks;
            public int CallCount;
            public readonly ConcurrentQueue<long> History = new();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void AddSample(long ticks, int maxHistory) {
                TotalTicks += ticks;
                CallCount++;
                MinTicks = Math.Min(MinTicks, ticks);
                MaxTicks = Math.Max(MaxTicks, ticks);
                
                History.Enqueue(ticks);
                while (History.Count > maxHistory) History.TryDequeue(out _);
            }

            public double AverageMS => CallCount > 0 ? (TotalTicks / (double)CallCount) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : 0;
            public double TotalMS => TotalTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            public double MinMS => MinTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            public double MaxMS => MaxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        public readonly struct ProfileSample {
            public readonly string Name;
            public readonly long ElapsedTicks, ElapsedMilliseconds;
            public readonly double ElapsedSeconds;

            public ProfileSample(string name, long ticks, long ms) {
                Name = name; ElapsedTicks = ticks; ElapsedMilliseconds = ms;
                ElapsedSeconds = ticks / (double)System.Diagnostics.Stopwatch.Frequency;
            }

            public override string ToString() => $"{Name}: {ElapsedMilliseconds}ms ({ElapsedSeconds:F6}s)";
        }

        private readonly struct MetricHistory<T> where T : struct {
            private readonly Queue<T> _values;
            private readonly int _maxSize;

            public MetricHistory(int maxSize) {
                _values = new Queue<T>(maxSize);
                _maxSize = maxSize;
            }

            public void Add(T value) {
                _values.Enqueue(value);
                while (_values.Count > _maxSize) _values.Dequeue();
            }

            public T[] ToArray() => _values.ToArray();
            public int Count => _values.Count;
            public void Clear() => _values.Clear();
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public PerformanceMetrics CurrentMetrics { get; private set; } = new();
        public bool IsPerformanceGood => CurrentMetrics.AverageFPS >= _settings.LowFPSThreshold && 
                                        CurrentMetrics.UsedMemory < _settings.HighMemoryThresholdMB;
        public int Priority => 3;

        public event Action<PerformanceAlert> OnPerformanceAlert;
        public event Action<PerformanceMetrics> OnMetricsUpdated;

        private readonly PerformanceSettings _settings;
        private readonly ConcurrentDictionary<string, ProfileData> _profileSections = new();
        private readonly ConcurrentDictionary<string, float> _customMetrics = new();
        private readonly ConcurrentDictionary<string, MetricHistory<float>> _customHistory = new();
        private readonly System.Diagnostics.Stopwatch _profileStopwatch = new();
        
        private MetricHistory<float> _fpsHistory;
        private MetricHistory<long> _memoryHistory;
        private MetricHistory<float> _gpuTimeHistory;
        
        private float _fpsTimer, _deltaTimeAccumulator, _lastGPUTime;
        private int _frameCount, _lastGC0, _lastGC1, _lastGC2;
        private long _lastGCMemory;

        public ProfileData this[string sectionName] => _profileSections.TryGetValue(sectionName, out var data) ? data : null;
        public float this[string metricName, bool average] => average ? GetCustomMetricAverage(metricName) : GetCustomMetric(metricName);
        public PerformanceAlert[] RecentAlerts { get; private set; } = Array.Empty<PerformanceAlert>();
        #endregion

        #region Constructors & Initialization
        public PerformanceMonitor() : this(new PerformanceSettings()) { }
        
        public PerformanceMonitor(PerformanceSettings settings) {
            _settings = settings;
            _fpsHistory = new MetricHistory<float>(settings.HistorySize);
            _memoryHistory = new MetricHistory<long>(settings.HistorySize);
            _gpuTimeHistory = new MetricHistory<float>(settings.HistorySize);
        }

        public async Task InitializeAsync() {
            if (IsInitialized) return;
            
            InitializeProfiling();
            StartMonitoring();
            
            IsInitialized = true;
            await Task.CompletedTask;
        }

        private void InitializeProfiling() {
            if (_settings.EnableProfiling && Profiler.enabled) {
                Profiler.enableBinaryLog = true;
                Profiler.logFile = "ProfilerLog";
            }
        }

        private void StartMonitoring() {
            _lastGC0 = GC.CollectionCount(0);
            _lastGC1 = GC.CollectionCount(1);
            _lastGC2 = GC.CollectionCount(2);

            _lastGCMemory = GC.GetTotalMemory(false);
        }
        #endregion

        #region Advanced Metrics Collection
        public void Tick(float deltaTime) {
            if (!IsInitialized) return;

            UpdateFPSMetrics(deltaTime);
            
            _fpsTimer += deltaTime;
            if (_fpsTimer >= _settings.UpdateInterval) {
                _fpsTimer = 0f;
                CurrentMetrics = UpdateAllMetrics();
                OnMetricsUpdated?.Invoke(CurrentMetrics);
                CheckForAlerts();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateFPSMetrics(float deltaTime) {
            _frameCount++;
            _deltaTimeAccumulator += deltaTime;
        }

        private PerformanceMetrics UpdateAllMetrics() {
            var fps = _settings.EnableFPS ? UpdateFPS() : (0f, 0f, 0f, 0f);
            var memory = _settings.EnableMemory ? UpdateMemory() : (0L, 0L, 0L, 0L, 0, 0, 0);
            var gpu = _settings.EnableGPU ? UpdateGPU() : (0f, 0f);
            var batteryData = _settings.EnableBattery ? UpdateBattery() : (0f, "Unknown");
            var thermal = _settings.EnableThermal ? UpdateThermal() : ThermalState.Nominal;

            UpdateCustomMetrics();

            return new PerformanceMetrics(fps.Item1, fps.Item2, fps.Item3, fps.Item4,
                                        memory.Item1, memory.Item2, memory.Item3, memory.Item4,
                                        memory.Item5, memory.Item6, memory.Item7, gpu.Item1, gpu.Item2,
                                        batteryData.Item1, batteryData.Item2, thermal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (float current, float average, float min, float max) UpdateFPS() {
            if (_deltaTimeAccumulator <= 0f) return (0f, 0f, 0f, 0f);

            var currentFPS = _frameCount / _deltaTimeAccumulator;
            _fpsHistory.Add(currentFPS);

            var history = _fpsHistory.ToArray();
            var average = history.Length > 0 ? history.Average() : 0f;
            var min = history.Length > 0 ? history.Min() : 0f;
            var max = history.Length > 0 ? history.Max() : 0f;

            _frameCount = 0;
            _deltaTimeAccumulator = 0f;

            return (currentFPS, average, min, max);
        }

        private (long used, long average, long unityUsed, long unityReserved, int gc0, int gc1, int gc2) UpdateMemory() {
            var currentMemory = GC.GetTotalMemory(false);
            _memoryHistory.Add(currentMemory);

            var history = _memoryHistory.ToArray();
            var average = history.Length > 0 ? (long)history.Average() : 0L;

            var unityUsed = Profiler.GetTotalAllocatedMemoryLong();
            var unityReserved = Profiler.GetTotalReservedMemoryLong();

            var gc0 = GC.CollectionCount(0) - _lastGC0;
            var gc1 = GC.CollectionCount(1) - _lastGC1;
            var gc2 = GC.CollectionCount(2) - _lastGC2;

            _lastGC0 = GC.CollectionCount(0);
            _lastGC1 = GC.CollectionCount(1);
            _lastGC2 = GC.CollectionCount(2);

            var memoryGrowth = currentMemory - _lastGCMemory;
            if (memoryGrowth > 50 * 1024 * 1024)
                TriggerAlert(AlertType.MemoryLeak, $"Memory leak detected: {memoryGrowth / (1024 * 1024)}MB growth", memoryGrowth, 50 * 1024 * 1024);
            
            _lastGCMemory = currentMemory;

            return (currentMemory, average, unityUsed, unityReserved, gc0, gc1, gc2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (float current, float average) UpdateGPU() {
            var gpuTime = Time.realtimeSinceStartup * 1000f;
            var currentGPU = gpuTime - _lastGPUTime;
            _gpuTimeHistory.Add(currentGPU);

            var history = _gpuTimeHistory.ToArray();
            var average = history.Length > 0 ? history.Average() : 0f;

            _lastGPUTime = gpuTime;
            return (currentGPU, average);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (float level, string status) UpdateBattery() {
            var level = SystemInfo.batteryLevel;
            var status = SystemInfo.batteryStatus.ToString();

            if (level is > 0f and < 0.2f)
                TriggerAlert(AlertType.LowBattery, "Low battery detected", level, 0.2f);

            return (level, status);
        }

        private ThermalState UpdateThermal() => CurrentMetrics.AverageFPS switch {
            < 15f => ThermalState.Critical,
            < 21f => ThermalState.Serious,
            < 27f => ThermalState.Fair,
            _ => ThermalState.Nominal
        };

        private void UpdateCustomMetrics() {
            foreach (var (key, value) in _customMetrics) {
                if (!_customHistory.TryGetValue(key, out var history)) {
                    history = new MetricHistory<float>(_settings.HistorySize);
                    _customHistory[key] = history;
                }
                history.Add(value);
            }
        }
        #endregion

        #region Advanced Profiling System
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginSample(string name) {
            if (_settings.EnableProfiling) Profiler.BeginSample(name);
            _profileStopwatch.Restart();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndSample(string name) {
            _profileStopwatch.Stop();
            if (_settings.EnableProfiling) Profiler.EndSample();

            if (!_profileSections.TryGetValue(name, out var data)) {
                data = new ProfileData();
                _profileSections[name] = data;
            }

            data.AddSample(_profileStopwatch.ElapsedTicks, _settings.HistorySize);
        }

        public ProfileSample MeasureExecutionTime(Action action, [CallerMemberName] string name = null) {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            BeginSample(name);
            try {
                action();
            } finally {
                stopwatch.Stop();
                EndSample(name);
            }

            return new ProfileSample(name, stopwatch.ElapsedTicks, stopwatch.ElapsedMilliseconds);
        }

        public async Task<ProfileSample> MeasureExecutionTimeAsync(Func<Task> asyncAction, [CallerMemberName] string name = null) {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            BeginSample(name);
            try {
                await asyncAction();
            } finally {
                stopwatch.Stop();
                EndSample(name);
            }

            return new ProfileSample(name, stopwatch.ElapsedTicks, stopwatch.ElapsedMilliseconds);
        }

        public T MeasureExecutionTime<T>(Func<T> func, [CallerMemberName] string name = null) {
            BeginSample(name);
            try {
                return func();
            } finally {
                EndSample(name);
            }
        }

        public async Task<T> MeasureExecutionTimeAsync<T>(Func<Task<T>> asyncFunc, [CallerMemberName] string name = null) {
            BeginSample(name);
            try {
                return await asyncFunc();
            } finally {
                EndSample(name);
            }
        }

        public Dictionary<string, (double avgMS, int calls, double totalMS)> GetProfilingReport() =>
            _profileSections.ToDictionary(kvp => kvp.Key, kvp => (kvp.Value.AverageMS, kvp.Value.CallCount, kvp.Value.TotalMS));
        #endregion

        #region Custom Metrics Management
        public void SetCustomMetric(string name, float value) => _customMetrics[name] = value;
        
        public float GetCustomMetric(string name) => _customMetrics.TryGetValue(name, out var value) ? value : 0f;

        public float GetCustomMetricAverage(string name) {
            if (_customHistory.TryGetValue(name, out var history)) {
                var values = history.ToArray();
                return values.Length > 0 ? values.Average() : 0f;
            }
            return 0f;
        }

        public void IncrementCustomMetric(string name, float delta = 1f) =>
            _customMetrics.AddOrUpdate(name, delta, (_, current) => current + delta);

        public Dictionary<string, float> GetAllCustomMetrics() => new(_customMetrics);
        
        public void RemoveCustomMetric(string name) {
            _customMetrics.TryRemove(name, out _);
            _customHistory.TryRemove(name, out _);
        }
        #endregion

        #region Alert System
        private readonly List<PerformanceAlert> _recentAlerts = new();

        private void CheckForAlerts() {
            if (CurrentMetrics.AverageFPS < _settings.LowFPSThreshold)
                TriggerAlert(AlertType.LowFPS, $"Low FPS detected: {CurrentMetrics.AverageFPS:F1}",
                           CurrentMetrics.AverageFPS, _settings.LowFPSThreshold);

            if (CurrentMetrics.UsedMemory > _settings.HighMemoryThresholdMB)
                TriggerAlert(AlertType.HighMemory, $"High memory usage: {CurrentMetrics.UsedMemory / (1024 * 1024)}MB",
                           CurrentMetrics.UsedMemory, _settings.HighMemoryThresholdMB);

            if (CurrentMetrics.ThermalState == ThermalState.Critical)
                TriggerAlert(AlertType.HighTemperature, "Critical thermal state detected", 1f, 0.8f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TriggerAlert(AlertType type, string message, float value, float threshold) {
            var alert = new PerformanceAlert(type, message, value, threshold);
            
            _recentAlerts.Add(alert);
            while (_recentAlerts.Count > 50) _recentAlerts.RemoveAt(0);
            
            RecentAlerts = _recentAlerts.ToArray();
            OnPerformanceAlert?.Invoke(alert);
        }

        public void ClearAlerts() {
            _recentAlerts.Clear();
            RecentAlerts = Array.Empty<PerformanceAlert>();
        }
        #endregion

        #region Performance Analysis & Reporting
        public async Task<PerformanceMetrics> GetDetailedMetricsAsync() {
            return await Task.Run(() => {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return CurrentMetrics;
            });
        }

        public Dictionary<string, object> GeneratePerformanceReport() => new() {
            ["Timestamp"] = DateTime.UtcNow,
            ["Metrics"] = CurrentMetrics,
            ["Profiling"] = GetProfilingReport(),
            ["CustomMetrics"] = GetAllCustomMetrics(),
            ["RecentAlerts"] = RecentAlerts,
            ["IsPerformanceGood"] = IsPerformanceGood
        };

        public void LogCurrentMetrics() => UnityEngine.Debug.Log($"[PerformanceMonitor] {CurrentMetrics}");

        public void LogProfilingReport() {
            var report = GetProfilingReport();
            var topSections = report.OrderByDescending(kvp => kvp.Value.totalMS).Take(10);
            
            UnityEngine.Debug.Log("[PerformanceMonitor] Top 10 Profiling Sections:");
            foreach (var (name, (avgMS, calls, totalMS)) in topSections)
                UnityEngine.Debug.Log($"  {name}: {totalMS:F2}ms total, {avgMS:F4}ms avg, {calls} calls");
        }
        #endregion

        #region Tickable Implementation & Utilities
        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }

        public void ResetMetrics() {
            _fpsHistory.Clear();
            _memoryHistory.Clear();
            _gpuTimeHistory.Clear();
            _profileSections.Clear();
            _customMetrics.Clear();
            _customHistory.Clear();

            ClearAlerts();
            
            CurrentMetrics = new();
        }

        public async Task WarmupProfilingAsync() {
            await Task.Run(() => {
                for (int i = 0; i < 100; i++) {
                    MeasureExecutionTime(() => Thread.Sleep(1), $"WarmupTest{i % 10}");
                }
            });
        }

        public void Initialize() => _ = InitializeAsync();

        public void Dispose() => ResetMetrics();
        #endregion
    }
}
