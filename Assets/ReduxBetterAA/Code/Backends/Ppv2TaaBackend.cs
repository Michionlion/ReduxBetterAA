using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Backends
{
    internal sealed class Ppv2TaaBackend : ITemporalBackend
    {
        private Camera _resolveCamera;
        private PostProcessLayer _resolveLayer;
        private Camera _sharedJitterCamera;
        private PostProcessLayer _sharedJitterLayer;

        private float _originalJitterSpread;
        private float _originalSharpness;
        private float _originalStationaryBlending;
        private float _originalMotionBlending;
        private System.Func<Camera, Vector2, Matrix4x4> _originalJitterFunction;
        private bool _createdTemporalAntialiasing;

        private bool _active;
        private TemporalBackendConfig _config = TemporalBackendConfig.ConservativePpv2;
        private CameraProjectionState _sharedProjectionState;
        private TemporalAntialiasing _ownedTaa;
        private SceneCameraState _resolveState, _sharedState;

        public string Id => "PPv2 TAA";
        internal bool RenderEnabled = true;
        public bool Active => _active && RenderEnabled;

        public bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason)
        {
            if (cameras == null || cameras.SceneKind == TemporalSceneKind.Unsupported)
            {
                unsupportedReason = "the active game state has no supported scene output";
                return false;
            }
            if (cameras.ResolveCamera == null || cameras.ResolveLayer == null)
            {
                unsupportedReason = "the final scene camera or PostProcessLayer is unavailable";
                return false;
            }
            if (!cameras.ProjectionJitterSupported)
            {
                unsupportedReason =
                    "the active scene output does not render coherently with PPv2 projection jitter";
                return false;
            }
            if (!cameras.ResolveCamera.isActiveAndEnabled || !cameras.ResolveLayer.enabled)
            {
                unsupportedReason = "the final scene camera or PostProcessLayer is disabled";
                return false;
            }

            TemporalAntialiasing taa = cameras.ResolveLayer.temporalAntialiasing;
            if (taa == null)
            {
                taa = new TemporalAntialiasing();
            }
            if (!taa.IsSupported())
            {
                unsupportedReason = "PPv2 TAA reports unsupported render-target or motion-vector capabilities";
                return false;
            }

            unsupportedReason = string.Empty;
            return true;
        }

        public bool Configure(TemporalCameraSet cameras, out string failureReason)
        {
            Deactivate();
            if (!ProbeSupport(cameras, out failureReason))
            {
                return false;
            }

            _resolveCamera = cameras.ResolveCamera;
            _resolveLayer = cameras.ResolveLayer;
            _sharedJitterCamera = cameras.SharedJitterCamera;
            _sharedJitterLayer = cameras.SharedJitterLayer;

            TemporalAntialiasing taa = _resolveLayer.temporalAntialiasing;
            if (taa == null)
            {
                _createdTemporalAntialiasing = true;
                taa = new TemporalAntialiasing();
                _resolveLayer.temporalAntialiasing = taa;
            }

            _ownedTaa = taa;
            _originalJitterSpread = taa.jitterSpread;
            _originalSharpness = taa.sharpness;
            _originalStationaryBlending = taa.stationaryBlending;
            _originalMotionBlending = taa.motionBlending;
            _originalJitterFunction = taa.jitteredMatrixFunc;
            _resolveState.Capture(_resolveCamera, _resolveLayer, PostProcessLayer.Antialiasing.TemporalAntialiasing);
            if (_sharedJitterCamera != _resolveCamera)
                _sharedState.Capture(_sharedJitterCamera, _sharedJitterLayer);

            ApplyConfig(in _config);
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
            _active = true;
            return true;
        }

        public void ApplyConfig(in TemporalBackendConfig config)
        {
            _config = config;
            if (_resolveLayer == null || _resolveLayer.temporalAntialiasing == null)
            {
                return;
            }

            TemporalAntialiasing taa = _resolveLayer.temporalAntialiasing;
            taa.jitterSpread = config.JitterSpread;
            taa.sharpness = config.Sharpness;
            taa.stationaryBlending = config.StationaryBlending;
            taa.motionBlending = config.MotionBlending;
        }

        public void Tick(uint frameIndex)
        {
        }

        public void ResetHistory(HistoryResetReason reason)
        {
            if (_active && _resolveLayer != null)
            {
                _resolveLayer.ResetHistory();
            }
        }

        public void Deactivate()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            _sharedProjectionState.Restore();
            if (_resolveLayer != null && ReferenceEquals(_resolveLayer.temporalAntialiasing, _ownedTaa) && _ownedTaa != null)
            {
                var taa = _ownedTaa;
                bool untouched = taa.jitterSpread == _config.JitterSpread && taa.sharpness == _config.Sharpness &&
                    taa.stationaryBlending == _config.StationaryBlending && taa.motionBlending == _config.MotionBlending &&
                    taa.jitteredMatrixFunc == _originalJitterFunction;
                if (taa.jitterSpread == _config.JitterSpread) taa.jitterSpread = _originalJitterSpread;
                if (taa.sharpness == _config.Sharpness) taa.sharpness = _originalSharpness;
                if (taa.stationaryBlending == _config.StationaryBlending) taa.stationaryBlending = _originalStationaryBlending;
                if (taa.motionBlending == _config.MotionBlending) taa.motionBlending = _originalMotionBlending;
                if (_createdTemporalAntialiasing && untouched)
                {
                    typeof(TemporalAntialiasing).GetMethod("Release", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)?.Invoke(taa, null);
                    _resolveLayer.temporalAntialiasing = null;
                }
            }
            _sharedState.Restore();
            _resolveState.Restore();
            _ownedTaa = null;

            _resolveCamera = null;
            _resolveLayer = null;
            _sharedJitterCamera = null;
            _sharedJitterLayer = null;
            _createdTemporalAntialiasing = false;
            _active = false;
        }

        public void Dispose()
        {
            Deactivate();
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (!Active || camera == null || camera != _sharedJitterCamera ||
                _resolveLayer == null || _resolveLayer.temporalAntialiasing == null)
            {
                return;
            }
            TemporalAntialiasing taa = _resolveLayer.temporalAntialiasing;
            Vector2 jitter = SharedJitterSequence.GetPpv2Offset(
                taa.sampleIndex,
                taa.jitterSpread
            );

            _sharedProjectionState.Apply(camera, jitter, taa.jitteredMatrixFunc);
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == _sharedJitterCamera) _sharedProjectionState.Restore();
        }
    }
}
