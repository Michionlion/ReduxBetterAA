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

        private PostProcessLayer.Antialiasing _originalResolveMode;
        private PostProcessLayer.Antialiasing _originalSharedMode;
        private DepthTextureMode _originalResolveDepthMode;
        private DepthTextureMode _originalSharedDepthMode;
        private float _originalJitterSpread;
        private float _originalSharpness;
        private float _originalStationaryBlending;
        private float _originalMotionBlending;
        private System.Func<Camera, Vector2, Matrix4x4> _originalJitterFunction;
        private bool _createdTemporalAntialiasing;

        private bool _active;
        private TemporalBackendConfig _config = TemporalBackendConfig.ConservativePpv2;
        private bool _sharedProjectionApplied;
        private int _sharedProjectionAppliedFrame = -1;
        private Matrix4x4 _sharedProjection;
        private Matrix4x4 _sharedNonJitteredProjection;
        private bool _sharedTransparentJitter;

        public string Id => "PPv2 TAA";
        public bool Active => _active;

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

            _originalResolveMode = _resolveLayer.antialiasingMode;
            _originalResolveDepthMode = _resolveCamera.depthTextureMode;
            _originalJitterSpread = taa.jitterSpread;
            _originalSharpness = taa.sharpness;
            _originalStationaryBlending = taa.stationaryBlending;
            _originalMotionBlending = taa.motionBlending;
            _originalJitterFunction = taa.jitteredMatrixFunc;

            if (_sharedJitterLayer != null && _sharedJitterLayer != _resolveLayer)
            {
                _originalSharedMode = _sharedJitterLayer.antialiasingMode;
                _sharedJitterLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
                _sharedJitterLayer.ResetHistory();
            }

            ApplyConfig(in _config);
            _resolveCamera.depthTextureMode |=
                DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            if (_sharedJitterCamera != null &&
                _sharedJitterCamera != _resolveCamera)
            {
                _originalSharedDepthMode = _sharedJitterCamera.depthTextureMode;
                _sharedJitterCamera.depthTextureMode |=
                    DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            }
            _resolveLayer.antialiasingMode =
                PostProcessLayer.Antialiasing.TemporalAntialiasing;
            _resolveLayer.ResetHistory();

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
            RestoreSharedProjection();

            if (_resolveLayer != null)
            {
                TemporalAntialiasing taa = _resolveLayer.temporalAntialiasing;
                if (taa != null)
                {
                    taa.jitterSpread = _originalJitterSpread;
                    taa.sharpness = _originalSharpness;
                    taa.stationaryBlending = _originalStationaryBlending;
                    taa.motionBlending = _originalMotionBlending;
                    taa.jitteredMatrixFunc = _originalJitterFunction;
                }
                _resolveLayer.antialiasingMode = _originalResolveMode;
                _resolveLayer.ResetHistory();
                if (_createdTemporalAntialiasing)
                {
                    _resolveLayer.temporalAntialiasing = null;
                }
            }
            if (_resolveCamera != null)
            {
                _resolveCamera.depthTextureMode = _originalResolveDepthMode;
            }
            if (_sharedJitterLayer != null && _sharedJitterLayer != _resolveLayer)
            {
                _sharedJitterLayer.antialiasingMode = _originalSharedMode;
                _sharedJitterLayer.ResetHistory();
            }
            if (_sharedJitterCamera != null &&
                _sharedJitterCamera != _resolveCamera)
            {
                _sharedJitterCamera.depthTextureMode = _originalSharedDepthMode;
            }

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
            if (!_active || camera == null || camera != _sharedJitterCamera ||
                _resolveLayer == null || _resolveLayer.temporalAntialiasing == null)
            {
                return;
            }
            if (_sharedProjectionApplied)
            {
                if (_sharedProjectionAppliedFrame == Time.frameCount)
                {
                    return;
                }

                // Keep a skipped onPostRender callback from freezing the prior
                // PPv2 jittered projection into a later frame.
                RestoreSharedProjection();
            }

            TemporalAntialiasing taa = _resolveLayer.temporalAntialiasing;
            Vector2 jitter = SharedJitterSequence.GetPpv2Offset(
                taa.sampleIndex,
                taa.jitterSpread
            );

            _sharedProjection = camera.projectionMatrix;
            _sharedNonJitteredProjection = camera.nonJitteredProjectionMatrix;
            _sharedTransparentJitter =
                camera.useJitteredProjectionMatrixForTransparentRendering;
            camera.nonJitteredProjectionMatrix = _sharedProjection;
            camera.projectionMatrix = taa.jitteredMatrixFunc != null
                ? taa.jitteredMatrixFunc(camera, jitter)
                : camera.orthographic
                    ? RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera, jitter)
                    : RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera, jitter);
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
            _sharedProjectionApplied = true;
            _sharedProjectionAppliedFrame = Time.frameCount;
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == _sharedJitterCamera)
            {
                RestoreSharedProjection();
            }
        }

        private void RestoreSharedProjection()
        {
            if (!_sharedProjectionApplied)
            {
                return;
            }
            if (_sharedJitterCamera != null)
            {
                _sharedJitterCamera.projectionMatrix = _sharedProjection;
                _sharedJitterCamera.nonJitteredProjectionMatrix =
                    _sharedNonJitteredProjection;
                _sharedJitterCamera.useJitteredProjectionMatrixForTransparentRendering =
                    _sharedTransparentJitter;
            }
            _sharedProjectionApplied = false;
            _sharedProjectionAppliedFrame = -1;
        }

    }
}
