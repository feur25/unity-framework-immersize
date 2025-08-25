using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using com.ImmersizeFramework.Core;
using com.ImmersizeFramework.Input;
using com.ImmersizeFramework.Save;

namespace com.ImmersizeFramework.Input {
    [System.Serializable]
    public class InputBindingSystem : MonoBehaviour, IFrameworkService {
        #region Input Action Configuration
        [System.Serializable]
        public class InputAction {
            [Header("Action Configuration")]
            public string actionName = "NewAction";
            public string displayName = "New Action";
            public string description = "Description of this action";
            public bool isEnabled = true;
            
            [Header("Primary Input")]
            public KeyCode primaryKey = KeyCode.None;
            public string primaryButton = "";
            public string primaryAxis = "";
            
            [Header("Secondary Input (Optional)")]
            public KeyCode secondaryKey = KeyCode.None;
            public string secondaryButton = "";
            
            [Header("Touch/Mouse Input")]
            public bool acceptsTouch = false;
            public bool acceptsMouse = true;
            public int touchPointId = 0;
            
            [Header("Gesture Input")]
            public InputManager.GestureType gestureType = InputManager.GestureType.Tap;
            public bool acceptsGesture = false;
            
            [Header("Action Properties")]
            public InputActionType actionType = InputActionType.Button;
            public float sensitivity = 1f;
            public float deadZone = 0.1f;
            public bool invertAxis = false;
            
            [Header("Timing")]
            public float holdTime = 0.5f;
            public float doubleTapTime = 0.3f;
            public bool requiresHold = false;
            public bool allowsRepeat = false;
            
            [Header("Target Information")]
            [SerializeField] private string targetComponentName;
            [SerializeField] private string targetMethodName;
            [SerializeField] private string targetGameObjectName;

            [System.NonSerialized] private Component _targetComponent;
            [System.NonSerialized] private MethodInfo _targetMethod;
            [System.NonSerialized] private bool _isPressed;
            [System.NonSerialized] private float _pressStartTime;
            [System.NonSerialized] private float _lastTapTime;
            [System.NonSerialized] private int _tapCount;

            public Component TargetComponent => _targetComponent;
            public MethodInfo TargetMethod => _targetMethod;
            public bool IsPressed => _isPressed;
            public float CurrentValue { get; set; }
            public Vector2 CurrentVector { get; set; }

            public void SetTarget(Component component, MethodInfo method) {
                _targetComponent = component;
                _targetMethod = method;
                targetComponentName = component?.GetType().Name ?? "";
                targetMethodName = method?.Name ?? "";
                targetGameObjectName = component?.gameObject.name ?? "";
            }

            public bool TryInvoke(params object[] parameters) {
                if (_targetComponent == null || _targetMethod == null) return false;
                
                try {
                    _targetMethod.Invoke(_targetComponent, parameters);
                    return true;
                } catch (Exception e) {
                    Debug.LogError($"[InputBinding] Failed to invoke {actionName}: {e.Message}");
                    return false;
                }
            }

            public void UpdateState(bool pressed, float value = 0f, Vector2 vector = default) {
                var wasPressed = _isPressed;
                _isPressed = pressed;
                CurrentValue = value;
                CurrentVector = vector;

                if (pressed && !wasPressed) {
                    _pressStartTime = Time.time;
                    
                    if (Time.time - _lastTapTime < doubleTapTime) _tapCount++;
                    else _tapCount = 1;

                    _lastTapTime = Time.time;
                }
            }

            public bool ShouldTrigger() {
                return actionType switch {
                    InputActionType.Button => (global::System.Boolean)(_isPressed && (!requiresHold || Time.time - _pressStartTime >= holdTime)),
                    InputActionType.ButtonDown => _isPressed && Time.time - _pressStartTime < Time.deltaTime,
                    InputActionType.ButtonUp => (global::System.Boolean)(!_isPressed && Time.time - _pressStartTime < Time.deltaTime),
                    InputActionType.Axis => Mathf.Abs(CurrentValue) > deadZone,
                    InputActionType.Vector2 => CurrentVector.magnitude > deadZone,
                    InputActionType.DoubleTap => _tapCount >= 2 && Time.time - _lastTapTime < doubleTapTime,
                    _ => false,
                };
            }

