using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Input;

namespace com.ImmersizeFramework.Game {
    public enum GameState : byte { Menu, Loading, Playing, Paused, Completed, Failed, Restarting, Transitioning }

    public abstract class GameManager<TSettings> : MonoBehaviour where TSettings : struct {
        #region Ultra-Flexible Game Configuration
        [System.Serializable]
        public readonly struct GameManagerSettings {
            public readonly bool EnablePause, EnableScoring, EnableAnalytics, EnableAutoSave;
            public readonly bool ShowDebugInfo, EnablePerformanceTracking, AllowRestart, EnableMultiplayer;
            public readonly bool UseCustomInput, EnableVR, EnableAR, EnableAccessibility, EnableStreaming;
            public readonly bool AllowDynamicDifficulty, EnablePlayerAdaptation, SupportMods, EnableCloud;
            public readonly float AutoSaveInterval, AnalyticsInterval, StateTransitionDelay;
            public readonly int MaxRetries, TargetFPS, MaxPlayers, MinPlayers;
            public readonly string[] SupportedPlatforms, RequiredFeatures, OptionalFeatures;
            public readonly Vector2 Resolution;

            public GameManagerSettings(bool pause = true, bool scoring = true, bool analytics = true, bool autoSave = true,
                                     bool debug = false, bool performance = true, bool restart = true, bool multiplayer = false,
                                     bool customInput = false, bool vr = false, bool ar = false, bool accessibility = false,
                                     bool streaming = false, bool dynamicDifficulty = false, bool adaptation = false,
                                     bool mods = false, bool cloud = false, float saveInterval = 30f, float analyticsInterval = 10f,
                                     float transitionDelay = 1f, int retries = 3, int fps = 60, int maxPlayers = 1,
                                     int minPlayers = 1, string[] platforms = null, string[] required = null,
                                     string[] optional = null, Vector2 resolution = default) =>
                (EnablePause, EnableScoring, EnableAnalytics, EnableAutoSave, ShowDebugInfo, EnablePerformanceTracking,
                 AllowRestart, EnableMultiplayer, UseCustomInput, EnableVR, EnableAR, EnableAccessibility, EnableStreaming,
                 AllowDynamicDifficulty, EnablePlayerAdaptation, SupportMods, EnableCloud, AutoSaveInterval, AnalyticsInterval,
                 StateTransitionDelay, MaxRetries, TargetFPS, MaxPlayers, MinPlayers, SupportedPlatforms, RequiredFeatures,
                 OptionalFeatures, Resolution) =
                (pause, scoring, analytics, autoSave, debug, performance, restart, multiplayer, customInput, vr, ar,
                 accessibility, streaming, dynamicDifficulty, adaptation, mods, cloud, saveInterval, analyticsInterval,
                 transitionDelay, retries, fps, maxPlayers, minPlayers, platforms ?? new[] { "Standalone", "Mobile" },
                 required ?? new string[0], optional ?? new string[0],
                 resolution == default ? new Vector2(1920, 1080) : resolution);

            public static GameManagerSettings ForVRGame() => new(vr: true, customInput: true, fps: 90, accessibility: true);
            public static GameManagerSettings ForARGame() => new(ar: true, customInput: true, streaming: true);
            public static GameManagerSettings ForMobileGame() => new(fps: 30, platforms: new[] { "Mobile" });
            public static GameManagerSettings ForMultiplayerGame(int maxPlayers = 4) => new(multiplayer: true, maxPlayers: maxPlayers, cloud: true);
            public static GameManagerSettings ForEducationalGame() => new(accessibility: true, adaptation: true, analytics: true);
            public static GameManagerSettings ForCompetitiveGame() => new(scoring: true, analytics: true, streaming: true, cloud: true);

            public static implicit operator GameManagerSettings(bool enableAll) => new(enableAll, enableAll, enableAll, enableAll, enableAll);
        }

