using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using UnityEngine;
using UnityCamera = UnityEngine.Camera;
using UnityInput = UnityEngine.Input;

namespace com.ImmersizeFramework.Camera {
    public enum CameraType { TopDown, TopDownHigh, TopDownMid, TopDownLow, ThirdPerson, FreeLook, FirstPerson, OvercookedFixed, BrawlStarsTopDown, TunicTopDown }

    [RequireComponent(typeof(UnityCamera))]
    public class CameraController : MonoBehaviour {
        #region Serialized Fields
        [Header("Cameras")]
        [SerializeField] private UnityCamera _topDownCam;
        [SerializeField] private UnityCamera _topDownHighCam;
        [SerializeField] private UnityCamera _topDownMidCam;
        [SerializeField] private UnityCamera _topDownLowCam;
        [SerializeField] private UnityCamera _thirdPersonCam;
        [SerializeField] private UnityCamera _freeLookCam;
        [SerializeField] private UnityCamera _firstPersonCam;

        [Header("Follow Settings")]
        [SerializeField] public Transform target;
        [SerializeField] public LayerMask cameraCollisionMask;

        [SerializeField] protected internal float cameraMinDistanceToWall = 0.4f;
        [SerializeField] protected internal float verticalSmoothTime = 0.04f;
        [SerializeField] protected internal float horizontalSmoothTime = 0.02f;
        [SerializeField] protected internal float collisionSmoothTime = 0.2f;
        [SerializeField] protected internal float followSpeed = 5f;

        [SerializeField] protected internal Vector3 cameraOriginOffset = Vector3.zero;

        [SerializeField] protected internal Vector3 topDownOffset = new Vector3(0, 6, -4);
        [SerializeField] protected internal Vector3 topDownHighOffset = new Vector3(0, 12, -8);
        [SerializeField] protected internal Vector3 topDownMidOffset = new Vector3(0, 8, -6);
        [SerializeField] protected internal Vector3 topDownLowOffset = new Vector3(0, 4, -2);
        [SerializeField] protected internal Vector3 thirdPersonOffset = new(0, 2, -10);
        [SerializeField] protected internal Vector3 freeLookOffset = new(0, 2, -12);
        [SerializeField] protected internal Vector3 firstPersonOffset = new(0, 1.6f, -2);

        [Header("Rotation Settings")]
        [SerializeField] private float _targetRotationSpeed = 10f;

        [Header("Camera Tilt Settings")]
        [SerializeField] private float _topDownTilt = 30f;
        [SerializeField] private float _topDownHighTilt = 45f;
        [SerializeField] private float _topDownMidTilt = 35f;
        [SerializeField] private float _topDownLowTilt = 25f;
        [SerializeField] private float _thirdPersonTilt = 15f;
        [SerializeField] private float _freeLookTilt = 0f;
        [SerializeField] private float _firstPersonTilt = 0f;
        [SerializeField] private float _tiltSmoothTime = 0.1f;

        [Header("Brawl Stars Camera Settings")]
        [SerializeField] private float _brawlStarsHeight = 15f;
        [SerializeField] private float _brawlStarsDistance = 12f;
        [SerializeField] private float _brawlStarsAngle = 55f;
        [SerializeField] private Vector2 _brawlStarsLevelMin = new Vector2(-20, -20);
        [SerializeField] private Vector2 _brawlStarsLevelMax = new Vector2(20, 20);
        [SerializeField] private float _brawlStarsSmoothTime = 0.15f;

        private Vector3 _brawlStarsVelocity = Vector3.zero;
        [Header("Tunic Camera Settings")]
        [SerializeField] private Vector2 _levelMin;
        [SerializeField] private Vector2 _levelMax;

        [SerializeField] private float _tunicAngle = 45f;
        [SerializeField] private float _tunicHeight = 15f;
        [SerializeField] private float _tunicZoomMin = 6f;
        [SerializeField] private float _tunicZoomMax = 20f;
        [SerializeField] private float _tunicZoomSensitivity = 0.2f;
        [SerializeField] private float _tunicPanSensitivity = 0.025f;
        [SerializeField] private float _tunicFollowSmooth = 0.2f;
        [SerializeField] private float _tunicRotateSmooth = 0.15f;
        [SerializeField] private float _tunicClampMargin = 20f;
        [SerializeField] private float _tunicTargetYOffset = 1.2f;
        [SerializeField] private float _tunicMinCollisionHeight = 3f;
        [SerializeField] private LayerMask _tunicCameraCollisionMask;