            public override string ToString() => $"{displayName} ({actionName})";
        }

        public enum InputActionType {
            Button,
            ButtonDown,
            ButtonUp,
            Axis,
            Vector2,
            DoubleTap,
            Hold,
            Gesture
        }

        [System.Serializable]
        public class InputProfile {
            public string profileName = "Default";
            public string description = "Default input profile";
            public bool isActive = true;
            public List<InputAction> actions = new();
            
            public InputAction GetAction(string name) => actions.FirstOrDefault(a => a.actionName == name);
            public bool HasAction(string name) => actions.Any(a => a.actionName == name);
        }
        #endregion

        #region Properties & Configuration
        [Header("Input Binding Configuration")]
        [SerializeField] private bool _autoDetectInputs = true;
        [SerializeField] private bool _createMissingActions = true;
        [SerializeField] private bool _showDebugInfo = false;
        [SerializeField] private float _updateInterval = 0.016f;
        
        [Header("Input Profiles")]
        [SerializeField] private List<InputProfile> _inputProfiles = new();
        [SerializeField] private int _activeProfileIndex = 0;
        
        [Header("Auto-Detection Settings")]
        [SerializeField] private string[] _methodPrefixes = {"On", "Handle", "Process", "Execute"};
        [SerializeField] private string[] _methodSuffixes = {"Input", "Action", "Command", "Event"};
        [SerializeField] private string[] _excludedNamespaces = {"Unity", "System"};

        public bool IsInitialized { get; private set; }
        public int Priority => 2;
        public InputProfile ActiveProfile => _activeProfileIndex < _inputProfiles.Count ? _inputProfiles[_activeProfileIndex] : null;
        public IReadOnlyList<InputProfile> Profiles => _inputProfiles;
        public IReadOnlyList<InputAction> ActiveActions => ActiveProfile?.actions ?? new List<InputAction>();

        public event Action<InputAction> OnActionTriggered;
        public event Action<InputAction, object[]> OnActionExecuted;
        public event Action<string> OnProfileChanged;
        public event Action<InputAction> OnActionAdded;
        
        private InputManager _inputManager;
        private SaveManager _saveManager;
        private float _lastUpdateTime;
        private Dictionary<string, InputAction> _actionLookup = new();
        private List<IInputReceiver> _detectedReceivers = new();
        
        private const string PROFILES_SAVE_KEY = "InputProfiles";
        private const string ACTIVE_INDEX_SAVE_KEY = "ActiveProfileIndex";
        #endregion

        #region Input Receiver Interface
        public interface IInputReceiver {
            string GetInputReceiverName();
            Dictionary<string, string> GetInputActions();
            void OnInputAction(string actionName, object[] parameters);
        }

        [AttributeUsage(AttributeTargets.Method)]
        public class InputActionAttribute : Attribute {
            public string ActionName { get; }
            public string DisplayName { get; }
            public InputActionType ActionType { get; }
            public KeyCode DefaultKey { get; }

            public InputActionAttribute(string actionName, string displayName = null, 
                                      InputActionType actionType = InputActionType.ButtonDown, 
                                      KeyCode defaultKey = KeyCode.None) {
                ActionName = actionName;
                DisplayName = displayName ?? actionName;
                ActionType = actionType;
                DefaultKey = defaultKey;
            }
        }
        #endregion

        #region Initialization & Auto-Detection
        private void Awake() {
            InitializeDefaultProfile();
            InitializeSaveManager();
            LoadProfiles();
        }

        private async void Start() {
            await InitializeAsync();
        }

        private void InitializeSaveManager() {
            _saveManager = new SaveManager(new SaveManager.SaveSettings(
                saveDir: "InputBindings",
                encryption: false,
                compression: false,
                autoSave: true,
                autoInterval: 2f,
                format: SaveManager.SaveFormat.JSON,
                prettyPrint: true
            ));
            _ = _saveManager.InitializeAsync();
        }

