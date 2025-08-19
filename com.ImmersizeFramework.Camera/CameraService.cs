using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Camera {
    public sealed class CameraService : IFrameworkService {
        #region Configuration & State
        public readonly struct CameraConfig {
            public readonly CameraType DefaultType { get; }
            public readonly CameraPreset DefaultPreset { get; }
            public readonly bool AutoFindTarget { get; }
            public readonly float ShakeDuration { get; }
            public readonly float ShakeMagnitude { get; }
            public readonly string PlayerTag { get; }

            public CameraConfig(CameraType defaultType = CameraType.TopDown, CameraPreset defaultPreset = CameraPreset.PlayBook, 
                bool autoFindTarget = true, float shakeDuration = 0.5f, float shakeMagnitude = 0.1f, string playerTag = "Player") =>
                (DefaultType, DefaultPreset, AutoFindTarget, ShakeDuration, ShakeMagnitude, PlayerTag) = 
                (defaultType, defaultPreset, autoFindTarget, shakeDuration, shakeMagnitude, playerTag);

            public static implicit operator CameraConfig(CameraType type) => new(type);
            public static implicit operator CameraConfig(CameraPreset preset) => new(defaultPreset: preset);
        }

        public readonly struct CameraState {
            public readonly CameraController Controller { get; }
            public readonly CameraType Type { get; }
            public readonly Transform Target { get; }
            public readonly bool IsValid { get; }

            public CameraState(CameraController controller, CameraType type, Transform target) =>
                (Controller, Type, Target, IsValid) = (controller, type, target, controller != null);

            public static implicit operator bool(CameraState state) => state.IsValid;

            public override string ToString() =>
                IsValid ? $"Type: {Type}, Target: {Target?.name ?? "NULL"}, Position: {Controller[Type]?.transform.position}" 
                        : "Invalid Camera State";
        }
        #endregion

        #region Properties & Indexers
        public bool IsInitialized { get; private set; }
        public int Priority => 3;

        private readonly CameraConfig _config;
        private readonly ConcurrentDictionary<string, CameraController> _controllers = new();
        private CameraController _activeController;
        private Transform _defaultTarget;

        public CameraController this[string name] {
            get => _controllers.TryGetValue(name, out var controller) ? controller : null;
            set {
                if (value != null) _controllers[name] = value;
                else _controllers.TryRemove(name, out _);
            }
        }

        public CameraState this[CameraType type] => new(_activeController, type, _defaultTarget);

        public CameraController ActiveController => _activeController;
        public CameraType CurrentType {
            get => _activeController?.CurrentCameraType ?? _config.DefaultType;
            set => SetCameraType(value);
        }

        public Transform DefaultTarget {
            get => _defaultTarget;
            set => SetDefaultTarget(value);
        }
        #endregion

        #region Events
        public event Action<CameraType> OnCameraTypeChanged;
        public event Action<CameraController> OnControllerChanged;
        public event Action<Transform> OnTargetChanged;
        #endregion

        #region Constructor
        public CameraService() : this(new CameraConfig()) { }
        public CameraService(CameraConfig config) => _config = config;
        #endregion

        #region Advanced Service Implementation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task InitializeAsync() {
            if (IsInitialized) return;

            await Task.Run(() => {
                InitializeControllers();
                InitializeDefaultTarget();
            });
            
            IsInitialized = true;
            FrameworkCore.Instance.LogMessage("[CameraService] Initialized");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Initialize() {
            if (IsInitialized) return;

            InitializeControllers();
            InitializeDefaultTarget();
            
            IsInitialized = true;
            FrameworkCore.Instance.LogMessage("[CameraService] Initialized");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Shutdown() {
            if (!IsInitialized) return;

            _controllers.Clear();
            _activeController = null;
            _defaultTarget = null;
            OnCameraTypeChanged = null;
            OnControllerChanged = null;
            OnTargetChanged = null;

            IsInitialized = false;
            FrameworkCore.Instance.LogMessage("[CameraService] Shutdown");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() {
            Shutdown();
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeControllers() {
            var existingController = UnityEngine.Object.FindObjectOfType<CameraController>();
            if (existingController != null) SetActiveController(existingController);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeDefaultTarget() {
            if (!_config.AutoFindTarget) return;
            
            var playerObject = GameObject.FindGameObjectWithTag(_config.PlayerTag);
            if (playerObject != null) SetDefaultTarget(playerObject.transform);
        }
        #endregion

        #region Advanced Camera Management
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetActiveController(CameraController controller) {
            if (controller == null || controller == _activeController) return;

            _activeController = controller;
            _controllers[controller.name] = controller;

            if (_activeController.target == null && _defaultTarget != null)
                _activeController.target = _defaultTarget;

            OnControllerChanged?.Invoke(_activeController);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CameraController CreateController(string name = "CameraController", CameraPreset preset = default) {
            var actualPreset = preset == default ? _config.DefaultPreset : preset;
            var cameraObject = new GameObject($"[{name}]");
            var controller = cameraObject.AddComponent<CameraController>();

            CameraControllerBuilder.Create(controller)
                .WithPreset(actualPreset)
                .WithTarget(_defaultTarget)
                .Build();

            SetActiveController(controller);
            return controller;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCameraType(CameraType type) {
            if (_activeController?.CurrentCameraType == type) return;
            
            if (_activeController != null) {
                _activeController.CurrentCameraType = type;
                OnCameraTypeChanged?.Invoke(type);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTarget(Transform target) {
            if (_activeController == null) return;

            _activeController.target = target;
            _defaultTarget = target;
            OnTargetChanged?.Invoke(target);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetDefaultTarget(Transform target) {
            _defaultTarget = target;
            if (_activeController?.target == null) _activeController.target = target;
            OnTargetChanged?.Invoke(target);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async void ShakeCamera(float duration = 0f, float magnitude = 0f) {
            if (_activeController == null) return;

            var actualDuration = duration > 0 ? duration : _config.ShakeDuration;
            var actualMagnitude = magnitude > 0 ? magnitude : _config.ShakeMagnitude;
            var activeCamera = _activeController[_activeController.CurrentCameraType];
            
            if (activeCamera != null) await CameraController.ShakeCameraAsync(activeCamera, actualDuration, actualMagnitude);
        }
        #endregion

        #region Advanced Builder Integration
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CameraControllerBuilder ConfigureActive() => 
            _activeController != null ? CameraControllerBuilder.Create(_activeController) : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyPreset(CameraPreset preset) {
            if (_activeController == null) return;

            CameraControllerBuilder.Create(_activeController)
                .WithPreset(preset)
                .Build();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CameraControllerBuilder ConfigureController(string name) => 
            _controllers.TryGetValue(name, out var controller) ? CameraControllerBuilder.Create(controller) : null;
        #endregion

        #region Advanced Validation & Diagnostics
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ValidateSetup() {
            if (_activeController?.target == null) return false;
            
            var camera = _activeController[_activeController.CurrentCameraType];
            return camera != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string GetDiagnostics() => _activeController == null 
            ? "[CameraService] No active controller" 
            : this[_activeController.CurrentCameraType].ToString();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (int total, int active) GetControllerStats() => (_controllers.Count, _activeController != null ? 1 : 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string[] GetControllerNames() => _controllers.Keys.ToArray();
        #endregion

        #region Operator Overloads
        public static implicit operator bool(CameraService service) => service?.IsInitialized == true && service._activeController != null;
        public static implicit operator CameraController(CameraService service) => service?._activeController;
        public static implicit operator Transform(CameraService service) => service?._defaultTarget;
        #endregion
    }
}
