using System;
using System.Reflection;
using UnityEngine;
using UnityCamera = UnityEngine.Camera;
using System.Threading.Tasks;


namespace com.ImmersizeFramework.Camera {
    public class CameraControllerBuilder {
        private readonly CameraController _controller;
        public CameraControllerBuilder(CameraController controller) {
            _controller = controller;
        }

        public static CameraControllerBuilder Create(CameraController controller) {
            return new CameraControllerBuilder(controller);
        }

        public CameraControllerBuilder WithTarget(Transform target) {
            _controller.target = target;
            return this;
        }

        public CameraControllerBuilder WithFollowSpeed(float speed) {
            _controller.followSpeed = speed;
            return this;
        }
        
        public CameraControllerBuilder WithSmoothTimes(float horizontal, float vertical, float collision = 0.2f){
            _controller.horizontalSmoothTime = horizontal;
            _controller.verticalSmoothTime = vertical;
            _controller.collisionSmoothTime = collision;
            return this;
        }

        public CameraControllerBuilder WithTopDownOffset(Vector3 offset) {
            _controller.topDownOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithTopDownHighOffset(Vector3 offset) {
            _controller.topDownHighOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithTopDownZoom(float min, float max, float sensitivity, float zoom = 8f) {
            _controller._topDownZoomMin = min;
            _controller._topDownZoomMax = max;
            _controller._topDownZoomSensitivity = sensitivity;
            _controller._topDownZoom = zoom;

            return this;
        }

        public CameraControllerBuilder WithTopDownMidOffset(Vector3 offset) {
            _controller.topDownMidOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithTopDownLowOffset(Vector3 offset) {
            _controller.topDownLowOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithThirdPersonOffset(Vector3 offset) {
            _controller.thirdPersonOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithFreeLookOffset(Vector3 offset) {
            _controller.freeLookOffset = offset;
            return this;
        }
        
        public CameraControllerBuilder WithFirstPersonOffset(Vector3 offset) {
            _controller.firstPersonOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithCameraOriginOffset(Vector3 offset) {
            _controller.cameraOriginOffset = offset;
            return this;
        }

        public CameraControllerBuilder WithTopDownTilt(float tilt) {
            var field = typeof(CameraController).GetField("_topDownTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithTopDownHighTilt(float tilt) {
            var field = typeof(CameraController).GetField("_topDownHighTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithTopDownMidTilt(float tilt) {
            var field = typeof(CameraController).GetField("_topDownMidTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithTopDownZoom(float zoom) {
            var field = typeof(CameraController).GetField("_topDownZoom", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, zoom);
            return this;
        }

        public CameraControllerBuilder WithTopDownLowTilt(float tilt) {
            var field = typeof(CameraController).GetField("_topDownLowTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithThirdPersonTilt(float tilt) {
            var field = typeof(CameraController).GetField("_thirdPersonTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithFreeLookTilt(float tilt) {
            var field = typeof(CameraController).GetField("_freeLookTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithFirstPersonTilt(float tilt) {
            var field = typeof(CameraController).GetField("_firstPersonTilt", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, tilt);
            return this;
        }

        public CameraControllerBuilder WithTiltSmoothTime(float smoothTime) {
            var field = typeof(CameraController).GetField("_tiltSmoothTime", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, smoothTime);
            return this;
        }

        public CameraControllerBuilder WithCollisionSettings(LayerMask mask, float minDistance = 0.4f) {
            _controller.cameraCollisionMask = mask;
            _controller.cameraMinDistanceToWall = minDistance;
            return this;
        }

        public CameraControllerBuilder WithBrawlStarsSettings(float height, float distance, float angle, Vector2 levelMin, Vector2 levelMax, float smoothTime) {
            SetPrivateField("_brawlStarsHeight", height);
            SetPrivateField("_brawlStarsDistance", distance);
            SetPrivateField("_brawlStarsAngle", angle);
            SetPrivateField("_brawlStarsLevelMin", levelMin);
            SetPrivateField("_brawlStarsLevelMax", levelMax);
            SetPrivateField("_brawlStarsSmoothTime", smoothTime);
            return this;
        }

        public CameraControllerBuilder WithTunicSettings(Vector2 levelMin, Vector2 levelMax, float angle, float height, float zoomMin, float zoomMax) {
            SetPrivateField("_levelMin", levelMin);
            SetPrivateField("_levelMax", levelMax);
            SetPrivateField("_tunicAngle", angle);
            SetPrivateField("_tunicHeight", height);
            SetPrivateField("_tunicZoomMin", zoomMin);
            SetPrivateField("_tunicZoomMax", zoomMax);
            return this;
        }

        public CameraControllerBuilder WithZoomSettings(float tunicZoomMin, float tunicZoomMax, float tunicZoomSensitivity = 0.2f, float tunicPanSensitivity = 0.025f) {
            SetPrivateField("_tunicZoomMin", tunicZoomMin);
            SetPrivateField("_tunicZoomMax", tunicZoomMax);
            SetPrivateField("_tunicZoomSensitivity", tunicZoomSensitivity);
            SetPrivateField("_tunicPanSensitivity", tunicPanSensitivity);
            return this;
        }

        public CameraControllerBuilder WithTopDownZoomSettings(float zoomMin = 3f, float zoomMax = 15f, float zoomSensitivity = 1f, float initialZoom = 8f) {
            SetPrivateField("_topDownZoomMin", zoomMin);
            SetPrivateField("_topDownZoomMax", zoomMax);
            SetPrivateField("_topDownZoomSensitivity", zoomSensitivity);
            SetPrivateField("_topDownZoom", initialZoom);
            return this;
        }

        public CameraControllerBuilder WithThirdPersonZoomSettings(float zoomMin = 5f, float zoomMax = 20f, float zoomSensitivity = 1f, float initialZoom = 10f) {
            SetPrivateField("_thirdPersonZoomMin", zoomMin);
            SetPrivateField("_thirdPersonZoomMax", zoomMax);
            SetPrivateField("_thirdPersonZoomSensitivity", zoomSensitivity);
            SetPrivateField("_thirdPersonZoom", initialZoom);
            return this;
        }

        public CameraControllerBuilder WithBrawlStarsZoomSettings(float zoomMin = 8f, float zoomMax = 25f, float zoomSensitivity = 1f, float initialZoom = 15f) {
            SetPrivateField("_brawlStarsZoomMin", zoomMin);
            SetPrivateField("_brawlStarsZoomMax", zoomMax);
            SetPrivateField("_brawlStarsZoomSensitivity", zoomSensitivity);
            SetPrivateField("_brawlStarsZoom", initialZoom);
            return this;
        }

        public CameraControllerBuilder WithOvercookedSettings(Bounds levelBounds, float margin = 2f) {
            SetPrivateField("_overcookedLevelBounds", levelBounds);
            SetPrivateField("_overcookedMargin", margin);
            return this;
        }

        public CameraControllerBuilder WithTopDownCamera(UnityCamera cam) {
            _controller[CameraType.TopDown] = cam;
            return this;
        }

        public CameraControllerBuilder WithTopDownHighCamera(UnityCamera cam) {
            _controller[CameraType.TopDownHigh] = cam;
            return this;
        }

        public CameraControllerBuilder WithTopDownMidCamera(UnityCamera cam) {
            _controller[CameraType.TopDownMid] = cam;
            return this;
        }

        public CameraControllerBuilder WithTopDownLowCamera(UnityCamera cam) {
            _controller[CameraType.TopDownLow] = cam;
            return this;
        }

        public CameraControllerBuilder WithThirdPersonCamera(UnityCamera cam) {
            _controller[CameraType.ThirdPerson] = cam;
            return this;
        }

        public CameraControllerBuilder WithFreeLookCamera(UnityCamera cam) {
            _controller[CameraType.FreeLook] = cam;
            return this;
        }

        public CameraControllerBuilder WithFirstPersonCamera(UnityCamera cam) {
            _controller[CameraType.FirstPerson] = cam;
            return this;
        }

        public CameraControllerBuilder WithDefaultTopDownSettings() {
            return this
                .WithTopDownOffset(new Vector3(0, 6, -4))
                .WithTopDownTilt(30f)
                .WithFollowSpeed(5f);
        }

        public CameraControllerBuilder WithDefaultThirdPersonSettings() {
            return this
                .WithThirdPersonOffset(new Vector3(0, 2, -10))
                .WithThirdPersonTilt(15f)
                .WithFollowSpeed(5f);
        }

        public CameraControllerBuilder WithDefaultFreeLookSettings() {
            return this
                .WithFreeLookOffset(new Vector3(0, 2, -12))
                .WithFreeLookTilt(0f)
                .WithFollowSpeed(5f);
        }

        public CameraControllerBuilder WithDefaultFirstPersonSettings() {
            return this
                .WithFirstPersonOffset(new Vector3(0, 1.6f, -2))
                .WithFirstPersonTilt(0f)
                .WithFollowSpeed(8f);
        }

        public CameraControllerBuilder WithAllTopDownSettings(Vector3 standard, Vector3 high, Vector3 mid, Vector3 low) {
            return this
                .WithTopDownOffset(standard)
                .WithTopDownHighOffset(high)
                .WithTopDownMidOffset(mid)
                .WithTopDownLowOffset(low);
        }

        public CameraControllerBuilder WithAllTopDownTilts(float standard, float high, float mid, float low) {
            return this
                .WithTopDownTilt(standard)
                .WithTopDownHighTilt(high)
                .WithTopDownMidTilt(mid)
                .WithTopDownLowTilt(low);
        }

        public CameraControllerBuilder WithPreset(CameraPreset preset) {
            return preset switch {
                CameraPreset.ActionGame => this
                    .WithFollowSpeed(8f)
                    .WithSmoothTimes(0.02f, 0.04f, 0.1f)
                    .WithThirdPersonOffset(new Vector3(0, 3, -8))
                    .WithThirdPersonTilt(20f),

                CameraPreset.RPG => this
                    .WithFollowSpeed(3f)
                    .WithSmoothTimes(0.05f, 0.08f, 0.3f)
                    .WithTopDownOffset(new Vector3(0, 8, -6))
                    .WithTopDownTilt(45f)
                    .WithZoomSettings(8f, 20f),

                CameraPreset.Racing => this
                    .WithFollowSpeed(12f)
                    .WithSmoothTimes(0.01f, 0.02f, 0.05f)
                    .WithThirdPersonOffset(new Vector3(0, 2, -12))
                    .WithThirdPersonTilt(10f),

                CameraPreset.Platformer => this
                    .WithFollowSpeed(6f)
                    .WithSmoothTimes(0.03f, 0.06f, 0.15f)
                    .WithThirdPersonOffset(new Vector3(2, 1, -8))
                    .WithThirdPersonTilt(5f),

                CameraPreset.Strategy => this
                    .WithFollowSpeed(2f)
                    .WithSmoothTimes(0.1f, 0.1f, 0.5f)
                    .WithTopDownOffset(new Vector3(0, 15, -10))
                    .WithTopDownTilt(60f)
                    .WithZoomSettings(15f, 40f)
                    .WithTopDownZoomSettings(initialZoom: 20f),

                CameraPreset.PlayBook => this
                    .WithFollowSpeed(5f)
                    .WithSmoothTimes(0.02f, 0.04f, 0.1f)
                    .WithTopDownMidOffset(new Vector3(0, 4, -8))
                    .WithTopDownTilt(20f)
                    .WithTopDownZoomSettings(5f, 20f, .02f, 10f),

                CameraPreset.Dialogue => this
                    .WithFollowSpeed(5f)
                    .WithSmoothTimes(0.02f, 0.04f, 0.1f)
                    .WithTopDownMidOffset(new Vector3(0, 5, -8))
                    .WithTopDownTilt(24f)
                    .WithTopDownZoomSettings(5f, 20f, .08f, 5f),

                _ => this,
            };
        }

        public CameraController Build()
        {
            _controller.Init();
            return _controller;
        }

        private void SetPrivateField(string fieldName, object value) {
            var field = typeof(CameraController).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_controller, value);
        }
    }
    public enum CameraPreset {
        ActionGame,
        RPG,
        Racing,
        Platformer,
        Strategy,
        Dialogue,
        PlayBook
    }
}