        private float _tunicZoom = 12f;
        private Vector3 _tunicVelocity;

        [Header("General Zoom Settings")]
        [SerializeField] protected internal float _topDownZoomMin = 3f;
        [SerializeField] protected internal float _topDownZoomMax = 15f;
        [SerializeField] protected internal float _topDownZoomSensitivity = 1f;
        [SerializeField] protected internal float _thirdPersonZoomMin = 5f;
        [SerializeField] protected internal float _thirdPersonZoomMax = 20f;
        [SerializeField] protected internal float _thirdPersonZoomSensitivity = 1f;
        [SerializeField] protected internal float _brawlStarsZoomMin = 8f;
        [SerializeField] protected internal float _brawlStarsZoomMax = 25f;
        [SerializeField] protected internal float _brawlStarsZoomSensitivity = 1f;

        [SerializeField] protected internal float _topDownZoom = 8f;
        [SerializeField] protected float _thirdPersonZoom = 10f;
        [SerializeField] protected float _brawlStarsZoom = 15f;

        #endregion
        [SerializeField] private Bounds _overcookedLevelBounds;
        [SerializeField] private float _overcookedMargin = 2f;

        private readonly Dictionary<CameraType, UnityCamera> _cameraDict = new();
        private CameraType _currentType;

        private CancellationTokenSource _cancelSource;

        private float _freeLookYaw = 0f;
        private float _freeLookPitch = 20f;
        private float _lastMouseInputTime = 0f;

        private readonly float _mouseSensitivity = 1.5f;
        private readonly float _pitchMin = 10f, _pitchMax = 70f;
        private readonly float _returnDelay = 5f;

        private readonly bool _isInitializedFromConstructor = false;
        private bool _cameraReset = false;

        private Vector3 _delayedHorizontalPosition, _delayedVerticalPosition, _verticalVelocity, _horizontalVelocity = Vector3.zero;
        private float _cameraDistance, _distanceVelocity = 0f;
        private Vector3 _topDownVelocity = Vector3.zero;

        public CameraType CurrentCameraType {
            get => _currentType;
            set {
                if (_currentType == value) return;

                _currentType = value;

                ResetCameraVelocities();
                SwitchCamera(_currentType);
            }
        }

        public UnityCamera this[CameraType type] {
            get => _cameraDict.TryGetValue(type, out var cam) ? cam : null;
            set {
                if (value != null)
                    _cameraDict[type] = value;
            }
        }

        #region Constructors
        public CameraController() {
            _cameraDict.Clear();
            _currentType = CameraType.TopDown;
        }

        public CameraController(Transform target, float followSpeed) : this() {
            this.target = target;
            this.followSpeed = followSpeed;
        }

        public CameraController(Vector3 topDownOffset, Vector3 thirdPersonOffset, Vector3 freeLookOffset, Vector3 firstPersonOffset) : this() {
            this.topDownOffset = topDownOffset;
            this.thirdPersonOffset = thirdPersonOffset;
            this.freeLookOffset = freeLookOffset;
            this.firstPersonOffset = firstPersonOffset;
        }

        public void Init() {
            this[CameraType.TopDown] = _topDownCam;
            this[CameraType.TopDownHigh] = _topDownHighCam;
            this[CameraType.TopDownMid] = _topDownMidCam;
            this[CameraType.TopDownLow] = _topDownLowCam;
            this[CameraType.ThirdPerson] = _thirdPersonCam;
            this[CameraType.FreeLook] = _freeLookCam;
            this[CameraType.FirstPerson] = _firstPersonCam;
            this[CameraType.OvercookedFixed] = _topDownCam;
            this[CameraType.BrawlStarsTopDown] = _topDownCam;
            this[CameraType.TunicTopDown] = _topDownCam;

            if (!Initialize())
                target ??= GameObject.FindGameObjectWithTag("Player")?.transform;
        }
        #endregion

        private void Reset() => Awake();

        private void Awake() {
            if (_isInitializedFromConstructor) return;
            Init();
        }

        private void Start() {
            if (_isInitializedFromConstructor) return;

            if (!Initialize())
                target ??= GameObject.FindGameObjectWithTag("Player")?.transform;
        }

