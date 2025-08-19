using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Linq;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.Input {
    public sealed class InputManager : IFrameworkService, IFrameworkTickable {
        #region Configuration & Nested Types
        public enum InputDeviceType : byte { Mouse, Touch, Gamepad, Keyboard, AR }
        public enum InputType : byte { PointerDown, PointerUp, PointerMove, KeyDown, KeyUp, GamepadButton, GamepadAxis, Gesture, Scroll }
        public enum GestureType : byte { Tap, DoubleTap, LongPress, Swipe, Pinch, Rotate, Pan, Custom }

        public readonly struct InputSettings {
            public readonly bool EnableTouch, EnableMouse, EnableKeyboard, EnableGamepad, EnableGestures, EnablePrediction;
            public readonly float TouchSensitivity, MouseSensitivity, BufferTime;
            public readonly int MaxTouchPoints;

            public InputSettings(bool touch = true, bool mouse = true, bool keyboard = true, bool gamepad = true, 
                               bool gestures = true, bool prediction = true, float touchSens = 1f, float mouseSens = 1f, 
                               float bufferTime = 0.1f, int maxTouch = 10) =>
                (EnableTouch, EnableMouse, EnableKeyboard, EnableGamepad, EnableGestures, EnablePrediction, 
                 TouchSensitivity, MouseSensitivity, BufferTime, MaxTouchPoints) = 
                (touch, mouse, keyboard, gamepad, gestures, prediction, touchSens, mouseSens, bufferTime, maxTouch);

            public static implicit operator InputSettings(bool enableAll) => new(enableAll, enableAll, enableAll, enableAll, enableAll, enableAll);
        }

        public readonly struct InputData {
            public readonly InputType Type;
            public readonly InputDeviceType DeviceType;
            public readonly Vector2 Position, Delta;
            public readonly float Pressure, Value;
            public readonly int TouchId;
            public readonly KeyCode KeyCode;
            public readonly string InputName;
            public readonly DateTime Timestamp;
            public readonly bool IsConsumed;

            public InputData(InputType type, InputDeviceType deviceType, Vector2 position, Vector2 delta = default,
                           float pressure = 0f, float value = 0f, int touchId = -1, KeyCode keyCode = KeyCode.None,
                           string inputName = null, bool isConsumed = false) =>
                (Type, DeviceType, Position, Delta, Pressure, Value, TouchId, KeyCode, InputName, Timestamp, IsConsumed) = 
                (type, deviceType, position, delta, pressure, value, touchId, keyCode, inputName, DateTime.UtcNow, isConsumed);

            public InputData WithConsumed(bool consumed) => new(Type, DeviceType, Position, Delta, Pressure, Value, TouchId, KeyCode, InputName, consumed);
        }

        public readonly struct GestureData {
            public readonly GestureType Type;
            public readonly Vector2 Position, StartPosition, Delta;
            public readonly float Distance, Angle, Duration;
            public readonly int TouchCount;
            public readonly bool IsComplete;
            public readonly DateTime Timestamp;

            public GestureData(GestureType type, Vector2 position, Vector2 startPos, Vector2 delta,
                             float distance, float angle, float duration, int touchCount, bool complete) =>
                (Type, Position, StartPosition, Delta, Distance, Angle, Duration, TouchCount, IsComplete, Timestamp) = 
                (type, position, startPos, delta, distance, angle, duration, touchCount, complete, DateTime.UtcNow);
        }

        public readonly struct TouchData {
            public readonly int Id;
            public readonly Vector2 StartPosition, CurrentPosition, LastPosition;
            public readonly float StartTime, LastMoveTime, TotalDistance;
            public readonly bool IsMoving;

            public TouchData(int id, Vector2 start, Vector2 current, Vector2 last, 
                           float startTime, float moveTime, float distance, bool moving) =>
                (Id, StartPosition, CurrentPosition, LastPosition, StartTime, LastMoveTime, TotalDistance, IsMoving) = 
                (id, start, current, last, startTime, moveTime, distance, moving);

            public TouchData WithPosition(Vector2 newPos, float time) =>
                new(Id, StartPosition, newPos, CurrentPosition, StartTime, time, 
                    TotalDistance + Vector2.Distance(CurrentPosition, newPos), true);
        }
        #endregion

        #region Properties & Indexers
        public bool IsInitialized { get; private set; }
        public InputDeviceType CurrentDeviceType { get; private set; }
        public Vector2 PointerPosition { get; private set; }
        public bool IsPointerDown { get; private set; }
        public int ActiveTouchCount { get; private set; }
        public int Priority => 1;

        public event Action<InputData> OnInputReceived;
        public event Action<GestureData> OnGestureDetected;
        public event Action<InputDeviceType> OnDeviceChanged;

        private readonly InputSettings _settings;
        private readonly ConcurrentDictionary<int, TouchData> _activeTouches = new();
        private readonly ConcurrentQueue<InputData> _inputBuffer = new();
        private readonly List<IGestureRecognizer> _gestureRecognizers = new();
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private readonly Dictionary<int, Vector2> _predictedPositions = new();
        private readonly Dictionary<int, Vector2> _velocities = new();
        private readonly object _gestureStateLock = new();

        public TouchData this[int touchId] => _activeTouches.TryGetValue(touchId, out var touch) ? touch : default;
        public bool this[GestureType type] => _gestureRecognizers.Any(r => r.Type == type && r.IsActive);
        public InputData this[InputType type] {
            get {
                while (_inputBuffer.TryDequeue(out var input))
                    if (input.Type == type) return input;
                return default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasTouch(int id) => _activeTouches.ContainsKey(id);
        
        public int TouchCount => _activeTouches.Count;
        public bool IsGestureActive(GestureType type) => this[type];
        #endregion

        #region Constructor & Initialization
        public InputManager() : this(new InputSettings()) { }
        
        public InputManager(InputSettings settings) {
            _settings = settings;
            CurrentDeviceType = Application.isMobilePlatform ? InputDeviceType.Touch : InputDeviceType.Mouse;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task InitializeAsync() {
            if (IsInitialized) return;
            
            InitializeGestureRecognizers();
            ConfigurePlatformInputs();
            
            IsInitialized = true;
            await Task.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeGestureRecognizers() {
            if (!_settings.EnableGestures) return;
            
            _gestureRecognizers.AddRange(new IGestureRecognizer[] {
                new TapGestureRecognizer(),
                new SwipeGestureRecognizer(), 
                new PinchGestureRecognizer(),
                new LongPressGestureRecognizer()
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ConfigurePlatformInputs() {
            UnityEngine.Input.multiTouchEnabled = _settings.EnableTouch;
            if (CurrentDeviceType == InputDeviceType.Touch) UnityEngine.Input.simulateMouseWithTouches = false;
        }
        #endregion

        #region Gesture Recognition System
        public interface IGestureRecognizer {
            GestureType Type { get; }
            bool IsActive { get; }
            bool TryRecognize(IReadOnlyDictionary<int, TouchData> touches, out GestureData gesture);
            void Reset();
        }

        private sealed class TapGestureRecognizer : IGestureRecognizer {
            private const float MAX_TAP_TIME = 0.2f, MAX_TAP_DISTANCE = 50f;
            
            public GestureType Type => GestureType.Tap;
            public bool IsActive { get; private set; }

            public bool TryRecognize(IReadOnlyDictionary<int, TouchData> touches, out GestureData gesture) {
                gesture = default;
                IsActive = false;

                if (touches.Count != 1) return false;

                var touch = touches.Values.First();
                var duration = Time.time - touch.StartTime;
                var distance = Vector2.Distance(touch.StartPosition, touch.CurrentPosition);

                if (duration <= MAX_TAP_TIME && distance <= MAX_TAP_DISTANCE) {
                    gesture = new GestureData(GestureType.Tap, touch.CurrentPosition, touch.StartPosition,
                                            Vector2.zero, distance, 0f, duration, 1, true);
                    return true;
                }
                return false;
            }

            public void Reset() => IsActive = false;
        }

        private sealed class SwipeGestureRecognizer : IGestureRecognizer {
            private const float MIN_SWIPE_DISTANCE = 100f, MAX_SWIPE_TIME = 1f;
            
            public GestureType Type => GestureType.Swipe;
            public bool IsActive { get; private set; }

            public bool TryRecognize(IReadOnlyDictionary<int, TouchData> touches, out GestureData gesture) {
                gesture = default;
                IsActive = false;

                if (touches.Count != 1) return false;

                var touch = touches.Values.First();
                var duration = Time.time - touch.StartTime;
                var delta = touch.CurrentPosition - touch.StartPosition;
                var distance = delta.magnitude;

                if (duration <= MAX_SWIPE_TIME && distance >= MIN_SWIPE_DISTANCE) {
                    var angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                    gesture = new GestureData(GestureType.Swipe, touch.CurrentPosition, touch.StartPosition,
                                            delta, distance, angle, duration, 1, true);
                    return true;
                }
                return false;
            }

            public void Reset() => IsActive = false;
        }

        private sealed class PinchGestureRecognizer : IGestureRecognizer {
            private float _initialDistance;
            private bool _isTracking;
            
            public GestureType Type => GestureType.Pinch;
            public bool IsActive => _isTracking;

            public bool TryRecognize(IReadOnlyDictionary<int, TouchData> touches, out GestureData gesture) {
                gesture = default;

                if (touches.Count != 2 && _isTracking) {
                    Reset();
                    return false;
                }

                if (touches.Count != 2) return false;

                var touchArray = touches.Values.ToArray();
                var currentDistance = Vector2.Distance(touchArray[0].CurrentPosition, touchArray[1].CurrentPosition);

                if (!_isTracking) {
                    _initialDistance = currentDistance;
                    _isTracking = true;
                    return false;
                }

                var center = (touchArray[0].CurrentPosition + touchArray[1].CurrentPosition) * 0.5f;
                
                gesture = new GestureData(GestureType.Pinch, center, center, Vector2.zero,
                                        currentDistance, 0f, Time.time - touchArray[0].StartTime, 2, false);
                return true;
            }

            public void Reset() {
                _isTracking = false;
                _initialDistance = 0f;
            }
        }

        private sealed class LongPressGestureRecognizer : IGestureRecognizer {
            private const float LONG_PRESS_TIME = 0.8f, MAX_MOVE_DISTANCE = 30f;
            
            public GestureType Type => GestureType.LongPress;
            public bool IsActive { get; private set; }

            public bool TryRecognize(IReadOnlyDictionary<int, TouchData> touches, out GestureData gesture) {
                gesture = default;
                IsActive = false;

                if (touches.Count != 1) return false;

                var touch = touches.Values.First();
                var duration = Time.time - touch.StartTime;
                var distance = Vector2.Distance(touch.StartPosition, touch.CurrentPosition);

                if (duration >= LONG_PRESS_TIME && distance <= MAX_MOVE_DISTANCE) {
                    gesture = new GestureData(GestureType.LongPress, touch.CurrentPosition, touch.StartPosition,
                                            Vector2.zero, distance, 0f, duration, 1, true);
                    IsActive = true;
                    return true;
                }
                return false;
            }

            public void Reset() => IsActive = false;
        }
        #endregion
        #region Input Processing Engine
        public void Tick(float deltaTime) {
            if (!IsInitialized) return;

            ProcessInputs(deltaTime);
            ProcessGestures();
            ProcessMainThreadQueue();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessInputs(float deltaTime) {
            _ = CurrentDeviceType switch {
                InputDeviceType.Touch when _settings.EnableTouch => ProcessTouchInput(deltaTime),
                InputDeviceType.Mouse when _settings.EnableMouse => ProcessMouseInput(deltaTime),
                _ => false
            };
            
            if (_settings.EnableKeyboard) ProcessKeyboardInput();
            if (_settings.EnableGamepad) ProcessGamepadInput();
        }

        private bool ProcessTouchInput(float deltaTime) {
            ActiveTouchCount = UnityEngine.Input.touchCount;

            for (int i = 0; i < ActiveTouchCount && i < _settings.MaxTouchPoints; i++) {
                var touch = UnityEngine.Input.GetTouch(i);
                ProcessTouch(touch, deltaTime);
            }

            CleanupInactiveTouches();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessTouch(Touch touch, float deltaTime) {
            var id = touch.fingerId;
            var pos = touch.position;

            switch (touch.phase) {
                case TouchPhase.Began:
                    _activeTouches[id] = new TouchData(id, pos, pos, pos, Time.time, Time.time, 0f, false);
                    QueueInput(new InputData(InputType.PointerDown, InputDeviceType.Touch, pos, touchId: id));
                    break;

                case TouchPhase.Moved when _activeTouches.TryGetValue(id, out var touchData):
                    var delta = pos - touchData.CurrentPosition;
                    var predicted = _settings.EnablePrediction ? PredictPosition(id, pos, touchData.LastPosition, deltaTime) : pos;
                    
                    _activeTouches[id] = touchData.WithPosition(pos, Time.time);
                    QueueInput(new InputData(InputType.PointerMove, InputDeviceType.Touch, predicted, delta, touchId: id));
                    break;

                case TouchPhase.Ended:
                case TouchPhase.Canceled:
                    QueueInput(new InputData(InputType.PointerUp, InputDeviceType.Touch, pos, touchId: id));
                    _activeTouches.TryRemove(id, out _);
                    _predictedPositions.Remove(id);
                    _velocities.Remove(id);
                    break;
            }
        }

        private bool ProcessMouseInput(float deltaTime) {
            PointerPosition = UnityEngine.Input.mousePosition;
            var mouseDelta = new Vector2(UnityEngine.Input.GetAxis("Mouse X"), UnityEngine.Input.GetAxis("Mouse Y")) * _settings.MouseSensitivity;

            if (UnityEngine.Input.GetMouseButtonDown(0)) {
                IsPointerDown = true;
                QueueInput(new InputData(InputType.PointerDown, InputDeviceType.Mouse, PointerPosition));
            }

            if (UnityEngine.Input.GetMouseButtonUp(0)) {
                IsPointerDown = false;
                QueueInput(new InputData(InputType.PointerUp, InputDeviceType.Mouse, PointerPosition));
            }

            if (mouseDelta.sqrMagnitude > 0.0001f)
                QueueInput(new InputData(InputType.PointerMove, InputDeviceType.Mouse, PointerPosition, mouseDelta));

            var scroll = UnityEngine.Input.mouseScrollDelta;
            if (scroll.sqrMagnitude > 0.0001f)
                QueueInput(new InputData(InputType.Scroll, InputDeviceType.Mouse, PointerPosition, scroll));

            return true;
        }

        private void ProcessKeyboardInput() {
            foreach (var key in Enum.GetValues(typeof(KeyCode)).Cast<KeyCode>().Where(k => UnityEngine.Input.GetKeyDown(k) || UnityEngine.Input.GetKeyUp(k))) {
                var isDown = UnityEngine.Input.GetKeyDown(key);
                QueueInput(new InputData(isDown ? InputType.KeyDown : InputType.KeyUp, InputDeviceType.Keyboard, 
                                       Vector2.zero, keyCode: key, value: isDown ? 1f : 0f));
            }
        }

        private void ProcessGamepadInput() {}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CleanupInactiveTouches() {
            var inactiveIds = _activeTouches.Keys.Where(id => 
                !Enumerable.Range(0, ActiveTouchCount).Any(i => UnityEngine.Input.GetTouch(i).fingerId == id)).ToArray();
            
            foreach (var id in inactiveIds) {
                _activeTouches.TryRemove(id, out _);
                _predictedPositions.Remove(id);
                _velocities.Remove(id);
            }
        }
        #endregion

        #region Input Prediction & Buffering
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector2 PredictPosition(int touchId, Vector2 currentPos, Vector2 lastPos, float deltaTime) {
            if (deltaTime <= 0f) return currentPos;

            var velocity = (currentPos - lastPos) / deltaTime;
            _velocities[touchId] = velocity;
            
            var predicted = currentPos + velocity * Time.fixedDeltaTime;
            _predictedPositions[touchId] = predicted;
            
            return predicted;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void QueueInput(InputData inputData) {
            _inputBuffer.Enqueue(inputData);
            OnInputReceived?.Invoke(inputData);
        }

        private void ProcessMainThreadQueue() {
            while (_mainThreadQueue.TryDequeue(out var action)) action?.Invoke();
        }
        #endregion

        #region Gesture Processing
        private void ProcessGestures() {
            if (!_settings.EnableGestures || _activeTouches.IsEmpty) return;

            lock (_gestureStateLock) {
                foreach (var recognizer in _gestureRecognizers) {
                    if (recognizer.TryRecognize(_activeTouches, out var gesture))
                        OnGestureDetected?.Invoke(gesture);
                }
            }
        }
        #endregion

        #region Advanced Public API
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetPointerDown(int pointerId = 0) => 
            CurrentDeviceType == InputDeviceType.Touch ? _activeTouches.ContainsKey(pointerId) : IsPointerDown;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 GetPointerPosition(int pointerId = 0) =>
            CurrentDeviceType == InputDeviceType.Touch && _activeTouches.TryGetValue(pointerId, out var touch) 
                ? touch.CurrentPosition : PointerPosition;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 GetPointerDelta(int pointerId = 0) =>
            CurrentDeviceType == InputDeviceType.Touch && _activeTouches.TryGetValue(pointerId, out var touch)
                ? touch.CurrentPosition - touch.LastPosition
                : new Vector2(UnityEngine.Input.GetAxis("Mouse X"), UnityEngine.Input.GetAxis("Mouse Y"));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsKeyPressed(KeyCode key) => UnityEngine.Input.GetKey(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsKeyDown(KeyCode key) => UnityEngine.Input.GetKeyDown(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsKeyUp(KeyCode key) => UnityEngine.Input.GetKeyUp(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InputData ConsumeInput(InputData inputData) => inputData.WithConsumed(true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterCustomGesture(IGestureRecognizer recognizer) => _gestureRecognizers.Add(recognizer);
        #endregion

        #region Framework Integration
        public void FixedTick(float fixedDeltaTime) { }
        public void LateTick(float deltaTime) { }

        public void Dispose() {
            _activeTouches.Clear();
            _inputBuffer.Clear();
            _gestureRecognizers.Clear();
            _predictedPositions.Clear();
            _velocities.Clear();
        }
        #endregion

        #region Operator Overloads
        public static implicit operator bool(InputManager manager) => manager?.IsInitialized == true;
        public static implicit operator InputDeviceType(InputManager manager) => manager?.CurrentDeviceType ?? InputDeviceType.Mouse;
        public static implicit operator Vector2(InputManager manager) => manager?.PointerPosition ?? Vector2.zero;
        #endregion
    }
}