        public readonly struct GameSession {
            public readonly string SessionID, GameType, Platform, BuildVersion;
            public readonly DateTime StartTime, EndTime;
            public readonly float Duration, AveragePerformance;
            public readonly GameState FinalState;
            public readonly int Score, Retries, PlayersCount;
            public readonly Vector2 Resolution;
            public readonly Dictionary<string, object> SessionData, PerformanceMetrics, PlayerMetrics;
            public readonly bool WasMultiplayer, UsedVR, UsedAR, UsedAccessibility, WasStreamed;

            public GameSession(string id, DateTime start, DateTime end, float duration, GameState state, int score, int retries,
                             string gameType = "", string platform = "", string version = "", int players = 1,
                             Vector2 resolution = default, float avgPerformance = 0f, bool multiplayer = false,
                             bool vr = false, bool ar = false, bool accessibility = false, bool streamed = false,
                             Dictionary<string, object> sessionData = null, Dictionary<string, object> perfMetrics = null,
                             Dictionary<string, object> playerMetrics = null) =>
                (SessionID, StartTime, EndTime, Duration, FinalState, Score, Retries, GameType, Platform, BuildVersion,
                 PlayersCount, Resolution, AveragePerformance, WasMultiplayer, UsedVR, UsedAR, UsedAccessibility,
                 WasStreamed, SessionData, PerformanceMetrics, PlayerMetrics) =
                (id, start, end, duration, state, score, retries, gameType, platform, version, players,
                 resolution == default ? new Vector2(Screen.width, Screen.height) : resolution, avgPerformance,
                 multiplayer, vr, ar, accessibility, streamed, sessionData ?? new Dictionary<string, object>(),
                 perfMetrics ?? new Dictionary<string, object>(), playerMetrics ?? new Dictionary<string, object>());

            public T GetSessionData<T>(string key, T defaultValue = default) =>
                SessionData.TryGetValue(key, out var value) && value is T result ? result : defaultValue;

            public override string ToString() =>
                $"Session {SessionID} [{GameType}]: {FinalState} in {Duration:F1}s (Score: {Score}, Players: {PlayersCount})";
        }
        #endregion

        #region Enhanced Properties & Events
        public GameState CurrentState { get; protected set; } = GameState.Menu;
        public GameState PreviousState { get; protected set; } = GameState.Menu;
        public bool IsGameActive => CurrentState == GameState.Playing;
        public bool IsGamePaused => CurrentState == GameState.Paused;
        public bool IsTransitioning => CurrentState == GameState.Transitioning;
        public float SessionDuration { get; protected set; }
        public int CurrentScore { get; protected set; }
        public int RetryCount { get; protected set; }
        public int ConnectedPlayers { get; protected set; } = 1;
        public string GameType { get; protected set; } = "Generic";
        public string Platform { get; protected set; } = Application.platform.ToString();
        public string BuildVersion { get; protected set; } = Application.version;
        public event Action<GameState, GameState> OnStateChanged;
        public event Action<int> OnScoreUpdated;
        public event Action<GameSession> OnSessionCompleted, OnSessionStarted;
        public event Action OnGamePaused, OnGameResumed, OnGameRestarted;
        public event Action<string> OnGameTypeChanged, OnPlatformChanged;
        public event Action<int> OnPlayersChanged;
        public event Action<float> OnPerformanceChanged;
        public event Action<Dictionary<string, object>> OnAnalyticsCollected;

        [SerializeField] protected GameManagerSettings _gameSettings = new();
        [SerializeField] protected TSettings _customSettings;

        protected ModuleManager _moduleManager;
        protected InputBindingSystem _inputBinding;
        protected string _currentSessionID;
        protected DateTime _sessionStartTime;
        protected float _autoSaveTimer, _analyticsTimer, _performanceTimer, _averagePerformance;
        protected bool _isInitialized, _isMultiplayerSession, _usesVR, _usesAR, _usesAccessibility;
        protected Dictionary<string, object> _sessionData = new(), _performanceMetrics = new(), _playerMetrics = new();
        protected Queue<GameState> _stateTransitionQueue = new();
        #endregion