        protected bool Initialize() {
            if (target == null) return false;

            if (_cancelSource != null) {
                _cancelSource.Cancel();
                _cancelSource.Dispose();
            }

            ResetCameraVelocities();

            CurrentCameraType = CameraType.TopDown;
            _cancelSource = new CancellationTokenSource();
            _ = ListenAsync(_cancelSource.Token);

            return true;
        }

        private async Task ListenAsync(CancellationToken token) {
            for (; !token.IsCancellationRequested;) {
                await Task.Delay(16, token);

                if (!_cameraDict.TryGetValue(_currentType, out var cam) || cam == null || target == null) continue;
                if (!this || !gameObject.activeInHierarchy) continue;

                _ = _currentType switch {
                    CameraType.TopDown => Run(() => SkylandersTopDownFollow(cam, topDownMidOffset)),
                    CameraType.TopDownHigh => Run(() => SkylandersTopDownFollow(cam, topDownHighOffset)),
                    CameraType.TopDownMid => Run(() => SkylandersTopDownFollow(cam, topDownMidOffset)),
                    CameraType.TopDownLow => Run(() => SkylandersTopDownFollow(cam, topDownLowOffset)),
                    CameraType.ThirdPerson => Run(() => { SmoothFollow(cam, thirdPersonOffset, true); RotateTargetToCamera(cam); }),
                    CameraType.FreeLook => Run(() => { HandleFreeLook(cam); RotateTargetToCamera(cam); }),
                    CameraType.FirstPerson => Run(() => { FirstPersonFollow(cam); RotateTargetToCamera(cam); }),
                    CameraType.OvercookedFixed => Run(() => OvercookedFixedTopDownCamera(cam)),
                    CameraType.BrawlStarsTopDown => Run(() => BrawlStarsTopDownFollow(cam, topDownOffset)),
                    CameraType.TunicTopDown => Run(() => TunicTopDownCameraMobile(cam)),
                    _ => Run(() => Debug.LogWarning($"[CameraController] Unknown CameraType: {_currentType}"))
                };
            }
        }

        private int Run(Action action) { action?.Invoke(); return 0; }

        private void SwitchCamera(CameraType type) {
            foreach (var cam in _cameraDict.Values)
                if (cam != null) cam.enabled = false;

            _ = type switch {
                CameraType.TopDown => EnableCam(CameraType.TopDown),
                CameraType.TopDownHigh => EnableCam(CameraType.TopDownHigh),
                CameraType.TopDownMid => EnableCam(CameraType.TopDownMid),
                CameraType.TopDownLow => EnableCam(CameraType.TopDownLow),
                CameraType.ThirdPerson => EnableCam(CameraType.ThirdPerson),
                CameraType.FreeLook => EnableCam(CameraType.FreeLook),
                CameraType.FirstPerson => EnableCam(CameraType.FirstPerson),
                CameraType.OvercookedFixed => EnableCam(CameraType.OvercookedFixed),
                CameraType.BrawlStarsTopDown => EnableCam(CameraType.BrawlStarsTopDown),
                CameraType.TunicTopDown => EnableCam(CameraType.TunicTopDown),
                _ => LogUnknownType(type)
            };
        }

        private int EnableCam(CameraType type) {
            if (_cameraDict.TryGetValue(type, out var cam) && cam != null) { cam.enabled = true; return 1; }
            Debug.LogWarning($"[CameraController] Camera for {type} is null."); return 0;
        }

        private int LogUnknownType(CameraType type) {
            Debug.LogWarning($"[CameraController] Unknown CameraType: {type}");
            return 0;
        }

        private void ResetCameraVelocities() {
            _topDownVelocity = Vector3.zero;
            _brawlStarsVelocity = Vector3.zero;
            _tunicVelocity = Vector3.zero;
            _delayedHorizontalPosition = Vector3.zero;
            _delayedVerticalPosition = Vector3.zero;
            _verticalVelocity = Vector3.zero;
            _horizontalVelocity = Vector3.zero;
            _distanceVelocity = 0f;
        }

        #region Camera Methods

        private void SkylandersTopDownFollow(UnityCamera cam, Vector3 offset) {
            if (target == null || cam == null) return;

            float currentTilt = _currentType switch {
                CameraType.TopDown => _topDownTilt,
                CameraType.TopDownHigh => _topDownHighTilt,
                CameraType.TopDownMid => _topDownMidTilt,
                CameraType.TopDownLow => _topDownLowTilt,
                _ => _topDownTilt
            };

            Vector3 desiredPosition = target.position + offset * (_topDownZoom / 8f) + Vector3.down;

            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, desiredPosition, ref _topDownVelocity, _topDownZoomSensitivity);

