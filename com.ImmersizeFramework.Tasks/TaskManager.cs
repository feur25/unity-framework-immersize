using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Linq;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Tasks {
    /// @(name="TaskManager", description="Advanced async task manager with priority queues, frame budgets, and parallel execution capabilities")
    public class TaskManager : IFrameworkService, IFrameworkTickable {
        
        #region Task Configuration & Nested Types
        public enum TaskPriority : byte { Low, Normal, High, Critical }
        
        [System.Serializable]
        public readonly struct TaskSettings {
            public readonly int MaxConcurrentTasks;
            public readonly float FrameBudgetMS;
            public readonly int HighPrioritySlots;
            public readonly float TimeoutSeconds;
            public readonly bool EnableProfiling;

            public TaskSettings(int maxConcurrent = 0, float frameBudget = 16.67f, int highSlots = 2, 
                               float timeout = 30f, bool profiling = true) {
                MaxConcurrentTasks = maxConcurrent > 0 ? maxConcurrent : Environment.ProcessorCount * 2;
                FrameBudgetMS = frameBudget;
                HighPrioritySlots = highSlots;
                TimeoutSeconds = timeout;
                EnableProfiling = profiling;
            }
        }

        public sealed class FrameworkTask {
            public Func<Task> TaskFunc { get; set; }
            public TaskPriority Priority { get; set; }
            public CancellationToken Token { get; set; }
            public DateTime CreationTime { get; set; } = DateTime.UtcNow;
            public string Name { get; set; }
            public TaskCompletionSource<object> TCS { get; set; } = new();
            
            public void Complete() => TCS.TrySetResult(null);
            public void Fail(Exception ex) => TCS.TrySetException(ex);
            public void Cancel() => TCS.TrySetCanceled();
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
            public readonly int Queued, Started, Completed, Failed, Cancelled;
            
            public TaskStats(int queued, int started, int completed, int failed, int cancelled) {
                Queued = queued; Started = started; Completed = completed; 
                Failed = failed; Cancelled = cancelled;
            }

            public static TaskStats operator +(TaskStats a, TaskStats b) => 
                new(a.Queued + b.Queued, a.Started + b.Started, a.Completed + b.Completed, 
                    a.Failed + b.Failed, a.Cancelled + b.Cancelled);

            public override string ToString() => 
                $"Q:{Queued} S:{Started} C:{Completed} F:{Failed} X:{Cancelled}";
        }
        #endregion

        #region Properties & Fields
        public bool IsInitialized { get; private set; }
        public int ActiveTasks => _activeTasks.Count;
        public int QueuedTasks => _taskQueues.Sum(q => q.Count);
        public float FrameTimeUsed { get; private set; }
        public TaskStats Stats { get { lock (_statsLock) return _stats; } }
        public int Priority => 1;

        private readonly TaskSettings _settings;
        private readonly ConcurrentQueue<FrameworkTask>[] _taskQueues;
        private readonly ConcurrentDictionary<Task, FrameworkTask> _activeTasks = new();
        private readonly Queue<Action> _mainThreadQueue = new();
        private readonly List<DelayedCall> _delayedCalls = new();
        private readonly object _mainThreadLock = new();
        private readonly object _statsLock = new();
        private readonly System.Diagnostics.Stopwatch _frameWatch = new();
        private readonly float _frameBudgetTicks;
        private TaskStats _stats;

        public FrameworkTask this[string name] => 
            _activeTasks.Values.FirstOrDefault(t => t.Name == name);
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
            lock (_statsLock) _stats = _stats + new TaskStats(1, 0, 0, 0, 0);
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

                try { call.Action?.Invoke(); } catch (Exception ex) { Debug.LogError($"[TaskManager] Delayed call error: {ex}"); }
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
            var task = Task.Run(async () => {
                try {
                    lock (_statsLock) _stats += new TaskStats(0, 1, 0, 0, 0);
                    
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(frameworkTask.Token, timeoutCts.Token);
                    
                    await frameworkTask.TaskFunc();
                    lock (_statsLock) _stats += new TaskStats(0, 0, 1, 0, 0);
                    frameworkTask.Complete();
                } catch (OperationCanceledException) {
                    lock (_statsLock) _stats += new TaskStats(0, 0, 0, 0, 1);
                    frameworkTask.Cancel();
                } catch (Exception ex) {
                    lock (_statsLock) _stats += new TaskStats(0, 0, 0, 1, 0);
                    if (_settings.EnableProfiling) Debug.LogError($"[TaskManager] '{frameworkTask.Name}' failed: {ex}");
                    frameworkTask.Fail(ex);
                }
            });

            _activeTasks.TryAdd(task, frameworkTask);
        }

        private void CleanupCompletedTasks() {
            var toRemove = _activeTasks.Where(kvp => kvp.Key.IsCompleted).Select(kvp => kvp.Key).ToList();
            foreach (var task in toRemove) _activeTasks.TryRemove(task, out _);
        }

        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }
        #endregion

        #region Management & Cleanup
        public void CancelAllTasks() {
            foreach (var task in _activeTasks.Values) task.Cancel();
            _activeTasks.Clear();
            
            foreach (var queue in _taskQueues) {
                while (queue.TryDequeue(out _)) { }
            }
            
            lock (_statsLock) _stats += new TaskStats(0, 0, 0, 0, _activeTasks.Count);
        }

        public void LogStats() => Debug.Log($"[TaskManager] {Stats} | Active:{ActiveTasks} Queued:{QueuedTasks} Frame:{FrameTimeUsed:F2}ms");
        
        public void ResetStats() { lock (_statsLock) _stats = new TaskStats(); }

        public void Dispose() {
            CancelAllTasks();
            _delayedCalls.Clear();
            lock (_mainThreadLock) _mainThreadQueue.Clear();
        }
        #endregion
    }
}