        #region Initialization
        protected virtual async void Start() => await InitializeAsync();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual async Task InitializeAsync() {
            if (_isInitialized) return;

            SetupFrameworkServices();
            await SetupGameSystems();
            ConfigureGameSettings();
            
            _isInitialized = true;
            OnGameInitialized();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void SetupFrameworkServices() {
            if (FrameworkCore.Instance is null) return;
            
            (_moduleManager, _inputBinding) = (
                FrameworkCore.Instance.GetService<ModuleManager>(),
                FrameworkCore.Instance.GetService<InputBindingSystem>()
            );
            
            if (_moduleManager is not null) {
                _moduleManager.OnExperienceStarted += HandleExperienceStarted;
                _moduleManager.OnExperienceCompleted += HandleExperienceCompleted;
                _moduleManager.OnModuleCompleted += HandleModuleCompleted;
            }

            SetupInputBindings();
        }

        protected virtual async Task SetupGameSystems() {
            if (_moduleManager?.IsInitialized is false)
                await _moduleManager.InitializeAsync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void ConfigureGameSettings() {
            if (_gameSettings.TargetFPS > 0)
                Application.targetFrameRate = _gameSettings.TargetFPS;
        }

        protected virtual void OnGameInitialized() { }
        #endregion

        #region Game State Management
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void StartGame() {
            if (!_isInitialized || IsGameActive) return;

            ChangeState(GameState.Loading);
            BeginSession();
            
            _moduleManager?.StartExperience();
            ChangeState(GameState.Playing);
            OnGameStarted();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void PauseGame() {
            if (!_gameSettings.EnablePause || !IsGameActive) return;

            ChangeState(GameState.Paused);
            Time.timeScale = 0f;
            OnGamePaused?.Invoke();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void ResumeGame() {
            if (CurrentState != GameState.Paused) return;

            ChangeState(GameState.Playing);
            Time.timeScale = 1f;
            OnGameResumed?.Invoke();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void CompleteGame(bool success = true) {
            if (!IsGameActive && CurrentState != GameState.Paused) return;

            var finalState = success ? GameState.Completed : GameState.Failed;
            ChangeState(finalState);
            EndSession(finalState);
            OnGameCompleted(success);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void RestartGame() {
            if (!_gameSettings.AllowRestart) return;

            if (IsGameActive || IsGamePaused)
                CompleteGame(false);

            if (++RetryCount <= _gameSettings.MaxRetries) {
                ResetGameState();
                StartGame();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void ChangeState(GameState newState) {
            if (CurrentState == newState) return;

            (PreviousState, CurrentState) = (CurrentState, newState);
            OnStateChanged?.Invoke(PreviousState, CurrentState);
            
            if (_gameSettings.ShowDebugInfo)
                Debug.Log($"[GameManager] State: {PreviousState} -> {CurrentState}");
        }

        protected virtual void OnGameStarted() { }
        protected virtual void OnGameCompleted(bool success) { }
        #endregion

        #region Session Management
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void BeginSession() =>
            (_currentSessionID, _sessionStartTime, SessionDuration, CurrentScore) = 
            ($"Session_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{UnityEngine.Random.Range(1000, 9999)}", 
             DateTime.UtcNow, 0f, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void EndSession(GameState finalState) {
            var session = new GameSession(_currentSessionID, _sessionStartTime, DateTime.UtcNow, 
                                        SessionDuration, finalState, CurrentScore, RetryCount);
            
            SaveSessionData(session);
            OnSessionCompleted?.Invoke(session);
            
            if (_gameSettings.ShowDebugInfo)
                Debug.Log($"[GameManager] Session completed: {session}");
        }

        protected virtual void SaveSessionData(GameSession session) {
            if (!_gameSettings.EnableAutoSave) return;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void ResetGameState() =>
            (SessionDuration, CurrentScore, _autoSaveTimer, _analyticsTimer) = (0f, 0, 0f, 0f);
        #endregion

        #region Scoring & Analytics
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void AddScore(int points) {
            if (!_gameSettings.EnableScoring) return;
            OnScoreUpdated?.Invoke(CurrentScore += points);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual void SetScore(int newScore) {
            if (!_gameSettings.EnableScoring) return;
            OnScoreUpdated?.Invoke(CurrentScore = newScore);
        }

        protected virtual void CollectAnalytics() {
            if (!_gameSettings.EnableAnalytics) return;

            ProcessAnalytics(new {
                SessionID = _currentSessionID,
                State = CurrentState.ToString(),
                Duration = SessionDuration,
                Score = CurrentScore,
                Retries = RetryCount,
                Timestamp = DateTime.UtcNow
            });
        }

        protected virtual void ProcessAnalytics(object metrics) { }
        #endregion

        #region Module Event Handlers
        protected virtual void HandleExperienceStarted() {
            if (_gameSettings.ShowDebugInfo)
                Debug.Log("[GameManager] Experience started");
        }

        protected virtual void HandleExperienceCompleted(ModuleManager.ExperienceMetrics metrics) {
            if (_gameSettings.EnableScoring)
                SetScore(metrics.TotalScore);
            
            CompleteGame(true);
            
            if (_gameSettings.ShowDebugInfo)
                Debug.Log($"[GameManager] Experience completed: {metrics}");
        }

        protected virtual void HandleModuleCompleted(GameModule module, GameModule.ModuleResult result) {
            if (_gameSettings.EnableScoring)
                AddScore(result.Score);
            
            if (_gameSettings.ShowDebugInfo)
                Debug.Log($"[GameManager] Module completed: {result}");
        }
        #endregion

        #region Update & Lifecycle
        protected virtual void Update() {
            if (!_isInitialized) return;

            UpdateTimers();
            HandleInput();
            
            if (_gameSettings.EnableAnalytics)
                UpdateAnalytics();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void UpdateTimers() {
            if (!IsGameActive) return;
            SessionDuration += Time.deltaTime;
            _autoSaveTimer += Time.deltaTime;
        }

        protected virtual void HandleInput() { }

        [InputBindingSystem.InputAction("game_pause", "Pause/Resume Game", InputBindingSystem.InputActionType.ButtonDown, KeyCode.Escape)]
        public virtual void HandleInputPause() => 
            (IsGameActive ? (System.Action)PauseGame : IsGamePaused ? ResumeGame : null)?.Invoke();

        [InputBindingSystem.InputAction("game_restart", "Restart Game", InputBindingSystem.InputActionType.ButtonDown, KeyCode.R)]
        public virtual void HandleInputRestart() {
            if (_gameSettings.AllowRestart)
                RestartGame();
        }

        [InputBindingSystem.InputAction("game_menu", "Toggle Menu", InputBindingSystem.InputActionType.ButtonDown, KeyCode.M)]
        public virtual void HandleInputMenu() => OnMenuToggleRequested();

        [InputBindingSystem.InputAction("game_screenshot", "Take Screenshot", InputBindingSystem.InputActionType.ButtonDown, KeyCode.F12)]
        public virtual void HandleInputScreenshot() => TakeScreenshot();

        protected virtual void SetupInputBindings() {
            if (_inputBinding is null) return;
            CreateGameSpecificInputActions();
        }

        protected virtual void CreateGameSpecificInputActions() {
            if (_inputBinding is null) return;

            var gameTypePrefix = GetGameTypePrefix();
            
            var actions = new (string name, string display, KeyCode key)[] {
                ($"{gameTypePrefix}_stats", "Show Statistics", KeyCode.Tab),
                ($"{gameTypePrefix}_help", "Show Help", KeyCode.F1),
                (_gameSettings.EnableMultiplayer ? $"{gameTypePrefix}_chat" : null, "Open Chat", KeyCode.T),
                (_gameSettings.EnableVR ? $"{gameTypePrefix}_vr_menu" : null, "VR Menu", KeyCode.None)
            };

            foreach (var (name, display, key) in actions.Where(a => a.name is not null))
                _inputBinding.CreateAction(name, display, InputBindingSystem.InputActionType.ButtonDown, key);
        }

        protected virtual string GetGameTypePrefix() => GameType.ToLower().Replace(" ", "_");

        protected virtual void OnMenuToggleRequested() {
            if (_gameSettings.ShowDebugInfo)
                Debug.Log("[GameManager] Menu toggle requested");
        }

        protected virtual void TakeScreenshot() {
            if (_gameSettings.ShowDebugInfo)
                Debug.Log("[GameManager] Screenshot taken");
            
            ScreenCapture.CaptureScreenshot($"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void UpdateAnalytics() {
            if ((_analyticsTimer += Time.deltaTime) >= _gameSettings.AnalyticsInterval) {
                _analyticsTimer = 0f;
                CollectAnalytics();
            }
        }

        protected virtual void OnDestroy() {
            if (_moduleManager is not null) {
                _moduleManager.OnExperienceStarted -= HandleExperienceStarted;
                _moduleManager.OnExperienceCompleted -= HandleExperienceCompleted;
                _moduleManager.OnModuleCompleted -= HandleModuleCompleted;
            }

            _inputBinding?.Dispose();
            
            if (IsGameActive || IsGamePaused)
                CompleteGame(false);
        }

        protected virtual void CleanupInputBindings() {
            if (_gameSettings.ShowDebugInfo)
                Debug.Log("[GameManager] Cleaning up input bindings");
        }
        #endregion

        #region Public Utilities & Indexers
        public virtual void ConfigureSettings(GameManagerSettings newSettings, TSettings customSettings) =>
            (_gameSettings, _customSettings) = (newSettings, customSettings);

        public virtual GameSession GetCurrentSession() => new(
            _currentSessionID, _sessionStartTime, DateTime.UtcNow,
            SessionDuration, CurrentState, CurrentScore, RetryCount
        );

        public virtual void ForceCompleteCurrentModule() => _moduleManager?.CurrentModule?.CompleteModule(true);
        public virtual void SkipCurrentModule() => _moduleManager?.SkipCurrentModule();

        public TValue GetSessionData<TValue>(string key, TValue defaultValue = default) =>
            _sessionData.TryGetValue(key, out var value) && value is TValue result ? result : defaultValue;

        public void SetSessionData<TValue>(string key, TValue value) => _sessionData[key] = value;

        public TValue GetPerformanceMetric<TValue>(string key, TValue defaultValue = default) =>
            _performanceMetrics.TryGetValue(key, out var value) && value is TValue result ? result : defaultValue;

        public void SetPerformanceMetric<TValue>(string key, TValue value) => _performanceMetrics[key] = value;

        public TValue GetPlayerMetric<TValue>(string key, TValue defaultValue = default) =>
            _playerMetrics.TryGetValue(key, out var value) && value is TValue result ? result : defaultValue;

        public void SetPlayerMetric<TValue>(string key, TValue value) => _playerMetrics[key] = value;

        public object this[string dataKey] {
            get => _sessionData.TryGetValue(dataKey, out var value) ? value : null;
            set => _sessionData[dataKey] = value;
        }

        public GameState this[int stateIndex] {
            get => stateIndex switch {
                0 => CurrentState,
                1 => PreviousState,
                _ => throw new ArgumentOutOfRangeException(nameof(stateIndex))
            };
        }

        public float this[GameMetricType metricType] {
            get => metricType switch {
                GameMetricType.SessionDuration => SessionDuration,
                GameMetricType.AveragePerformance => _averagePerformance,
                GameMetricType.AutoSaveTimer => _autoSaveTimer,
                GameMetricType.AnalyticsTimer => _analyticsTimer,
                _ => 0f
            };
        }
        #endregion

        #region Advanced Operators & Conversions
        public static implicit operator bool(GameManager<TSettings> manager) => manager?._isInitialized == true;
        public static implicit operator GameState(GameManager<TSettings> manager) => manager?.CurrentState ?? GameState.Menu;
        public static implicit operator int(GameManager<TSettings> manager) => manager?.CurrentScore ?? 0;
        public static implicit operator float(GameManager<TSettings> manager) => manager?.SessionDuration ?? 0f;

        public static bool operator !(GameManager<TSettings> manager) => !manager._isInitialized;
        public static GameManager<TSettings> operator ++(GameManager<TSettings> manager) { manager.RetryCount++; return manager; }
        public static GameManager<TSettings> operator --(GameManager<TSettings> manager) { manager.RetryCount = Math.Max(0, manager.RetryCount - 1); return manager; }

        public void Deconstruct(out GameState state, out int score, out float duration) =>
            (state, score, duration) = (CurrentState, CurrentScore, SessionDuration);

        public void Deconstruct(out GameState current, out GameState previous, out bool isActive, out bool isPaused) =>
            (current, previous, isActive, isPaused) = (CurrentState, PreviousState, IsGameActive, IsGamePaused);
        #endregion

        #region Enums & Extensions
        public enum GameMetricType : byte { SessionDuration, AveragePerformance, AutoSaveTimer, AnalyticsTimer }
        #endregion
    }
}