            Vector3 lookAtTarget = target.position + Vector3.up * 1.5f;
            Quaternion lookRotation = Quaternion.LookRotation(lookAtTarget - cam.transform.position);

            Quaternion tiltRotation = Quaternion.Euler(currentTilt, lookRotation.eulerAngles.y, 0f);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, tiltRotation, followSpeed * Time.deltaTime);

            Vector3 camForward = cam.transform.forward;
            camForward.y = 0f;
            if (camForward.sqrMagnitude > 0.001f) {
                Quaternion targetRotation = Quaternion.LookRotation(camForward, Vector3.up);
            }
        }

        private void BrawlStarsTopDownFollow(UnityCamera cam, Vector3 offset) {
            if (!cam || !target) return;

            Quaternion rotation = Quaternion.Euler(_brawlStarsAngle, 0f, 0f);
            Vector3 baseOffset = rotation * new Vector3(0f, 0f, -_brawlStarsDistance * (_brawlStarsZoom / 15f)) + Vector3.up * _brawlStarsHeight;
            Vector3 rawTarget = target.position + baseOffset;

            rawTarget.x = Mathf.Clamp(rawTarget.x, _brawlStarsLevelMin.x, _brawlStarsLevelMax.x);
            rawTarget.z = Mathf.Clamp(rawTarget.z, _brawlStarsLevelMin.y, _brawlStarsLevelMax.y);

            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, rawTarget, ref _brawlStarsVelocity, _brawlStarsSmoothTime);

            Vector3 lookTarget = target.position + Vector3.up * 1.5f;
            Quaternion lookRot = Quaternion.LookRotation(lookTarget - cam.transform.position);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, lookRot, followSpeed * Time.deltaTime);
        }

        private void TunicTopDownCameraMobile(UnityCamera cam) {
            if (cam == null || target == null) return;

            if (UnityInput.touchCount == 2) {
                Touch t0 = UnityInput.GetTouch(0), t1 = UnityInput.GetTouch(1);
                float prevMag = (t0.position - t0.deltaPosition - (t1.position - t1.deltaPosition)).magnitude;
                float currMag = (t0.position - t1.position).magnitude;
                _tunicZoom = Mathf.Clamp(_tunicZoom + (prevMag - currMag) * _tunicZoomSensitivity * Time.deltaTime, _tunicZoomMin, _tunicZoomMax);
                cam.transform.position += (Vector3)(-(t0.deltaPosition + t1.deltaPosition) * 0.5f * _tunicPanSensitivity);
            }

            Vector3 desiredPos = target.position + Quaternion.Euler(_tunicAngle, 0f, 0f) * new Vector3(0f, 0f, -Mathf.Lerp(_tunicZoom, _tunicZoomMax, 0.5f)) + Vector3.up * _tunicHeight;
            desiredPos.x = Mathf.Clamp(desiredPos.x, _levelMin.x, _levelMax.x);
            desiredPos.z = Mathf.Clamp(desiredPos.z, _levelMin.y, _levelMax.y);

            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, desiredPos, ref _tunicVelocity, _tunicFollowSmooth);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, Quaternion.LookRotation((target.position + Vector3.up * _tunicTargetYOffset - cam.transform.position).normalized), _tunicRotateSmooth);

            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
            if (Physics.Raycast(ray, out RaycastHit hit, 1000f, _tunicCameraCollisionMask)) {
                float h = hit.point.y + _tunicMinCollisionHeight;
                if (cam.transform.position.y < h) cam.transform.position = new Vector3(cam.transform.position.x, h, cam.transform.position.z);
            }

            cam.transform.position = new Vector3(
                Mathf.Clamp(cam.transform.position.x, _levelMin.x - _tunicClampMargin, _levelMax.x + _tunicClampMargin),
                cam.transform.position.y,
                Mathf.Clamp(cam.transform.position.z, _levelMin.y - _tunicClampMargin, _levelMax.y + _tunicClampMargin)
            );
        }
        private void OvercookedFixedTopDownCamera(UnityCamera cam) {
            if (!cam) return;

            var c = _overcookedLevelBounds.center;
            var s = _overcookedLevelBounds.size + Vector3.one * _overcookedMargin * 2f;
            float aspect = (float)Screen.width / Screen.height;

            cam.orthographic = true;

            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            float targetSize = Mathf.Max(s.z * 0.5f, s.x * 0.5f / aspect);

            cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, targetSize, followSpeed * Time.deltaTime);
            Vector3 pos = new(c.x, c.y + 10f, c.z);
            Vector2 pan = Vector2.zero;

            cam.transform.position = new Vector3(
                Mathf.Clamp((pos + new Vector3(pan.x, 0f, pan.y)).x, _overcookedLevelBounds.min.x, _overcookedLevelBounds.max.x),
                pos.y,
                Mathf.Clamp((pos + new Vector3(pan.x, 0f, pan.y)).z, _overcookedLevelBounds.min.z, _overcookedLevelBounds.max.z)
            );
        }

        private void SmoothFollow(UnityCamera cam, Vector3 offset, bool lookAt) {
            if (target == null) return;

            float currentTilt = _currentType switch {
                CameraType.ThirdPerson => _thirdPersonTilt,
                CameraType.FreeLook => _freeLookTilt,
                CameraType.FirstPerson => _firstPersonTilt,
                _ => 0f
            };

            Vector3 followPosition = target.position + cameraOriginOffset;

            Vector3 horizontalOffset = Vector3.ProjectOnPlane(offset, Vector3.up);
            Vector3 verticalOffset = offset - horizontalOffset;

            Vector3 horizontalTarget = Vector3.ProjectOnPlane(followPosition + horizontalOffset, Vector3.up);
            Vector3 verticalTarget = followPosition + verticalOffset - horizontalTarget;

            _delayedHorizontalPosition = Vector3.SmoothDamp(_delayedHorizontalPosition, horizontalTarget,
                ref _horizontalVelocity, horizontalSmoothTime, Mathf.Infinity, Time.deltaTime
            );

            _delayedVerticalPosition = Vector3.SmoothDamp(_delayedVerticalPosition, verticalTarget,
                ref _verticalVelocity, verticalSmoothTime, Mathf.Infinity, Time.deltaTime
            );

            Vector3 smoothedFollowPosition = _delayedHorizontalPosition + _delayedVerticalPosition;
            Vector3 camDirection = (cam.transform.position - smoothedFollowPosition).normalized;
            Vector3 rawCamOffset = offset.magnitude * camDirection;

            if (Physics.Raycast(smoothedFollowPosition, rawCamOffset.normalized, out RaycastHit hit, offset.magnitude + cameraMinDistanceToWall, cameraCollisionMask)) {
                float camMargin = cameraMinDistanceToWall / Mathf.Sin(Vector3.Angle(hit.normal, -rawCamOffset.normalized) * Mathf.Deg2Rad);
                _cameraDistance = Mathf.SmoothDamp(_cameraDistance, hit.distance - camMargin, ref _distanceVelocity, collisionSmoothTime);
            }
            else _cameraDistance = Mathf.SmoothDamp(_cameraDistance, offset.magnitude, ref _distanceVelocity, collisionSmoothTime);

            cam.transform.position = smoothedFollowPosition + rawCamOffset.normalized * _cameraDistance;

            if (lookAt) {
                Quaternion lookRotation = Quaternion.LookRotation(followPosition - cam.transform.position);

                Quaternion tiltRotation = Quaternion.Euler(lookRotation.eulerAngles.x + currentTilt, lookRotation.eulerAngles.y, 0f);
                cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, tiltRotation, followSpeed * Time.deltaTime);
            }
        }

        private void HandleFreeLook(UnityCamera cam) {
            if (target == null || cam == null) return;

#if UNITY_EDITOR || UNITY_STANDALONE
            float mx = UnityInput.GetAxis("Mouse X"), my = UnityInput.GetAxis("Mouse Y");
            if (Mathf.Abs(mx) > 0.01f || Mathf.Abs(my) > 0.01f) {
                _freeLookYaw += mx * _mouseSensitivity;
                _freeLookPitch = Mathf.Clamp(_freeLookPitch - my * _mouseSensitivity, _pitchMin, _pitchMax);
                _lastMouseInputTime = Time.time;
                _cameraReset = false;
            }
#else
            if (UnityInput.touchCount == 1 && UnityInput.GetTouch(0).phase == TouchPhase.Moved) {
                var d = UnityInput.GetTouch(0).deltaPosition * _mouseSensitivity * 0.1f;
                _freeLookYaw += d.x;
                _freeLookPitch = Mathf.Clamp(_freeLookPitch - d.y, _pitchMin, _pitchMax);
                _lastMouseInputTime = Time.time;
                _cameraReset = false;
            }
            if (UnityInput.touchCount == 2) {
                var t0 = UnityInput.GetTouch(0); var t1 = UnityInput.GetTouch(1);
                freeLookOffset.z = Mathf.Clamp(freeLookOffset.z + ((t0.position - t1.position).magnitude - (t0.position - t0.deltaPosition - (t1.position - t1.deltaPosition)).magnitude) * 0.01f, -20f, -2f);
            }
#endif

            Quaternion rot = Quaternion.Euler(_freeLookPitch, _freeLookYaw, 0f);
            Vector3 dir = rot * Vector3.back, pos = target.position + dir * freeLookOffset.magnitude;
            float dist = freeLookOffset.magnitude;
            if (Physics.Raycast(target.position, dir, out RaycastHit hit, dist + cameraMinDistanceToWall, cameraCollisionMask)) {
                float margin = cameraMinDistanceToWall / Mathf.Sin(Vector3.Angle(hit.normal, -dir) * Mathf.Deg2Rad);
                dist = Mathf.Min(hit.distance - margin, dist);
            }
            pos = target.position + dir * dist;
            Vector3 v = Vector3.zero;
            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, pos, ref v, 0.1f);

            if (Time.time - _lastMouseInputTime > _returnDelay && !_cameraReset)
                _ = ResetCameraAfterDelay(_freeLookCam, _returnDelay, _cancelSource.Token);
            else
                cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, rot, followSpeed * Time.deltaTime);
        }

        private async Task ResetCameraAfterDelay(UnityCamera cam, float delay, CancellationToken token) {
            try {
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
                if (!token.IsCancellationRequested) {
                    cam.transform.position = target.position + freeLookOffset;
                    cam.transform.LookAt(target);
                    _cameraReset = true;
                }
            } catch (TaskCanceledException e) {
                Debug.Log($"[CameraController] ResetCameraAfterDelay was cancelled: {e.Message}");
            }
        }

        private void FirstPersonFollow(UnityCamera cam) {
            if (target == null || cam == null) return;

#if UNITY_EDITOR || UNITY_STANDALONE
            float scroll = UnityInput.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f) {
                cam.fieldOfView = Mathf.Clamp(cam.fieldOfView - scroll * 5f, 30f, 90f);
            }
