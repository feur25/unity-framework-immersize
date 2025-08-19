using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Linq;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Game {
    public enum ModuleStatus : byte { Inactive, Active, Completed, Expired, Failed }

    public abstract class GameModule : MonoBehaviour {
        #region Ultra-Flexible Module Configuration
        [System.Serializable]
        public readonly struct ModuleSettings {
            public readonly string ModuleName, AudioClipName, TargetImageName, VideoClipName, PrefabName;
            public readonly float DurationSeconds, ScoreMultiplier, DelayBeforeStart, FadeInDuration, FadeOutDuration;
            public readonly bool AutoComplete, RequireValidation, ShowTimer, CanPause, CanRestart, CanSkip;
            public readonly bool UseInfiniteTime, AllowMultipleAttempts, EnableHints, EnableTutorial;
            public readonly int MaxAttempts, PointsOnComplete, PenaltyOnExpire, BonusPointsPerSecond, HintCost;
            public readonly Vector3 SpawnPosition, SpawnRotation, SpawnScale;
            public readonly string[] CustomTags, RequiredComponents, OptionalAssets;
            public readonly float[] CustomFloatParams;
            public readonly int[] CustomIntParams;
            public readonly bool[] CustomBoolParams;

            public ModuleSettings(string name = "Module", string audio = null, string image = null, string video = null, string prefab = null,
                                float duration = 180f, float scoreMultiplier = 1f, float delayStart = 0f, float fadeIn = 0f, float fadeOut = 0f,
                                bool autoComplete = true, bool requireValidation = false, bool showTimer = true, bool canPause = true,
                                bool canRestart = true, bool canSkip = false, bool infiniteTime = false, bool multipleAttempts = true,
                                bool enableHints = false, bool enableTutorial = false, int maxAttempts = -1, int pointsComplete = 100,
                                int penaltyExpire = -25, int bonusPerSecond = 0, int hintCost = 5,
                                Vector3 spawnPos = default, Vector3 spawnRot = default, Vector3 spawnScale = default,
                                string[] tags = null, string[] requiredComponents = null, string[] optionalAssets = null,
                                float[] customFloats = null, int[] customInts = null, bool[] customBools = null) =>
                (ModuleName, AudioClipName, TargetImageName, VideoClipName, PrefabName, DurationSeconds, ScoreMultiplier,
                 DelayBeforeStart, FadeInDuration, FadeOutDuration, AutoComplete, RequireValidation, ShowTimer, CanPause,
                 CanRestart, CanSkip, UseInfiniteTime, AllowMultipleAttempts, EnableHints, EnableTutorial, MaxAttempts,
                 PointsOnComplete, PenaltyOnExpire, BonusPointsPerSecond, HintCost, SpawnPosition, SpawnRotation, SpawnScale,
                 CustomTags, RequiredComponents, OptionalAssets, CustomFloatParams, CustomIntParams, CustomBoolParams) =
                (name, audio, image, video, prefab, duration, scoreMultiplier, delayStart, fadeIn, fadeOut, autoComplete,
                 requireValidation, showTimer, canPause, canRestart, canSkip, infiniteTime, multipleAttempts, enableHints,
                 enableTutorial, maxAttempts, pointsComplete, penaltyExpire, bonusPerSecond, hintCost,
                 spawnPos == default ? Vector3.zero : spawnPos, spawnRot == default ? Vector3.zero : spawnRot,
                 spawnScale == default ? Vector3.one : spawnScale, tags ?? new string[0], requiredComponents ?? new string[0],
                 optionalAssets ?? new string[0], customFloats ?? new float[0], customInts ?? new int[0], customBools ?? new bool[0]);
            public static ModuleSettings ForPuzzleGame(string name, float duration = 300f) => 
                new(name, duration: duration, enableHints: true, bonusPerSecond: 1, maxAttempts: -1);
            
            public static ModuleSettings ForActionGame(string name, float duration = 120f) => 
                new(name, duration: duration, canPause: false, bonusPerSecond: 2, maxAttempts: 3);
            
            public static ModuleSettings ForEducationalGame(string name, float duration = 240f) => 
                new(name, duration: duration, enableTutorial: true, enableHints: true, multipleAttempts: true);
            
            public static ModuleSettings ForEndlessGame(string name) => 
                new(name, infiniteTime: true, canSkip: false, bonusPerSecond: 1);
            
            public static ModuleSettings ForTimedChallenge(string name, float duration = 60f) => 
                new(name, duration: duration, canPause: false, canRestart: false, bonusPerSecond: 5);

            public static implicit operator ModuleSettings(string name) => new(name);
            public static implicit operator ModuleSettings(float duration) => new(duration: duration);
        }

        public readonly struct ModuleResult {
            public readonly string ModuleID, GameType, Difficulty;
            public readonly ModuleStatus FinalStatus;
            public readonly float CompletionTime, RemainingTime, EfficiencyScore;
            public readonly int Score, Attempts, HintsUsed, BonusPoints, PenaltyPoints;
            public readonly DateTime StartTime, EndTime;
            public readonly Vector3 FinalPosition, FinalRotation;
            public readonly Dictionary<string, object> CustomData;
            public readonly bool UsedTutorial, WasPaused, WasSkipped;

            public ModuleResult(string id, ModuleStatus status, float completionTime, float remainingTime,
                              int score, int attempts, DateTime start, DateTime end, string gameType = "",
                              string difficulty = "", float efficiency = 1f, int hintsUsed = 0, int bonusPoints = 0,
                              int penaltyPoints = 0, Vector3 finalPos = default, Vector3 finalRot = default,
                              Dictionary<string, object> customData = null, bool usedTutorial = false,
                              bool wasPaused = false, bool wasSkipped = false) =>
                (ModuleID, FinalStatus, CompletionTime, RemainingTime, Score, Attempts, StartTime, EndTime,
                 GameType, Difficulty, EfficiencyScore, HintsUsed, BonusPoints, PenaltyPoints, FinalPosition,
                 FinalRotation, CustomData, UsedTutorial, WasPaused, WasSkipped) =
                (id, status, completionTime, remainingTime, score, attempts, start, end, gameType, difficulty,
                 efficiency, hintsUsed, bonusPoints, penaltyPoints, finalPos, finalRot, customData ?? new Dictionary<string, object>(),
                 usedTutorial, wasPaused, wasSkipped);

            public T GetCustomData<T>(string key, T defaultValue = default) =>
                CustomData.TryGetValue(key, out var value) && value is T result ? result : defaultValue;

            public override string ToString() =>
                $"{ModuleID} [{GameType}]: {FinalStatus} in {CompletionTime:F1}s (Score: {Score}, Efficiency: {EfficiencyScore:P})";
        }
        #endregion

        #region Enhanced Properties & Events
        public string ModuleID { get; private set; }
        public string GameType { get; protected set; } = "Generic";
        public string Difficulty { get; protected set; } = "Normal";
        public ModuleStatus Status { get; private set; } = ModuleStatus.Inactive;
        public float ElapsedTime { get; private set; }
        public float RemainingTime => _settings.UseInfiniteTime ? float.MaxValue : Mathf.Max(0f, _settings.DurationSeconds - ElapsedTime);
        public bool IsActive => Status == ModuleStatus.Active;
        public bool IsCompleted => Status == ModuleStatus.Completed || Status == ModuleStatus.Expired;
        public bool IsPaused { get; private set; }
        public int CurrentAttempts { get; private set; }
        public int CalculatedScore { get; private set; }
        public int HintsUsed { get; private set; }
        public bool HasUsedTutorial { get; private set; }

        public event Action<GameModule> OnModuleStarted, OnModulePaused, OnModuleResumed;
        public event Action<GameModule, ModuleResult> OnModuleCompleted;
        public event Action<GameModule> OnModuleExpired, OnModuleSkipped, OnModuleFailed;
        public event Action<GameModule, int> OnAttemptMade, OnScoreChanged;
        public event Action<GameModule, string> OnHintRequested, OnTutorialStepCompleted;
        public event Action<GameModule, Vector3> OnPositionChanged;
        public event Action<GameModule, string, object> OnCustomEvent;

        [SerializeField] protected ModuleSettings _settings = new();
        protected DateTime _startTime;
        protected AudioSource _audioSource;
        protected Renderer _targetRenderer;
        protected VideoPlayer _videoPlayer;
        protected GameObject _spawnedPrefab;
        protected bool _hasValidated, _tutorialActive;
        protected float _delayTimer, _fadeTimer;
        protected Dictionary<string, object> _moduleData = new();
        protected List<string> _availableHints = new();
        protected Queue<string> _tutorialSteps = new();
        #endregion

        #region Advanced Initialization & Setup
        protected virtual void Awake() {
            ModuleID = _settings.ModuleName.Length > 0 ? _settings.ModuleName : $"Module_{GetInstanceID()}";
            SetupAudioSource();
            SetupVideoPlayer();
            SetupTargetImage();
            SetupPrefab();
            InitializeHints();
            InitializeTutorial();
            InitializeCustomData();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetupAudioSource() {
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null && !string.IsNullOrEmpty(_settings.AudioClipName)) {
                _audioSource = gameObject.AddComponent<AudioSource>();
            }
            
            if (_audioSource != null) {
                _audioSource.playOnAwake = false;
                _audioSource.loop = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetupVideoPlayer() {
            if (!string.IsNullOrEmpty(_settings.VideoClipName)) {
                _videoPlayer = GetComponent<VideoPlayer>();
                if (_videoPlayer == null) _videoPlayer = gameObject.AddComponent<VideoPlayer>();
                
                _videoPlayer.playOnAwake = false;
                _videoPlayer.isLooping = false;
                LoadVideoAsync();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetupTargetImage() {
            _targetRenderer = GetComponent<Renderer>();
            if (_targetRenderer != null && !string.IsNullOrEmpty(_settings.TargetImageName))
                LoadTargetImageAsync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetupPrefab() {
            if (!string.IsNullOrEmpty(_settings.PrefabName))
                LoadPrefabAsync();
        }

        protected virtual void InitializeHints() {
            if (!_settings.EnableHints) return;

            _availableHints.Clear();
            PopulateHints();
        }

        protected virtual void InitializeTutorial() {
            if (!_settings.EnableTutorial) return;

            _tutorialSteps.Clear();
            PopulateTutorialSteps();
        }

        protected virtual void InitializeCustomData() {
            _moduleData.Clear();
            _moduleData["moduleType"] = GetType().Name;
            _moduleData["spawnTime"] = DateTime.UtcNow;
            _moduleData["position"] = transform.position;
            _moduleData["rotation"] = transform.rotation;
            _moduleData["scale"] = transform.localScale;
            
            for (int i = 0; i < _settings.CustomFloatParams.Length; i++)
                _moduleData[$"customFloat_{i}"] = _settings.CustomFloatParams[i];
            for (int i = 0; i < _settings.CustomIntParams.Length; i++)
                _moduleData[$"customInt_{i}"] = _settings.CustomIntParams[i];
            for (int i = 0; i < _settings.CustomBoolParams.Length; i++)
                _moduleData[$"customBool_{i}"] = _settings.CustomBoolParams[i];
        }

        protected virtual void PopulateHints() { }
        protected virtual void PopulateTutorialSteps() { }

        protected virtual async void LoadTargetImageAsync() {
            var mediaManager = FrameworkCore.Instance?.GetService<Media.MediaManager>();
            if (mediaManager != null) {
                var imageAsset = await mediaManager.LoadAsync<Media.ImageAsset>(_settings.TargetImageName);
                if (imageAsset?.TypedAsset != null && _targetRenderer != null)
                    _targetRenderer.material.mainTexture = imageAsset.TypedAsset;
            }
        }

        protected virtual async void LoadVideoAsync() {
            var mediaManager = FrameworkCore.Instance?.GetService<Media.MediaManager>();
            if (mediaManager != null && _videoPlayer != null) {
                try {
                    var videoAsset = await mediaManager.LoadAsync<Media.MediaAsset<VideoClip>>(_settings.VideoClipName);
                    if (videoAsset?.TypedAsset != null) {
                        _videoPlayer.clip = videoAsset.TypedAsset;
                    }
                } catch {
                    Debug.LogWarning($"[GameModule] Could not load video: {_settings.VideoClipName}");
                }
            }
        }

        protected virtual async void LoadPrefabAsync() {
            var mediaManager = FrameworkCore.Instance?.GetService<Media.MediaManager>();
            if (mediaManager != null) {
                try {
                    var prefabAsset = await mediaManager.LoadAsync<Media.MediaAsset<GameObject>>(_settings.PrefabName);
                    if (prefabAsset?.TypedAsset != null) {
                        _spawnedPrefab = Instantiate(prefabAsset.TypedAsset, _settings.SpawnPosition, Quaternion.Euler(_settings.SpawnRotation));
                        _spawnedPrefab.transform.localScale = _settings.SpawnScale;
                        _spawnedPrefab.transform.SetParent(transform);
                    }
                } catch {
                    Debug.LogWarning($"[GameModule] Could not load prefab: {_settings.PrefabName}");
                }
            }
        }
        #endregion

        #region Universal Module Lifecycle
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void StartModule() {
            if (Status != ModuleStatus.Inactive) return;

            Status = ModuleStatus.Active;
            ElapsedTime = 0f;
            CurrentAttempts = 0;
            HintsUsed = 0;
            _hasValidated = false;
            IsPaused = false;
            _startTime = DateTime.UtcNow;
            CalculatedScore = 0;
            _delayTimer = 0f;
            _fadeTimer = 0f;

            if (_settings.DelayBeforeStart > 0f) StartCoroutine(DelayedStart());
            else ActivateModule();
        }

        private System.Collections.IEnumerator DelayedStart() {
            yield return new WaitForSeconds(_settings.DelayBeforeStart);
            ActivateModule();
        }

        protected virtual void ActivateModule() {
            OnModuleActivated();
            OnModuleStarted?.Invoke(this);

            if (_settings.EnableTutorial && _tutorialSteps.Count > 0) StartTutorial();
        }

        protected virtual void Update() {
            if (Status != ModuleStatus.Active || IsPaused) return;

            ElapsedTime += Time.deltaTime;

            if (!_settings.UseInfiniteTime && RemainingTime <= 0f) {
                ExpireModule();
                return;
            }

            if (_settings.BonusPointsPerSecond > 0) {
                var bonusThisFrame = Mathf.RoundToInt(_settings.BonusPointsPerSecond * Time.deltaTime);
                if (bonusThisFrame > 0) {
                    CalculatedScore += bonusThisFrame;
                    OnScoreChanged?.Invoke(this, CalculatedScore);
                }
            }

            UpdateModuleLogic();
            UpdateTutorial();
        }

        protected virtual void UpdateModuleLogic() { }
        protected virtual void UpdateTutorial() { }

        protected virtual void OnModuleActivated() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void PauseModule() {
            if (!IsActive || !_settings.CanPause || IsPaused) return;

            IsPaused = true;
            OnModulePaused?.Invoke(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void ResumeModule() {
            if (!IsActive || !IsPaused) return;

            IsPaused = false;
            OnModuleResumed?.Invoke(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void SkipModule() {
            if (!IsActive || !_settings.CanSkip) return;

            Status = ModuleStatus.Expired;
            OnModuleSkipped?.Invoke(this);
            OnModuleDeactivated();

            var result = CreateResult(true);
            OnModuleCompleted?.Invoke(this, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void CompleteModule(bool forced = false) {
            if (Status != ModuleStatus.Active) return;

            Status = ModuleStatus.Completed;
            CalculateScore();
            
            PlayCompletionAudio();
            OnModuleDeactivated();

            var result = CreateResult(false, forced);
            OnModuleCompleted?.Invoke(this, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void ExpireModule() {
            if (Status != ModuleStatus.Active) return;

            Status = ModuleStatus.Expired;
            
            if (_settings.AutoComplete) {
                CalculateScore(true);
                PlayCompletionAudio();
            }

            OnModuleDeactivated();
            OnModuleExpired?.Invoke(this);

            var result = CreateResult();
            OnModuleCompleted?.Invoke(this, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void FailModule(string reason = "") {
            if (Status != ModuleStatus.Active) return;

            Status = ModuleStatus.Failed;
            _moduleData["failureReason"] = reason;
            
            OnModuleDeactivated();
            OnModuleFailed?.Invoke(this);

            var result = CreateResult();
            OnModuleCompleted?.Invoke(this, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void RestartModule() {
            if (!_settings.CanRestart) return;

            if (IsActive) {
                FailModule("Restarted");
            }

            ResetModule();
            StartModule();
        }

        protected virtual void OnModuleDeactivated() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void ResetModule() {
            Status = ModuleStatus.Inactive;
            ElapsedTime = 0f;
            CurrentAttempts = 0;
            HintsUsed = 0;
            CalculatedScore = 0;
            _hasValidated = false;
            IsPaused = false;
            HasUsedTutorial = false;
            _tutorialActive = false;
            _moduleData.Clear();
            InitializeCustomData();
        }
        #endregion

        #region Universal Interaction & Validation System
        public virtual void MakeAttempt() {
            if (Status != ModuleStatus.Active || IsPaused) return;
            
            CurrentAttempts++;
            OnAttemptMade?.Invoke(this, CurrentAttempts);

            if (_settings.MaxAttempts > 0 && CurrentAttempts >= _settings.MaxAttempts) {
                if (_settings.AllowMultipleAttempts) {
                    CurrentAttempts = 0;
                    CalculatedScore = Mathf.Max(0, CalculatedScore - 10);
                    OnScoreChanged?.Invoke(this, CalculatedScore);
                } else {
                    FailModule("Max attempts reached");
                    return;
                }
            }

            ProcessAttempt();
        }

        protected virtual void ProcessAttempt() { }

        public virtual bool ValidateModule() {
            if (Status != ModuleStatus.Active || _hasValidated || IsPaused) return false;
            
            _hasValidated = true;
            
            if (!_settings.RequireValidation || CanValidate()) {
                CompleteModule();
                return true;
            }
            
            return false;
        }

        protected virtual bool CanValidate() => true;

        public virtual void RequestHint() {
            if (!_settings.EnableHints || HintsUsed >= _availableHints.Count) return;

            var hint = GetNextHint();
            if (!string.IsNullOrEmpty(hint)) {
                HintsUsed++;
                CalculatedScore = Mathf.Max(0, CalculatedScore - _settings.HintCost);
                OnHintRequested?.Invoke(this, hint);
                OnScoreChanged?.Invoke(this, CalculatedScore);
            }
        }

        protected virtual string GetNextHint() =>
            HintsUsed < _availableHints.Count ? _availableHints[HintsUsed] : "";
        public virtual void StartTutorial() {
            if (!_settings.EnableTutorial || _tutorialActive) return;

            _tutorialActive = true;
            HasUsedTutorial = true;
            ProcessNextTutorialStep();
        }

        public virtual void CompleteTutorialStep() {
            if (!_tutorialActive || _tutorialSteps.Count == 0) return;

            var step = _tutorialSteps.Dequeue();
            OnTutorialStepCompleted?.Invoke(this, step);

            if (_tutorialSteps.Count > 0) ProcessNextTutorialStep();
            else _tutorialActive = false;
        }

        protected virtual void ProcessNextTutorialStep() {
            if (_tutorialSteps.Count > 0) {
                var nextStep = _tutorialSteps.Peek();
                HandleTutorialStep(nextStep);
            }
        }

        protected virtual void HandleTutorialStep(string step) { }

        public virtual void UpdatePosition(Vector3 newPosition) {
            transform.position = newPosition;
            _moduleData["currentPosition"] = newPosition;
            OnPositionChanged?.Invoke(this, newPosition);
        }

        public virtual void TriggerCustomEvent(string eventName, object data = null) {
            _moduleData[$"event_{eventName}"] = data ?? DateTime.UtcNow;
            OnCustomEvent?.Invoke(this, eventName, data);
        }

        public virtual void SetModuleData(string key, object value) {
            _moduleData[key] = value;
        }

        public virtual T GetModuleData<T>(string key, T defaultValue = default) {
            return _moduleData.TryGetValue(key, out var value) && value is T result ? result : defaultValue;
        }
        #endregion

        #region Advanced Scoring & Media System
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void CalculateScore(bool isExpired = false) {
            if (isExpired) {
                CalculatedScore += _settings.PenaltyOnExpire;
                return;
            }

            var baseScore = _settings.PointsOnComplete;
            var timeBonus = 0f;
            var attemptPenalty = 0;
            var hintPenalty = HintsUsed * _settings.HintCost;
            var tutorialPenalty = HasUsedTutorial ? 20 : 0;

            if (!_settings.UseInfiniteTime)
                timeBonus = (RemainingTime / _settings.DurationSeconds) * 100f;

            if (CurrentAttempts > 1)
                attemptPenalty = Mathf.Min((CurrentAttempts - 1) * 5, baseScore / 4);

            var efficiencyBonus = 0f;
            if (ElapsedTime < _settings.DurationSeconds * 0.5f)
                efficiencyBonus = baseScore * 0.25f;

            var finalScore = baseScore + timeBonus + efficiencyBonus - attemptPenalty - hintPenalty - tutorialPenalty;
            CalculatedScore = Mathf.RoundToInt(finalScore * _settings.ScoreMultiplier);
            CalculatedScore = Mathf.Max(0, CalculatedScore);

            OnScoreChanged?.Invoke(this, CalculatedScore);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual async void PlayCompletionAudio() {
            if (string.IsNullOrEmpty(_settings.AudioClipName) || _audioSource == null) return;

            var mediaManager = FrameworkCore.Instance?.GetService<Media.MediaManager>();
            if (mediaManager != null) {
                try {
                    var audioAsset = await mediaManager.LoadAsync<Media.AudioAsset>(_settings.AudioClipName);
                    if (audioAsset?.TypedAsset != null) {
                        _audioSource.clip = audioAsset.TypedAsset;
                        _audioSource.Play();
                    }
                } catch {
                    Debug.LogWarning($"[GameModule] Could not play audio: {_settings.AudioClipName}");
                }
            }
        }

        protected virtual async void PlayAudio(string audioName) {
            if (string.IsNullOrEmpty(audioName) || _audioSource == null) return;

            var mediaManager = FrameworkCore.Instance?.GetService<Media.MediaManager>();
            if (mediaManager != null) {
                try {
                    var audioAsset = await mediaManager.LoadAsync<Media.AudioAsset>(audioName);
                    if (audioAsset?.TypedAsset != null) {
                        _audioSource.clip = audioAsset.TypedAsset;
                        _audioSource.Play();
                    }
                } catch {
                    Debug.LogWarning($"[GameModule] Could not play audio: {audioName}");
                }
            }
        }

        protected virtual void PlayVideo() {
            if (_videoPlayer != null && _videoPlayer.clip != null) {
                _videoPlayer.Play();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ModuleResult CreateResult(bool wasSkipped = false, bool wasForced = false) {
            var efficiency = _settings.DurationSeconds > 0 ? 
                Mathf.Clamp01(1f - (ElapsedTime / _settings.DurationSeconds)) : 1f;

            var customData = new Dictionary<string, object>(_moduleData) {
                ["wasForced"] = wasForced,
                ["efficiency"] = efficiency,
                ["endPosition"] = transform.position,
                ["endRotation"] = transform.rotation,
                ["endScale"] = transform.localScale
            };

            return new ModuleResult(
                ModuleID, Status, ElapsedTime, RemainingTime,
                CalculatedScore, CurrentAttempts, _startTime, DateTime.UtcNow,
                GameType, Difficulty, efficiency, HintsUsed,
                0, 0,
                transform.position, transform.rotation.eulerAngles,
                customData, HasUsedTutorial, IsPaused, wasSkipped
            );
        }
        #endregion

        #region Universal Utilities & Extensions
        public void ConfigureModule(ModuleSettings newSettings) {
            if (Status == ModuleStatus.Active) return;
            
            _settings = newSettings;
            ModuleID = _settings.ModuleName.Length > 0 ? _settings.ModuleName : ModuleID;
            
            if (!string.IsNullOrEmpty(_settings.TargetImageName))
                LoadTargetImageAsync();
            if (!string.IsNullOrEmpty(_settings.VideoClipName))
                LoadVideoAsync();
            if (!string.IsNullOrEmpty(_settings.PrefabName))
                LoadPrefabAsync();

            InitializeHints();
            InitializeTutorial();
        }

        public void ConfigureForGameType(string gameType, string difficulty = "Normal") {
            GameType = gameType;
            Difficulty = difficulty;
            
            var settings = gameType.ToLower() switch {
                "puzzle" => ModuleSettings.ForPuzzleGame(ModuleID),
                "action" => ModuleSettings.ForActionGame(ModuleID),
                "educational" => ModuleSettings.ForEducationalGame(ModuleID),
                "endless" => ModuleSettings.ForEndlessGame(ModuleID),
                "timed" => ModuleSettings.ForTimedChallenge(ModuleID),
                _ => _settings
            };
            
            ConfigureModule(settings);
        }

        public float GetProgress() => _settings.UseInfiniteTime ? 0f : 
            (_settings.DurationSeconds > 0f ? ElapsedTime / _settings.DurationSeconds : 0f);

        public float GetEfficiency() => _settings.DurationSeconds > 0f ? 
            Mathf.Clamp01(1f - (ElapsedTime / _settings.DurationSeconds)) : 1f;

        public string GetStatusDisplay() => Status switch {
            ModuleStatus.Inactive => "En attente",
            ModuleStatus.Active when IsPaused => "En pause",
            ModuleStatus.Active when _settings.UseInfiniteTime => "Actif",
            ModuleStatus.Active => $"{RemainingTime:F0}s restantes",
            ModuleStatus.Completed => "Terminé",
            ModuleStatus.Expired => "Expiré",
            ModuleStatus.Failed => "Échoué",
            _ => "Inconnu"
        };

        public string GetScoreDisplay() => $"{CalculatedScore} pts";

        public string GetDetailedStatus() => 
            $"{GetStatusDisplay()} | {GetScoreDisplay()} | Tentatives: {CurrentAttempts}" +
            (HintsUsed > 0 ? $" | Indices: {HintsUsed}" : "") +
            (HasUsedTutorial ? " | Tutoriel utilisé" : "");

        public virtual void AdjustDifficulty(string newDifficulty) {
            Difficulty = newDifficulty;
            
            var multiplier = newDifficulty.ToLower() switch {
                "easy" => 0.7f,
                "normal" => 1f,
                "hard" => 1.3f,
                "expert" => 1.6f,
                _ => 1f
            };

            var adjustedSettings = new ModuleSettings(
                _settings.ModuleName, _settings.AudioClipName, _settings.TargetImageName,
                _settings.VideoClipName, _settings.PrefabName,
                _settings.DurationSeconds / multiplier,
                _settings.ScoreMultiplier * multiplier,
                _settings.DelayBeforeStart, _settings.FadeInDuration, _settings.FadeOutDuration,
                _settings.AutoComplete, _settings.RequireValidation, _settings.ShowTimer,
                _settings.CanPause, _settings.CanRestart, _settings.CanSkip,
                _settings.UseInfiniteTime, _settings.AllowMultipleAttempts,
                newDifficulty == "easy",
                newDifficulty == "easy",
                newDifficulty == "easy" ? -1 : Mathf.RoundToInt(_settings.MaxAttempts / multiplier),
                _settings.PointsOnComplete, _settings.PenaltyOnExpire,
                _settings.BonusPointsPerSecond, _settings.HintCost,
                _settings.SpawnPosition, _settings.SpawnRotation, _settings.SpawnScale,
                _settings.CustomTags, _settings.RequiredComponents, _settings.OptionalAssets,
                _settings.CustomFloatParams, _settings.CustomIntParams, _settings.CustomBoolParams
            );

            ConfigureModule(adjustedSettings);
        }
        public void QuickStart() => StartModule();
        public void QuickComplete() => CompleteModule();
        public void QuickReset() => ResetModule();
        public void QuickPause() => PauseModule();
        public void QuickResume() => ResumeModule();

        public GameModule WithGameType(string gameType) { ConfigureForGameType(gameType); return this; }
        public GameModule WithDifficulty(string difficulty) { AdjustDifficulty(difficulty); return this; }
        public GameModule WithData(string key, object value) { SetModuleData(key, value); return this; }
        #endregion

        #region Operator Overloads
        public static implicit operator bool(GameModule module) => module?.Status == ModuleStatus.Active;
        public static implicit operator ModuleStatus(GameModule module) => module?.Status ?? ModuleStatus.Inactive;
        public static implicit operator float(GameModule module) => module?.RemainingTime ?? 0f;
        #endregion
    }
}
