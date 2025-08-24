using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Diagnostics;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Tasks {
    public class TaskManager : IFrameworkService, IFrameworkTickable {
        
        #region Task Configuration & Nested Types
        public enum TaskPriority : byte { Low, Normal, High, Critical }
        public enum TaskState : byte { Pending, Running, Completed, Failed, Cancelled }
        
        [System.Serializable]
        public readonly struct TaskSettings {
            public readonly int MaxConcurrentTasks;
            public readonly float FrameBudgetMS;
            public readonly int HighPrioritySlots;
            public readonly float TimeoutSeconds;
            public readonly bool EnableProfiling;
            public readonly int MaxRetries;
            public readonly float RetryDelayMS;

            public TaskSettings(int maxConcurrent = 0, float frameBudget = 16.67f, int highSlots = 2, 
                               float timeout = 30f, bool profiling = true, int retries = 3, float retryDelay = 100f) {
                MaxConcurrentTasks = maxConcurrent > 0 ? maxConcurrent : Environment.ProcessorCount * 2;
                FrameBudgetMS = frameBudget;
                HighPrioritySlots = highSlots;
                TimeoutSeconds = timeout;
                EnableProfiling = profiling;
                MaxRetries = retries;
                RetryDelayMS = retryDelay;
            }
        }

        public sealed class FrameworkTask {
            public Func<Task> TaskFunc { get; set; }
            public TaskPriority Priority { get; set; }
            public CancellationToken Token { get; set; }
            public DateTime CreationTime { get; set; } = DateTime.UtcNow;
            public string Name { get; set; }
            public TaskCompletionSource<object> TCS { get; set; } = new();
            public TaskState State { get; set; } = TaskState.Pending;
            public int RetryCount { get; set; }
            public Exception LastException { get; set; }
            public TimeSpan ExecutionTime { get; set; }
            public object Result { get; set; }
            
            public void Complete(object result = null) {
                State = TaskState.Completed;
                Result = result;
                TCS.TrySetResult(result);
            }
            
            public void Fail(Exception ex) {
                State = TaskState.Failed;
                LastException = ex;
                TCS.TrySetException(ex);
            }
            
            public void Cancel() {
                State = TaskState.Cancelled;
                TCS.TrySetCanceled();
            }
            
            public void SetRunning() => State = TaskState.Running;
        }

        private readonly struct DelayedCall {
            public readonly Action Action;
            public readonly float ExecuteTime;
            public readonly bool IsRepeating;
            public readonly float Interval;
            public readonly CancellationToken Token;

            public DelayedCall(Action action, float executeTime, bool repeating = false, 
                             float interval = 0f, CancellationToken token = default) {
                Action = action;
                ExecuteTime = executeTime;
                IsRepeating = repeating;
                Interval = interval;
                Token = token;
            }

            public DelayedCall WithNextExecution(float currentTime) => 
                new(Action, currentTime + Interval, IsRepeating, Interval, Token);
        }

        public readonly struct TaskStats {
            public readonly int Queued, Started, Completed, Failed, Cancelled, Retried;
            public readonly float AverageExecutionTime, FrameUsagePercent;
            
            public TaskStats(int queued, int started, int completed, int failed, int cancelled, 
                           int retried = 0, float avgExecution = 0f, float frameUsage = 0f) {
                Queued = queued; Started = started; Completed = completed; 
                Failed = failed; Cancelled = cancelled; Retried = retried;
                AverageExecutionTime = avgExecution; FrameUsagePercent = frameUsage;
            }

            public static TaskStats operator +(TaskStats a, TaskStats b) => 
                new(a.Queued + b.Queued, a.Started + b.Started, a.Completed + b.Completed, 
                    a.Failed + b.Failed, a.Cancelled + b.Cancelled, a.Retried + b.Retried,
                    (a.AverageExecutionTime + b.AverageExecutionTime) / 2f,
                    (a.FrameUsagePercent + b.FrameUsagePercent) / 2f);

            public override string ToString() => 
                $"Q:{Queued} S:{Started} C:{Completed} F:{Failed} X:{Cancelled} R:{Retried} T:{AverageExecutionTime:F2}ms F:{FrameUsagePercent:F1}%";
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public int ActiveTasks => _activeTasks.Count;
        public int QueuedTasks => _taskQueues.Sum(q => q.Count);
        public float FrameTimeUsed { get; private set; }
        public TaskStats Stats { get { lock (_statsLock) return _stats; } }
        public int Priority => 1;
        public IReadOnlyCollection<FrameworkTask> RunningTasks => _activeTasks.Values.ToList().AsReadOnly();
        public IReadOnlyDictionary<TaskPriority, int> QueueSizes => 
            _taskQueues.Select((q, i) => new { Priority = (TaskPriority)i, Count = q.Count })
                       .ToDictionary(x => x.Priority, x => x.Count);

        private readonly TaskSettings _settings;
        private readonly ConcurrentQueue<FrameworkTask>[] _taskQueues;
        private readonly ConcurrentDictionary<Task, FrameworkTask> _activeTasks = new();
        private readonly Queue<Action> _mainThreadQueue = new();
        private readonly List<DelayedCall> _delayedCalls = new();
        private readonly ConcurrentDictionary<string, FrameworkTask> _namedTasks = new();
        private readonly Queue<float> _executionTimes = new();
        private readonly object _mainThreadLock = new();
        private readonly object _statsLock = new();
        private readonly Stopwatch _frameWatch = new();
        private readonly float _frameBudgetTicks;
        private TaskStats _stats;

        public FrameworkTask this[string name] => _namedTasks.TryGetValue(name, out var task) ? task : null;
        #endregion

        #region Constructors & Initialization
        public TaskManager() : this(new TaskSettings()) { }
        
        public TaskManager(TaskSettings settings) {
            _settings = settings;
            _taskQueues = new ConcurrentQueue<FrameworkTask>[(Enum.GetValues(typeof(TaskPriority)).Length)];
            for (int i = 0; i < _taskQueues.Length; i++) _taskQueues[i] = new();
            _frameBudgetTicks = settings.FrameBudgetMS * System.Diagnostics.Stopwatch.Frequency / 1000f;
        }

        public async Task InitializeAsync() {
            if (IsInitialized) return;
            
            ThreadPool.SetMinThreads(_settings.MaxConcurrentTasks, _settings.MaxConcurrentTasks);
            ThreadPool.SetMaxThreads(_settings.MaxConcurrentTasks * 2, _settings.MaxConcurrentTasks * 2);
            
            IsInitialized = true;
            await Task.CompletedTask;
        }
        #endregion

        #region Advanced Features
        public Task<T> RunWithRetry<T>(Func<Task<T>> taskFunc, int maxRetries = -1, float retryDelay = -1f, 
            TaskPriority priority = TaskPriority.Normal, CancellationToken token = default, 
            [CallerMemberName] string name = null) {
            
            maxRetries = maxRetries < 0 ? _settings.MaxRetries : maxRetries;
            retryDelay = retryDelay < 0 ? _settings.RetryDelayMS : retryDelay;

            return RunAsync(async () => {
                for (int attempt = 0; attempt <= maxRetries; attempt++) {
                    try {
                        return await taskFunc();
                    } catch (Exception ex) when (attempt < maxRetries) {
                        lock (_statsLock) _stats += new TaskStats(0, 0, 0, 0, 0, 1);
                        if (retryDelay > 0) await Task.Delay((int)retryDelay, token);
                        if (attempt == maxRetries - 1) throw;
                    }
                }
                throw new InvalidOperationException("Max retries exceeded");
            }, priority, token, $"{name}_Retry");
        }

        public Task<T> RunWithTimeout<T>(Func<Task<T>> taskFunc, TimeSpan timeout, 
            TaskPriority priority = TaskPriority.Normal, [CallerMemberName] string name = null) {
            
            using var cts = new CancellationTokenSource(timeout);
            return RunAsync(taskFunc, priority, cts.Token, $"{name}_Timeout");
        }

        public Task<T> RunWithCircuitBreaker<T>(string circuitName, Func<Task<T>> taskFunc, 
            int failureThreshold = 5, TimeSpan resetTimeout = default, 
            TaskPriority priority = TaskPriority.Normal, [CallerMemberName] string name = null) {
            
            return RunAsync(async () => {
                var circuit = GetOrCreateCircuit(circuitName, failureThreshold, resetTimeout);
                return await circuit.ExecuteAsync(taskFunc);
            }, priority, default, $"{name}_Circuit");
        }

        public Task Batch(IEnumerable<Func<Task>> tasks, int batchSize = 10, 
            TaskPriority priority = TaskPriority.Normal, CancellationToken token = default) {
            
            return RunAsync(async () => {
                var taskList = tasks.ToList();
                for (int i = 0; i < taskList.Count; i += batchSize) {
                    var batch = taskList.Skip(i).Take(batchSize);
                    var batchTasks = batch.Select(t => RunAsync(t, priority, token));
                    await Task.WhenAll(batchTasks);
                }
            }, priority, token, "BatchExecution");
        }

        public async Task<T> RaceAsync<T>(params Func<Task<T>>[] tasks) {
            var raceTasks = tasks.Select((t, i) => RunAsync(t, TaskPriority.High, default, $"Race_{i}"));
            return await Task.WhenAny(raceTasks).ContinueWith(t => t.Result.Result);
        }

        public Task Pipeline<T>(IEnumerable<T> items, params Func<T, Task<T>>[] stages) {
            return RunAsync(async () => {
                var results = items;
                foreach (var stage in stages) {
                    var currentStage = stage;
                    results = await Task.WhenAll(results.Select(currentStage));
                }
            }, TaskPriority.Normal, default, "Pipeline");
        }

        private readonly ConcurrentDictionary<string, CircuitBreaker> _circuits = new();
        
        private CircuitBreaker GetOrCreateCircuit(string name, int threshold, TimeSpan timeout) {
            return _circuits.GetOrAdd(name, _ => new CircuitBreaker(threshold, timeout));
        }

        private sealed class CircuitBreaker {
            private readonly int _failureThreshold;
            private readonly TimeSpan _resetTimeout;
            private int _failureCount;
            private DateTime _lastFailureTime;
            private volatile bool _isOpen;

            public CircuitBreaker(int threshold, TimeSpan timeout) {
                _failureThreshold = threshold;
                _resetTimeout = timeout == default ? TimeSpan.FromMinutes(1) : timeout;
            }

            public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation) {
                if (_isOpen && DateTime.UtcNow - _lastFailureTime < _resetTimeout)
                    throw new InvalidOperationException("Circuit breaker is open");

                try {
                    var result = await operation();
                    Reset();
                    return result;
                } catch (Exception) {
                    RecordFailure();
                    throw;
                }
            }

            private void RecordFailure() {
                _failureCount++;
                _lastFailureTime = DateTime.UtcNow;
                if (_failureCount >= _failureThreshold) _isOpen = true;
            }

            private void Reset() {
                _failureCount = 0;
                _isOpen = false;
            }
        }
        #endregion

        #region Task Execution
        public Task<T> RunAsync<T>(Func<Task<T>> taskFunc, TaskPriority priority = TaskPriority.Normal, 
            CancellationToken token = default, [CallerMemberName] string name = null) {
            
            var tcs = new TaskCompletionSource<T>();
            var task = new FrameworkTask {
                TaskFunc = async () => {
                    try {
                        var result = await taskFunc();
                        tcs.SetResult(result);
                    } catch (Exception ex) {
                        tcs.SetException(ex);
                        throw;
                    }
                },
                Priority = priority,
                Token = token,
                Name = name ?? $"Task<{typeof(T).Name}>"
            };

            EnqueueTask(task);
            return tcs.Task;
        }

        public Task RunAsync(Func<Task> taskFunc, TaskPriority priority = TaskPriority.Normal, 
            CancellationToken token = default, [CallerMemberName] string name = null) {
            
            var task = new FrameworkTask {
                TaskFunc = taskFunc,
                Priority = priority,
                Token = token,
                Name = name ?? "Task"
            };

            EnqueueTask(task);
            return task.TCS.Task;
        }

        public Task RunOnMainThread(Action action, [CallerMemberName] string name = null) {
            var tcs = new TaskCompletionSource<object>();
            
            lock (_mainThreadLock) {
                _mainThreadQueue.Enqueue(() => {
                    try {
                        action();
                        tcs.SetResult(null);
                    } catch (Exception ex) {
                        tcs.SetException(ex);
                    }
                });
            }

            return tcs.Task;
        }

        public Task<T> RunOnMainThread<T>(Func<T> func, [CallerMemberName] string name = null) {
            var tcs = new TaskCompletionSource<T>();
            
            lock (_mainThreadLock) {
                _mainThreadQueue.Enqueue(() => {
                    try {
                        var result = func();
                        tcs.SetResult(result);
                    } catch (Exception ex) {
                        tcs.SetException(ex);
                    }
                });
            }

            return tcs.Task;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnqueueTask(FrameworkTask task) {
            _taskQueues[(int)task.Priority].Enqueue(task);
            if (!string.IsNullOrEmpty(task.Name)) _namedTasks.TryAdd(task.Name, task);
            lock (_statsLock) _stats += new TaskStats(1, 0, 0, 0, 0);
        }
        #endregion

        #region Delayed & Parallel Execution
        public void ScheduleDelayedCall(Action action, float delay, CancellationToken token = default) =>
            _delayedCalls.Add(new DelayedCall(action, Time.time + delay, false, 0f, token));

        public void ScheduleRepeatingCall(Action action, float interval, CancellationToken token = default) =>
            _delayedCalls.Add(new DelayedCall(action, Time.time + interval, true, interval, token));

        public async Task DelayAsync(float delay, CancellationToken token = default) {
            var tcs = new TaskCompletionSource<object>();
            ScheduleDelayedCall(() => tcs.SetResult(null), delay, token);

            token.Register(() => tcs.SetCanceled());
            await tcs.Task;
        }

        public async Task RunParallel(params Func<Task>[] tasks) {
            var parallelTasks = tasks.Select((t, i) => RunAsync(t, TaskPriority.Normal, default, $"Parallel_{i}"));
            await Task.WhenAll(parallelTasks);
        }

        public async Task<T[]> RunParallel<T>(params Func<Task<T>>[] tasks) {
            var parallelTasks = tasks.Select((t, i) => RunAsync(t, TaskPriority.Normal, default, $"Parallel<{typeof(T).Name}>_{i}"));
            return await Task.WhenAll(parallelTasks);
        }

        public async Task ForEachAsync<T>(IEnumerable<T> items, Func<T, Task> processor, 
            int maxConcurrency = -1, CancellationToken token = default) {
            
            if (maxConcurrency <= 0) maxConcurrency = _settings.MaxConcurrentTasks;

            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = items.Select(async item => {
                await semaphore.WaitAsync(token);
                try {
                    await processor(item);
                } finally {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        #endregion

        #region Tick Implementation
        public void Tick(float deltaTime) {
            _frameWatch.Restart();
            
            ProcessMainThreadQueue();
            ProcessDelayedCalls();
            ProcessTaskQueues();
            CleanupCompletedTasks();
            
            _frameWatch.Stop();
            FrameTimeUsed = (float)_frameWatch.ElapsedTicks / System.Diagnostics.Stopwatch.Frequency * 1000f;
        }

        private void ProcessMainThreadQueue() {
            lock (_mainThreadLock) {
                var budget = _frameBudgetTicks * 0.3f;
                for (; _mainThreadQueue.Count > 0 && _frameWatch.ElapsedTicks < budget ;) {
                    _mainThreadQueue.Dequeue()?.Invoke();
                }
            }
        }

        private void ProcessDelayedCalls() {
            var currentTime = Time.time;
            for (int i = _delayedCalls.Count - 1; i >= 0; i--) {
                var call = _delayedCalls[i];

                if (call.Token.IsCancellationRequested || currentTime < call.ExecuteTime) continue;

                try { call.Action?.Invoke(); } catch (Exception ex) { UnityEngine.Debug.LogError($"[TaskManager] Delayed call error: {ex}"); }
                if (call.IsRepeating && !call.Token.IsCancellationRequested)
                    _delayedCalls[i] = call.WithNextExecution(currentTime);
                else _delayedCalls.RemoveAt(i);
            }
            _delayedCalls.RemoveAll(c => c.Token.IsCancellationRequested);
        }

        private void ProcessTaskQueues() {
            var highPriorityProcessed = 0;
            
            for (int priority = _taskQueues.Length - 1; priority >= 0; priority--) {
                var queue = _taskQueues[priority];
                var maxSlots = priority >= (int)TaskPriority.High ? _settings.HighPrioritySlots : int.MaxValue;
                var processed = 0;

                for (; processed < maxSlots && _activeTasks.Count < _settings.MaxConcurrentTasks && 
                       queue.TryDequeue(out var task) ;) {
                    ExecuteTask(task);
                    processed++;
                    
                    if (priority >= (int)TaskPriority.High) highPriorityProcessed++;
                }
                
                if (priority >= (int)TaskPriority.High && highPriorityProcessed >= _settings.HighPrioritySlots) break;
            }
        }

        private void ExecuteTask(FrameworkTask frameworkTask) {
            var taskTimer = Stopwatch.StartNew();
            
            var task = Task.Run(async () => {
                try {
                    frameworkTask.SetRunning();
                    lock (_statsLock) _stats += new TaskStats(0, 1, 0, 0, 0);
                    
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(frameworkTask.Token, timeoutCts.Token);
                    
                    await frameworkTask.TaskFunc();
                    
                    taskTimer.Stop();
                    frameworkTask.ExecutionTime = taskTimer.Elapsed;
                    UpdateExecutionStats(taskTimer.Elapsed);
                    
                    lock (_statsLock) _stats += new TaskStats(0, 0, 1, 0, 0);
                    frameworkTask.Complete();
                } catch (OperationCanceledException) {
                    lock (_statsLock) _stats += new TaskStats(0, 0, 0, 0, 1);
                    frameworkTask.Cancel();
                } catch (Exception ex) {
                    lock (_statsLock) _stats += new TaskStats(0, 0, 0, 1, 0);
                    frameworkTask.Fail(ex);
                } finally {
                    if (!string.IsNullOrEmpty(frameworkTask.Name)) 
                        _namedTasks.TryRemove(frameworkTask.Name, out _);
                }
            });

            _activeTasks.TryAdd(task, frameworkTask);
        }

        private void UpdateExecutionStats(TimeSpan executionTime) {
            const int maxSamples = 100;
            lock (_statsLock) {
                _executionTimes.Enqueue((float)executionTime.TotalMilliseconds);
                if (_executionTimes.Count > maxSamples) _executionTimes.Dequeue();
                
                var avgTime = _executionTimes.Average();
                var frameUsage = (FrameTimeUsed / _settings.FrameBudgetMS) * 100f;
                
                _stats = new TaskStats(_stats.Queued, _stats.Started, _stats.Completed, 
                                     _stats.Failed, _stats.Cancelled, _stats.Retried, avgTime, frameUsage);
            }
        }

        private void CleanupCompletedTasks() {
            var toRemove = _activeTasks.Where(kvp => kvp.Key.IsCompleted).Select(kvp => kvp.Key).ToList();
            foreach (var task in toRemove) _activeTasks.TryRemove(task, out _);
        }

        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }
        #endregion

        #region Management & Cleanup
        public void CancelTask(string name) {
            if (_namedTasks.TryGetValue(name, out var task)) task.Cancel();
        }

        public void CancelAllTasks() {
            foreach (var task in _activeTasks.Values) task.Cancel();
            _activeTasks.Clear();
            _namedTasks.Clear();
            
            foreach (var queue in _taskQueues) {
                while (queue.TryDequeue(out _)) { }
            }
            
            lock (_statsLock) _stats += new TaskStats(0, 0, 0, 0, _activeTasks.Count);
        }

        public void PauseTask(string name) {
            if (_namedTasks.TryGetValue(name, out var task) && task.State == TaskState.Running) {
                task.Token.ThrowIfCancellationRequested();
            }
        }

        public IEnumerable<FrameworkTask> GetTasksByPriority(TaskPriority priority) =>
            _activeTasks.Values.Where(t => t.Priority == priority);

        public IEnumerable<FrameworkTask> GetTasksByState(TaskState state) =>
            _activeTasks.Values.Where(t => t.State == state);

        public void OptimizeQueues() {
            var lowPriorityTasks = new List<FrameworkTask>();
            while (_taskQueues[(int)TaskPriority.Low].TryDequeue(out var task)) {
                if (DateTime.UtcNow - task.CreationTime > TimeSpan.FromMinutes(5)) {
                    task.Cancel();
                } else {
                    lowPriorityTasks.Add(task);
                }
            }
            
            foreach (var task in lowPriorityTasks) {
                _taskQueues[(int)TaskPriority.Low].Enqueue(task);
            }
        }

        public void ResetStats() { 
            lock (_statsLock) {
                _stats = new TaskStats();
                _executionTimes.Clear();
            }
        }

        public void Dispose() {
            CancelAllTasks();
            _delayedCalls.Clear();
            _circuits.Clear();
            lock (_mainThreadLock) _mainThreadQueue.Clear();
        }
        #endregion
    }
}
