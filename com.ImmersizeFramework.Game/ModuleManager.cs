using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading.Tasks;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Game {
    public sealed class ModuleManager : MonoBehaviour, IFrameworkService, IFrameworkTickable {
        #region Ultra-Flexible Configuration
        [System.Serializable]
        public readonly struct ModuleManagerSettings {
            public readonly float TotalExperienceDuration, LinearProgressionTolerance;
            public readonly bool EnforceLinearProgression, EnableScoring, EnablePerformanceTracking;
            public readonly bool AutoStartNextModule, ShowGlobalTimer, AllowModuleSkip, AllowModuleRestart;
            public readonly bool EnableDynamicDifficulty, EnableAdaptiveTiming, EnablePauseExperience;
            public readonly bool UseInfiniteExperience, AllowParallelModules, EnableModulePooling;
            public readonly int MaxConcurrentModules, ScoreMultiplierBonus, MaxExperienceRetries;
            public readonly string[] SupportedGameTypes, RequiredTags, OptionalFeatures;
            public readonly float DifficultyAdjustmentRate, TimingAdjustmentRate;

            public ModuleManagerSettings(float totalDuration = 900f, float progressionTolerance = 0.5f,
                                       bool linearProgression = false, bool scoring = true, bool performance = true,
                                       bool autoStart = true, bool showTimer = false, bool allowSkip = true,
                                       bool allowRestart = true, bool dynamicDifficulty = false, bool adaptiveTiming = false,
                                       bool pauseExperience = true, bool infiniteExperience = false, bool parallelModules = false,
                                       bool modulePooling = false, int maxConcurrent = 3, int scoreBonus = 10,
                                       int maxRetries = -1, string[] gameTypes = null, string[] tags = null,
                                       string[] features = null, float difficultyRate = 0.1f, float timingRate = 0.05f) =>
                (TotalExperienceDuration, LinearProgressionTolerance, EnforceLinearProgression, EnableScoring,
                 EnablePerformanceTracking, AutoStartNextModule, ShowGlobalTimer, AllowModuleSkip, AllowModuleRestart,
                 EnableDynamicDifficulty, EnableAdaptiveTiming, EnablePauseExperience, UseInfiniteExperience,
                 AllowParallelModules, EnableModulePooling, MaxConcurrentModules, ScoreMultiplierBonus,
                 MaxExperienceRetries, SupportedGameTypes, RequiredTags, OptionalFeatures,
                 DifficultyAdjustmentRate, TimingAdjustmentRate) =
                (totalDuration, progressionTolerance, linearProgression, scoring, performance, autoStart,
                 showTimer, allowSkip, allowRestart, dynamicDifficulty, adaptiveTiming, pauseExperience,
                 infiniteExperience, parallelModules, modulePooling, maxConcurrent, scoreBonus, maxRetries,
                 gameTypes ?? new[] { "Generic", "Puzzle", "Action", "Educational", "Endless" },
                 tags ?? new string[0], features ?? new string[0], difficultyRate, timingRate);
            public static ModuleManagerSettings ForEducationalExperience() => new(
                totalDuration: 1200f, linearProgression: true, dynamicDifficulty: true,
                adaptiveTiming: true, allowSkip: false, pauseExperience: true);

            public static ModuleManagerSettings ForGameExperience() => new(
                totalDuration: 600f, linearProgression: false, parallelModules: true,
                allowSkip: true, dynamicDifficulty: true, maxConcurrent: 2);

            public static ModuleManagerSettings ForEndlessExperience() => new(
                infiniteExperience: true, modulePooling: true, parallelModules: true,
                dynamicDifficulty: true, adaptiveTiming: true, maxConcurrent: 5);

            public static ModuleManagerSettings ForLinearStoryExperience() => new(
                totalDuration: 1800f, linearProgression: true, allowSkip: false,
                pauseExperience: true, autoStart: true, maxConcurrent: 1);

            public static implicit operator ModuleManagerSettings(float duration) => new(duration);
            public static implicit operator ModuleManagerSettings(bool linearProgression) => new(linearProgression: linearProgression);
        }

        public readonly struct ExperienceMetrics {
            public readonly int TotalModules, CompletedModules, ExpiredModules, FailedModules, SkippedModules;
            public readonly int ParallelModules, PooledModules, RestartedModules;
            public readonly float ElapsedTime, RemainingTime, AverageCompletionTime, AverageEfficiency;
            public readonly int TotalScore, CurrentStreak, BestStreak, BonusPoints, PenaltyPoints;
            public readonly float ProgressionRate, PerformanceScore, DifficultyLevel, PlayerAdaptation;
            public readonly string CurrentGameType, AverageDifficulty, PreferredGameType;
            public readonly Dictionary<string, int> GameTypeStats, DifficultyStats;
            public readonly bool IsAdaptiveMode, IsInfiniteMode, HasParallelModules;

            public ExperienceMetrics(int total, int completed, int expired, int failed, int skipped,
                                   int parallel, int pooled, int restarted, float elapsed, float remaining,
                                   float avgCompletion, float avgEfficiency, int score, int streak, int bestStreak,
                                   int bonusPoints, int penaltyPoints, float progression, float performance,
                                   float difficulty, float adaptation, string gameType, string avgDifficulty,
                                   string preferredType, Dictionary<string, int> gameStats, Dictionary<string, int> diffStats,
                                   bool adaptive, bool infinite, bool hasParallel) =>
                (TotalModules, CompletedModules, ExpiredModules, FailedModules, SkippedModules, ParallelModules,
                 PooledModules, RestartedModules, ElapsedTime, RemainingTime, AverageCompletionTime, AverageEfficiency,
                 TotalScore, CurrentStreak, BestStreak, BonusPoints, PenaltyPoints, ProgressionRate, PerformanceScore,
                 DifficultyLevel, PlayerAdaptation, CurrentGameType, AverageDifficulty, PreferredGameType,
                 GameTypeStats, DifficultyStats, IsAdaptiveMode, IsInfiniteMode, HasParallelModules) =
                (total, completed, expired, failed, skipped, parallel, pooled, restarted, elapsed, remaining,
                 avgCompletion, avgEfficiency, score, streak, bestStreak, bonusPoints, penaltyPoints, progression,
                 performance, difficulty, adaptation, gameType, avgDifficulty, preferredType,
                 gameStats ?? new Dictionary<string, int>(), diffStats ?? new Dictionary<string, int>(),
                 adaptive, infinite, hasParallel);

            public float SuccessRate => TotalModules > 0 ? (float)CompletedModules / TotalModules : 0f;
            public float CompletionRatio => TotalModules > 0 ? (float)(CompletedModules + ExpiredModules) / TotalModules : 0f;

            public override string ToString() =>
                $"Progress: {CompletedModules}/{TotalModules} ({SuccessRate:P}) | Score: {TotalScore} | " +
                $"Time: {ElapsedTime:F1}s | Difficulty: {DifficultyLevel:F1} | Performance: {PerformanceScore:F1}";
        }

        private readonly struct ModuleEntry {
            public readonly GameModule Module;
            public readonly int Order, Priority;
            public readonly float ExpectedStartTime, Weight;
            public readonly bool IsOptional, CanRunInParallel, IsPooled;
            public readonly string Category, Prerequisites;
            public readonly string[] Tags;

            public ModuleEntry(GameModule module, int order, float expectedStart, bool optional = false,
                             bool parallel = false, bool pooled = false, int priority = 0, float weight = 1f,
                             string category = "", string prerequisites = "", string[] tags = null) =>
                (Module, Order, ExpectedStartTime, IsOptional, CanRunInParallel, IsPooled, Priority, Weight,
                 Category, Prerequisites, Tags) =
                (module, order, expectedStart, optional, parallel, pooled, priority, weight, category,
                 prerequisites, tags ?? new string[0]);
        }
        #endregion

        #region Enhanced Properties & Events
        public bool IsInitialized { get; private set; }
        public bool IsExperienceActive { get; private set; }
        public bool IsExperiencePaused { get; private set; }
        public float GlobalElapsedTime { get; private set; }
        public float GlobalRemainingTime => _settings.UseInfiniteExperience ? float.MaxValue : 
            Mathf.Max(0f, _settings.TotalExperienceDuration - GlobalElapsedTime);
        public ExperienceMetrics CurrentMetrics { get; private set; }
        public int Priority => 8;
        public string CurrentExperienceType { get; private set; } = "Generic";
        public float CurrentDifficultyLevel { get; private set; } = 1f;
        public bool IsAdaptiveMode => _settings.EnableDynamicDifficulty || _settings.EnableAdaptiveTiming;
        public event Action OnExperienceStarted, OnExperiencePaused, OnExperienceResumed, OnExperienceRestarted;
        public event Action<ExperienceMetrics> OnExperienceCompleted, OnMetricsUpdated;
        public event Action<GameModule> OnModuleActivated, OnModulePooled, OnModuleDeactivated;
        public event Action<GameModule, GameModule.ModuleResult> OnModuleCompleted;
        public event Action<float> OnDifficultyAdjusted;
        public event Action<string> OnGameTypeChanged;
        public event Action<GameModule[]> OnParallelModulesStarted;

        [SerializeField] private ModuleManagerSettings _settings = new();
        private readonly List<ModuleEntry> _moduleSequence = new();
        private readonly List<ModuleEntry> _modulePool = new();
        private readonly ConcurrentDictionary<string, GameModule> _moduleRegistry = new();
        private readonly List<GameModule.ModuleResult> _completedResults = new();
        private readonly ConcurrentQueue<GameModule> _activeModules = new();
        private readonly Dictionary<string, int> _gameTypeStats = new();
        private readonly Dictionary<string, int> _difficultyStats = new();
        private readonly Queue<ModuleEntry> _pendingModules = new();

        private DateTime _experienceStartTime;
        private int _currentModuleIndex, _experienceRetries;
        private int _totalScore, _currentStreak, _bestStreak, _bonusPoints, _penaltyPoints;
        private int _skippedModules, _restartedModules, _pooledModules;
        private float _metricsUpdateTimer, _difficultyAdjustmentTimer, _playerAdaptationScore;
        private bool _experienceCompleted;
        private string _preferredGameType = "";
        public GameModule CurrentModule => _currentModuleIndex < _moduleSequence.Count ? 
            _moduleSequence[_currentModuleIndex].Module : null;
        public GameModule this[int index] => index >= 0 && index < _moduleSequence.Count ? 
            _moduleSequence[index].Module : null;
        public GameModule this[string moduleID] => _moduleRegistry.TryGetValue(moduleID, out var module) ? module : null;
        public bool HasActiveModules => !_activeModules.IsEmpty;
        public bool HasParallelModules => _settings.AllowParallelModules && _activeModules.Count > 1;
        public int ActiveModuleCount => _activeModules.Count;
        public float OverallProgress => _moduleSequence.Count > 0 ? (float)_currentModuleIndex / _moduleSequence.Count : 0f;
        #endregion

        #region Initialization
        public async Task InitializeAsync() {
            if (IsInitialized) return;

            DiscoverModules();
            SetupModuleSequence();
            
            IsInitialized = true;
        }

        public void Initialize() => _ = InitializeAsync();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DiscoverModules() {
            var modules = FindObjectsOfType<GameModule>();
            _moduleRegistry.Clear();

            foreach (var module in modules) {
                _moduleRegistry[module.ModuleID] = module;
                module.OnModuleStarted += HandleModuleStarted;
                module.OnModuleCompleted += HandleModuleCompleted;
                module.OnModuleExpired += HandleModuleExpired;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetupModuleSequence() {
            _moduleSequence.Clear();
            var expectedDuration = _settings.TotalExperienceDuration / _moduleRegistry.Count;
            var currentTime = 0f;

            var sortedModules = _moduleRegistry.Values
                .OrderBy(m => m.transform.GetSiblingIndex())
                .ToArray();

            for (int i = 0; i < sortedModules.Length; i++) {
                var module = sortedModules[i];
                _moduleSequence.Add(new ModuleEntry(module, i, currentTime));
                currentTime += expectedDuration;
            }
        }
        #endregion

        #region Experience Management
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StartExperience() {
            if (IsExperienceActive || _moduleSequence.Count == 0) return;

            IsExperienceActive = true;
            _experienceStartTime = DateTime.UtcNow;
            GlobalElapsedTime = 0f;
            _currentModuleIndex = 0;
            _totalScore = 0;
            _currentStreak = 0;
            _experienceCompleted = false;
            _completedResults.Clear();

            OnExperienceStarted?.Invoke();
            
            if (_settings.AutoStartNextModule)
                ActivateNextModule();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ActivateNextModule() {
            if (!IsExperienceActive || _currentModuleIndex >= _moduleSequence.Count) return;

            var moduleEntry = _moduleSequence[_currentModuleIndex];
            
            if (_settings.EnforceLinearProgression && !CanActivateModule(moduleEntry)) {
                Debug.LogWarning($"[ModuleManager] Cannot activate {moduleEntry.Module.ModuleID} - progression constraint violated");
                return;
            }

            var module = moduleEntry.Module;
            _activeModules.Enqueue(module);
            module.StartModule();
            
            OnModuleActivated?.Invoke(module);
            _currentModuleIndex++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanActivateModule(ModuleEntry entry) {
            if (!_settings.EnforceLinearProgression) return true;

            var expectedTime = entry.ExpectedStartTime;
            var tolerance = _settings.LinearProgressionTolerance * _settings.TotalExperienceDuration;
            
            return Mathf.Abs(GlobalElapsedTime - expectedTime) <= tolerance;
        }

        public void SkipCurrentModule() {
            if (!_settings.AllowModuleSkip || CurrentModule == null) return;
            
            CurrentModule.ExpireModule();
        }

        public void CompleteExperience() {
            if (!IsExperienceActive || _experienceCompleted) return;

            _experienceCompleted = true;
            IsExperienceActive = false;

            var metrics = CalculateExperienceMetrics();
            OnExperienceCompleted?.Invoke(metrics);
        }
        #endregion

        #region Module Event Handlers
        private void HandleModuleStarted(GameModule module) {
            Debug.Log($"[ModuleManager] Module started: {module.ModuleID}");
        }

        private void HandleModuleCompleted(GameModule module, GameModule.ModuleResult result) {
            _activeModules.TryDequeue(out _);
            _completedResults.Add(result);

            if (_settings.EnableScoring) {
                UpdateScoring(result);
            }

            Debug.Log($"[ModuleManager] Module completed: {result}");

            if (_settings.AutoStartNextModule && _currentModuleIndex < _moduleSequence.Count) {
                ActivateNextModule();
            } else if (_currentModuleIndex >= _moduleSequence.Count) {
                CompleteExperience();
            }
        }

        private void HandleModuleExpired(GameModule module) {
            Debug.Log($"[ModuleManager] Module expired: {module.ModuleID}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateScoring(GameModule.ModuleResult result) {
            _totalScore += result.Score;

            if (result.FinalStatus == ModuleStatus.Completed) {
                _currentStreak++;
                _bestStreak = Mathf.Max(_bestStreak, _currentStreak);
            } else {
                _currentStreak = 0;
            }

            if (_currentStreak >= 3) {
                var bonus = _currentStreak * _settings.ScoreMultiplierBonus;
                _totalScore += bonus;
            }
        }
        #endregion

        #region Metrics & Analytics
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ExperienceMetrics CalculateExperienceMetrics() {
            var completed = _completedResults.Count(r => r.FinalStatus == ModuleStatus.Completed);
            var expired = _completedResults.Count(r => r.FinalStatus == ModuleStatus.Expired);
            var failed = _completedResults.Count(r => r.FinalStatus == ModuleStatus.Failed);
            var skipped = _completedResults.Count(r => r.WasSkipped);
            
            var avgCompletion = _completedResults.Count > 0 ? 
                _completedResults.Average(r => r.CompletionTime) : 0f;
            
            var avgEfficiency = _completedResults.Count > 0 ? 
                _completedResults.Average(r => r.EfficiencyScore) : 0f;
            
            var expectedProgress = GlobalElapsedTime / _settings.TotalExperienceDuration;
            var actualProgress = (float)_currentModuleIndex / _moduleSequence.Count;
            var progressionRate = expectedProgress > 0f ? actualProgress / expectedProgress : 0f;
            
            var performanceScore = CalculatePerformanceScore();
            
            var gameTypeStats = _completedResults
                .GroupBy(r => r.GameType)
                .ToDictionary(g => g.Key, g => g.Count());
            
            var difficultyStats = _completedResults
                .GroupBy(r => r.Difficulty)
                .ToDictionary(g => g.Key, g => g.Count());
            
            var currentGameType = CurrentModule?.GameType ?? "None";
            var avgDifficulty = _completedResults.Count > 0 ? 
                _completedResults.GroupBy(r => r.Difficulty).OrderByDescending(g => g.Count()).First().Key : "Normal";
            var preferredType = gameTypeStats.Count > 0 ? 
                gameTypeStats.OrderByDescending(kvp => kvp.Value).First().Key : "Generic";

            return new ExperienceMetrics(
                _moduleSequence.Count, completed, expired, failed, skipped,
                0, 0, 0,
                GlobalElapsedTime, GlobalRemainingTime, avgCompletion, avgEfficiency,
                _totalScore, _currentStreak, _bestStreak,
                0, 0,
                progressionRate, performanceScore,
                1.0f, 1.0f,
                currentGameType, avgDifficulty, preferredType,
                gameTypeStats, difficultyStats,
                false,
                _settings.UseInfiniteExperience,
                false
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculatePerformanceScore() {
            if (_completedResults.Count == 0) return 0f;

            var completionRate = (float)_completedResults.Count(r => r.FinalStatus == ModuleStatus.Completed) / _completedResults.Count;
            var timeEfficiency = _completedResults.Average(r => r.RemainingTime / 180f);
            var streakBonus = _bestStreak / (float)_moduleSequence.Count;

            return (completionRate * 0.5f + timeEfficiency * 0.3f + streakBonus * 0.2f) * 100f;
        }

        public void LogMetrics() {
            var metrics = CalculateExperienceMetrics();
            Debug.Log($"[ModuleManager] Experience Metrics: {metrics}");
            
            foreach (var result in _completedResults.TakeLast(5))
                Debug.Log($"[ModuleManager] Recent Result: {result}");
        }
        #endregion

        #region Framework Integration
        public void Tick(float deltaTime) {
            if (!IsExperienceActive) return;

            GlobalElapsedTime += deltaTime;
            _metricsUpdateTimer += deltaTime;

            if (GlobalRemainingTime <= 0f && !_experienceCompleted) {
                CompleteExperience();
                return;
            }

            if (_metricsUpdateTimer >= 1f) {
                _metricsUpdateTimer = 0f;
                CurrentMetrics = CalculateExperienceMetrics();
                OnMetricsUpdated?.Invoke(CurrentMetrics);
            }
        }

        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }

        public void Dispose() {
            IsInitialized = false;
            IsExperienceActive = false;
            
            foreach (var module in _moduleRegistry.Values) {
                if (module != null) {
                    module.OnModuleStarted -= HandleModuleStarted;
                    module.OnModuleCompleted -= HandleModuleCompleted;
                    module.OnModuleExpired -= HandleModuleExpired;
                }
            }
            
            _moduleRegistry.Clear();
            _moduleSequence.Clear();
            _completedResults.Clear();
        }
        #endregion

        #region Public Utilities
        public void RegisterModule(GameModule module) {
            if (module == null || _moduleRegistry.ContainsKey(module.ModuleID)) return;

            _moduleRegistry[module.ModuleID] = module;
            module.OnModuleStarted += HandleModuleStarted;
            module.OnModuleCompleted += HandleModuleCompleted;
            module.OnModuleExpired += HandleModuleExpired;
        }

        public bool UnregisterModule(string moduleID) {
            if (!_moduleRegistry.TryGetValue(moduleID, out var module)) return false;

            module.OnModuleStarted -= HandleModuleStarted;
            module.OnModuleCompleted -= HandleModuleCompleted;
            module.OnModuleExpired -= HandleModuleExpired;
            
            return _moduleRegistry.TryRemove(moduleID, out _);
        }

        public GameModule.ModuleResult[] GetCompletedResults() => _completedResults.ToArray();

        public void ConfigureSettings(ModuleManagerSettings newSettings) {
            if (IsExperienceActive) return;
            _settings = newSettings;
            SetupModuleSequence();
        }

        public float GetModuleProgressionTime(int moduleIndex) =>
            moduleIndex >= 0 && moduleIndex < _moduleSequence.Count ? 
                _moduleSequence[moduleIndex].ExpectedStartTime : -1f;
        #endregion

        #region Operator Overloads
        public static implicit operator bool(ModuleManager manager) => manager?.IsExperienceActive == true;
        public static implicit operator int(ModuleManager manager) => manager?._currentModuleIndex ?? 0;
        public static implicit operator float(ModuleManager manager) => manager?.GlobalRemainingTime ?? 0f;
        #endregion
    }
}