#else
            if (UnityInput.touchCount == 2) {
                Touch t0 = UnityInput.GetTouch(0), t1 = UnityInput.GetTouch(1);
                float prevMag = (t0.position - t0.deltaPosition - (t1.position - t1.deltaPosition)).magnitude;
                float currMag = (t0.position - t1.position).magnitude;
                cam.fieldOfView = Mathf.Clamp(cam.fieldOfView - (currMag - prevMag) * 0.1f, 30f, 90f);
            }
#endif

            Vector3 desiredPosition = target.position + firstPersonOffset;
            cam.transform.position = Vector3.Lerp(cam.transform.position, desiredPosition, followSpeed * Time.deltaTime);

            Quaternion targetRotation = Quaternion.LookRotation(target.forward, Vector3.up);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, targetRotation, followSpeed * Time.deltaTime);
        }

        private void RotateTargetToCamera(UnityCamera cam) {
            if (target == null || cam == null) return;

            Vector3 camForward = cam.transform.forward;
            camForward.y = 0f;
            if (camForward.sqrMagnitude < 0.001f) return;

            Quaternion targetRotation = Quaternion.LookRotation(camForward, Vector3.up);
        }

        public static async Task ShakeCameraAsync(UnityCamera cam, float duration, float magnitude) {
            if (cam == null) { Debug.LogWarning("[CameraController] ShakeCameraAsync: Camera is null."); return; }

            Vector3 orig = cam.transform.position; float t = 0f;

            while (t < duration) {
                cam.transform.position = orig + new Vector3(UnityEngine.Random.Range(-1f, 1f) * magnitude, UnityEngine.Random.Range(-1f, 1f) * magnitude, 0);
                t += Time.deltaTime; await Task.Yield();
            }

            cam.transform.position = orig;
        }
        #endregion

        private void OnDestroy() {
            _cancelSource?.Cancel();
            _cancelSource?.Dispose();
        }
    }
}