        private async void LoadProfiles() {
            await Task.Delay(100);
            
            var savedProfiles = await _saveManager.LoadAsync<List<InputProfile>>(PROFILES_SAVE_KEY, SaveManager.SaveFormat.JSON);
            if (savedProfiles != null && savedProfiles.Count > 0) {
                _inputProfiles = savedProfiles;
            }
            
            var savedIndex = await _saveManager.LoadAsync<int>(ACTIVE_INDEX_SAVE_KEY, SaveManager.SaveFormat.JSON, 0);
            if (savedIndex >= 0 && savedIndex < _inputProfiles.Count) {
                _activeProfileIndex = savedIndex;
            }
            
            AutoDetectInputActions();
            RefreshActionLookup();
        }

        private async void SaveProfiles() {
            if (_saveManager != null && _saveManager.IsInitialized) {
                await _saveManager.SaveAsync(PROFILES_SAVE_KEY, _inputProfiles, SaveManager.SaveFormat.JSON);
                await _saveManager.SaveAsync(ACTIVE_INDEX_SAVE_KEY, _activeProfileIndex, SaveManager.SaveFormat.JSON);
            }
        }

        public async System.Threading.Tasks.Task InitializeAsync() {
            if (IsInitialized) return;

            _inputManager = FrameworkCore.Instance?.GetService<InputManager>();
            if (_inputManager == null) 
                Debug.LogWarning("[InputBinding] InputManager not found, input binding will be limited");

            if (_autoDetectInputs) 
                AutoDetectInputActions();
            RefreshActionLookup();

            IsInitialized = true;
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void InitializeDefaultProfile() {
            if (_inputProfiles.Count == 0) {
                var defaultProfile = new InputProfile {
                    profileName = "Default",
                    description = "Default input configuration",
                    isActive = true
                };
                
                defaultProfile.actions.AddRange(CreateDefaultActions());
                _inputProfiles.Add(defaultProfile);
            }
        }

        private List<InputAction> CreateDefaultActions() {
            return new List<InputAction> {
                new InputAction {
                    actionName = "Pause",
                    displayName = "Pause Game",
                    description = "Pause or resume the game",
                    primaryKey = KeyCode.Escape,
                    acceptsTouch = true,
                    actionType = InputActionType.ButtonDown
                },
                new InputAction {
                    actionName = "Restart",
                    displayName = "Restart Game",
                    description = "Restart the current game",
                    primaryKey = KeyCode.R,
                    actionType = InputActionType.ButtonDown
                },
                new InputAction {
                    actionName = "Interact",
                    displayName = "Interact",
                    description = "Primary interaction button",
                    primaryKey = KeyCode.Space,
                    primaryButton = "Fire1",
                    acceptsTouch = true,
                    acceptsMouse = true,
                    actionType = InputActionType.ButtonDown
                },
                new InputAction {
                    actionName = "Menu",
                    displayName = "Open Menu",
                    description = "Open main menu",
                    primaryKey = KeyCode.M,
                    actionType = InputActionType.ButtonDown
                }
            };
        }

        [ContextMenu("Auto-Detect Input Actions")]
        public void AutoDetectInputActions() {
            _detectedReceivers.Clear();
            var activeProfile = ActiveProfile;
            if (activeProfile == null) return;

            var allMonoBehaviours = FindObjectsOfType<MonoBehaviour>();
            
            foreach (var mb in allMonoBehaviours)
                DetectInputsInComponent(mb, activeProfile);

            RefreshActionLookup();
            
            if (_showDebugInfo)
                Debug.Log($"[InputBinding] Auto-detected {_detectedReceivers.Count} input receivers with {activeProfile.actions.Count} total actions");
        }

        private void DetectInputsInComponent(MonoBehaviour component, InputProfile profile) {
            var type = component.GetType();
            
            if (_excludedNamespaces.Any(ns => type.Namespace?.StartsWith(ns) == true)) return;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var foundActions = new List<InputAction>();
            var hasNewActions = false;

            foreach (var method in methods) {
                var attribute = method.GetCustomAttribute<InputActionAttribute>();
                if (attribute != null) {
                    var action = CreateActionFromAttribute(attribute, component, method);
                    foundActions.Add(action);
                    continue;
                }

                if (IsInputMethod(method.Name)) {
                    var action = CreateActionFromMethod(method, component);
                    if (action != null) foundActions.Add(action);
                }
            }

            foreach (var action in foundActions) {
                if (!profile.HasAction(action.actionName)) {
                    profile.actions.Add(action);
                    OnActionAdded?.Invoke(action);
                    hasNewActions = true;
                }
            }

            if (hasNewActions)
                SaveProfiles();

            if (foundActions.Any() && component is IInputReceiver receiver) {
                _detectedReceivers.Add(receiver);
            }
        }

        private bool IsInputMethod(string methodName) {
            return _methodPrefixes.Any(prefix => methodName.StartsWith(prefix)) &&
                   _methodSuffixes.Any(suffix => methodName.EndsWith(suffix));
        }

        private InputAction CreateActionFromAttribute(InputActionAttribute attr, Component component, MethodInfo method) {
            var action = new InputAction {
                actionName = attr.ActionName,
                displayName = attr.DisplayName,
                actionType = attr.ActionType,
                primaryKey = attr.DefaultKey,
                description = $"Auto-generated from {component.GetType().Name}.{method.Name}"
            };
            
            action.SetTarget(component, method);
            return action;
        }

        private InputAction CreateActionFromMethod(MethodInfo method, Component component) {
            if (!_createMissingActions) return null;

            var actionName = ExtractActionName(method.Name);
            var action = new InputAction {
                actionName = actionName,
                displayName = FormatDisplayName(actionName),
                description = $"Auto-detected from {component.GetType().Name}.{method.Name}",
                actionType = InputActionType.ButtonDown
            };
            
            action.SetTarget(component, method);
            return action;
        }

        private string ExtractActionName(string methodName) {
            var name = methodName;
            
            foreach (var prefix in _methodPrefixes) {
                if (name.StartsWith(prefix)) {
                    name = name.Substring(prefix.Length);
                    break;
                }
            }
            
            foreach (var suffix in _methodSuffixes) {
                if (name.EndsWith(suffix)) {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }
            
            return name;
        }

        private string FormatDisplayName(string actionName) =>
            System.Text.RegularExpressions.Regex.Replace(actionName, "(\\B[A-Z])", " $1");
        #endregion

        #region Input Processing & Updates
        private void Update() {
            if (!IsInitialized || Time.time - _lastUpdateTime < _updateInterval) return;
            
            _lastUpdateTime = Time.time;
            ProcessInputActions();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessInputActions() {
            var activeProfile = ActiveProfile;
            if (activeProfile?.actions == null) return;

            foreach (var action in activeProfile.actions) {
                if (!action.isEnabled) continue;
                
                ProcessSingleAction(action);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessSingleAction(InputAction action) {
            var inputDetected = false;
            var value = 0f;
            var vector = Vector2.zero;

            if (action.primaryKey != KeyCode.None)
                inputDetected |= CheckKeyboardInput(action, ref value);

            if (action.acceptsMouse && _inputManager != null)
                inputDetected |= CheckMouseInput(action, ref value, ref vector);

            if (action.acceptsTouch && _inputManager != null)
                inputDetected |= CheckTouchInput(action, ref value, ref vector);

            if (action.acceptsGesture && _inputManager != null)
                inputDetected |= CheckGestureInput(action);

            action.UpdateState(inputDetected, value, vector);

            if (action.ShouldTrigger())
                TriggerAction(action);
        }

        private bool CheckKeyboardInput(InputAction action, ref float value) {
            switch (action.actionType) {
                case InputActionType.Button:
                    return UnityEngine.Input.GetKey(action.primaryKey);
                
                case InputActionType.ButtonDown:
                    return UnityEngine.Input.GetKeyDown(action.primaryKey);
                
                case InputActionType.ButtonUp:
                    return UnityEngine.Input.GetKeyUp(action.primaryKey);
                
                default:
                    if (UnityEngine.Input.GetKey(action.primaryKey)) {
                        value = 1f;
                        return true;
                    }
                    return false;
            }
        }

        private bool CheckMouseInput(InputAction action, ref float value, ref Vector2 vector) {
            if (!string.IsNullOrEmpty(action.primaryButton)) {
                return action.actionType switch {
                    InputActionType.Button => UnityEngine.Input.GetButton(action.primaryButton),
                    InputActionType.ButtonDown => UnityEngine.Input.GetButtonDown(action.primaryButton),
                    InputActionType.ButtonUp => UnityEngine.Input.GetButtonUp(action.primaryButton),
                    _ => false
                };
            }

            if (action.acceptsMouse) {
                return action.actionType switch {
                    InputActionType.ButtonDown => UnityEngine.Input.GetMouseButtonDown(0),
                    InputActionType.ButtonUp => UnityEngine.Input.GetMouseButtonUp(0),
                    InputActionType.Button => UnityEngine.Input.GetMouseButton(0),
                    _ => false
                };
            }

            return false;
        }

        private bool CheckTouchInput(InputAction action, ref float value, ref Vector2 vector) {
            if (_inputManager.TouchCount > action.touchPointId) {
                var touch = _inputManager[action.touchPointId];
                if (touch.Id >= 0) {
                    vector = touch.CurrentPosition;
                    value = 1f;
                    return true;
                }
            }
            return false;
        }

        private bool CheckGestureInput(InputAction action) {
            return _inputManager.IsGestureActive(action.gestureType);
        }

        private void TriggerAction(InputAction action) {
            OnActionTriggered?.Invoke(action);

            TriggerCallbacks(action.actionName);

            var parameters = PrepareParameters(action);
            if (action.TryInvoke(parameters)) {
                OnActionExecuted?.Invoke(action, parameters);
                
                if (_showDebugInfo) {
                    Debug.Log($"[InputBinding] Executed action: {action.actionName}");
                }
            }
        }

        private object[] PrepareParameters(InputAction action) {
            var method = action.TargetMethod;
            if (method == null) return new object[0];

            var paramTypes = method.GetParameters();
            var parameters = new object[paramTypes.Length];

            for (int i = 0; i < paramTypes.Length; i++) {
                var t = paramTypes[i].ParameterType;
                parameters[i] =
                    t == typeof(float) ? action.CurrentValue :
                    t == typeof(Vector2) ? action.CurrentVector :
                    t == typeof(bool) ? action.IsPressed :
                    t == typeof(string) ? action.actionName :
                    GetDefaultValue(t);
            }

            return parameters;
        }

        private object GetDefaultValue(Type type) {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
        #endregion

        #region Public API for Dynamic Configuration
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TriggerAction(string actionName) {
            if (_actionLookup.TryGetValue(actionName, out var action)) {
                TriggerAction(action);
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetActionKey(string actionName, KeyCode newKey) {
            if (_actionLookup.TryGetValue(actionName, out var action)) {
                action.primaryKey = newKey;
                SaveProfiles();
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetActionEnabled(string actionName, bool enabled) {
            if (_actionLookup.TryGetValue(actionName, out var action)) {
                action.isEnabled = enabled;
                SaveProfiles();
                return true;
            }
            return false;
        }

        public InputAction CreateAction(string name, string displayName, InputActionType type, KeyCode key = KeyCode.None) {
            var action = new InputAction {
                actionName = name,
                displayName = displayName,
                actionType = type,
                primaryKey = key,
                description = "Dynamically created action"
            };

            var activeProfile = ActiveProfile;
            if (activeProfile != null && !activeProfile.HasAction(name)) {
                activeProfile.actions.Add(action);
                RefreshActionLookup();
                OnActionAdded?.Invoke(action);
                SaveProfiles();
            }

            return action;
        }

        public bool RemoveAction(string actionName) {
            var activeProfile = ActiveProfile;
            if (activeProfile != null) {
                var action = activeProfile.GetAction(actionName);
                if (action != null) {
                    activeProfile.actions.Remove(action);
                    RefreshActionLookup();
                    SaveProfiles();
                    return true;
                }
            }
            return false;
        }

        public void SwitchProfile(string profileName) {
            var profileIndex = _inputProfiles.FindIndex(p => p.profileName == profileName);
            if (profileIndex >= 0) {
                _activeProfileIndex = profileIndex;
                RefreshActionLookup();
                OnProfileChanged?.Invoke(profileName);
                SaveProfiles();
            }
        }

        public void CreateProfile(string name, string description = "") {
            var profile = new InputProfile {
                profileName = name,
                description = description,
                isActive = false
            };
            
            _inputProfiles.Add(profile);
            SaveProfiles();
        }

        private void RefreshActionLookup() {
            _actionLookup.Clear();
            var activeProfile = ActiveProfile;
            if (activeProfile != null) {
                foreach (var action in activeProfile.actions) {
                    _actionLookup[action.actionName] = action;
                }
            }
        }
        #endregion

        #region Framework Integration
        public void Initialize() {
            _ = InitializeAsync();
        }

        public void Dispose() {
            SaveProfiles();
            _saveManager?.Dispose();
            IsInitialized = false;
            _actionLookup.Clear();
            _detectedReceivers.Clear();
        }

        public void Cleanup() => Dispose();
        #endregion

        #region Public API Extensions
        private Dictionary<string, List<System.Action>> _actionCallbacks = new();

        public void RegisterCallback(string actionName, System.Action callback) {
            if (!_actionCallbacks.ContainsKey(actionName)) {
                _actionCallbacks[actionName] = new List<System.Action>();
            }
            _actionCallbacks[actionName].Add(callback);
        }

        public void UnregisterCallback(string actionName, System.Action callback) {
            if (_actionCallbacks.TryGetValue(actionName, out var callbacks)) {
                callbacks.Remove(callback);
                if (callbacks.Count == 0) {
                    _actionCallbacks.Remove(actionName);
                }
            }
        }

        public void CreateOrUpdateAction(string actionName, InputAction actionData) {
            var activeProfile = ActiveProfile;
            if (activeProfile == null) return;

            var existingAction = activeProfile.GetAction(actionName);
            
            if (existingAction != null) {
                existingAction.displayName = actionData.displayName;
                existingAction.primaryKey = actionData.primaryKey;
                existingAction.actionType = actionData.actionType;
                existingAction.description = actionData.description;
                existingAction.acceptsTouch = actionData.acceptsTouch;
                existingAction.acceptsMouse = actionData.acceptsMouse;
                existingAction.acceptsGesture = actionData.acceptsGesture;
            } else {
                actionData.actionName = actionName;
                activeProfile.actions.Add(actionData);
                OnActionAdded?.Invoke(actionData);
            }

            RefreshActionLookup();
            SaveProfiles();
        }

        public List<InputAction> GetAllDetectedActions() {
            return ActiveProfile?.actions?.ToList() ?? new List<InputAction>();
        }

        public bool HasAction(string actionName) {
            return _actionLookup.ContainsKey(actionName);
        }

        public string GetCurrentProfileName() {
            return ActiveProfile?.profileName ?? "None";
        }
        private void TriggerCallbacks(string actionName) {
            if (_actionCallbacks.TryGetValue(actionName, out var callbacks)) {
                foreach (var callback in callbacks) {
                    try {
                        callback?.Invoke();
                    } catch (System.Exception e) {
                        Debug.LogError($"[InputBinding] Error in callback for {actionName}: {e.Message}");
                    }
                }
            }
        }
        #endregion

        #region Inspector Utilities
        [ContextMenu("Refresh Action Detection")]
        public void RefreshDetection() {
            AutoDetectInputActions();
        }

        [ContextMenu("Create New Profile")]
        public void CreateNewProfile() {
            CreateProfile($"Profile_{_inputProfiles.Count + 1}", "New input profile");
        }

        [ContextMenu("Show Current Actions")]
        public void ShowCurrentActions() {
            var activeProfile = ActiveProfile;
            if (activeProfile != null) {
                Debug.Log($"[InputBinding] Active Profile: {activeProfile.profileName}");
                foreach (var action in activeProfile.actions) {
                    Debug.Log($"  - {action.displayName} ({action.actionName}): {action.primaryKey}");
                }
            }
        }
        #endregion

        #region Operator Overloads
        public static implicit operator bool(InputBindingSystem system) => system?.IsInitialized == true;
        public InputAction this[string actionName] => _actionLookup.TryGetValue(actionName, out var action) ? action : null;
        #endregion
    }
}