using System;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    internal readonly struct MotionVectorMatrixSnapshot
    {
        public readonly int Frame;
        public readonly bool Valid;
        public readonly Matrix4x4 UnityNonJitteredViewProjection;
        public readonly Matrix4x4 UnityPreviousViewProjection;
        public readonly Matrix4x4 TrackedCurrentViewProjection;
        public readonly Matrix4x4 TrackedPreviousViewProjection;
        public readonly float UnityCurrentVsTrackedCurrentMaxAbs;
        public readonly float UnityPreviousVsTrackedPreviousMaxAbs;
        public readonly float UnityPreviousVsCurrentMaxAbs;
        public readonly float TrackedPreviousVsCurrentMaxAbs;
        public readonly float FieldOfView;
        public readonly float NearClipPlane;
        public readonly float FarClipPlane;
        public readonly float Aspect;
        public readonly Vector2 CurrentJitterPixels;
        public readonly Vector2 CurrentJitterNormalized;
        public readonly Vector3 CameraPosition;
        public readonly Quaternion CameraRotation;

        public MotionVectorMatrixSnapshot(
            int frame,
            bool valid,
            Matrix4x4 unityNonJitteredViewProjection,
            Matrix4x4 unityPreviousViewProjection,
            Matrix4x4 trackedCurrentViewProjection,
            Matrix4x4 trackedPreviousViewProjection,
            float unityCurrentVsTrackedCurrentMaxAbs,
            float unityPreviousVsTrackedPreviousMaxAbs,
            float unityPreviousVsCurrentMaxAbs,
            float trackedPreviousVsCurrentMaxAbs,
            float fieldOfView,
            float nearClipPlane,
            float farClipPlane,
            float aspect,
            Vector2 currentJitterPixels,
            Vector2 currentJitterNormalized,
            Vector3 cameraPosition,
            Quaternion cameraRotation)
        {
            Frame = frame;
            Valid = valid;
            UnityNonJitteredViewProjection = unityNonJitteredViewProjection;
            UnityPreviousViewProjection = unityPreviousViewProjection;
            TrackedCurrentViewProjection = trackedCurrentViewProjection;
            TrackedPreviousViewProjection = trackedPreviousViewProjection;
            UnityCurrentVsTrackedCurrentMaxAbs =
                unityCurrentVsTrackedCurrentMaxAbs;
            UnityPreviousVsTrackedPreviousMaxAbs =
                unityPreviousVsTrackedPreviousMaxAbs;
            UnityPreviousVsCurrentMaxAbs = unityPreviousVsCurrentMaxAbs;
            TrackedPreviousVsCurrentMaxAbs = trackedPreviousVsCurrentMaxAbs;
            FieldOfView = fieldOfView;
            NearClipPlane = nearClipPlane;
            FarClipPlane = farClipPlane;
            Aspect = aspect;
            CurrentJitterPixels = currentJitterPixels;
            CurrentJitterNormalized = currentJitterNormalized;
            CameraPosition = cameraPosition;
            CameraRotation = cameraRotation;
        }
    }

    /// <summary>
    /// Produces a persistent temporal-backend-safe motion texture. Unity Built-in
    /// motion is explicitly transformed into the requested component convention.
    /// Invalid or implausibly large samples use depth-based camera reprojection.
    /// </summary>
    internal sealed class MotionVectorSanitizer : IDisposable
    {
        internal const bool DefaultEnabled = false;
        internal const float MaximumMotionPixels = 256.0f;
        internal const float MaximumFallbackMotionPixels = 256.0f;
        internal const float MaximumCameraDisagreementPixels = 96.0f;
        internal const int CorruptionSampleCount = 16;
        internal const int CorruptionMinimumSamples = 6;

        private const string ShaderAddress =
            "Assets/ReduxBetterAA/Shaders/MotionVectorSanitizer.shader";

        private static readonly int SourceDimensions =
            Shader.PropertyToID("_SourceDimensions");
        private static readonly int MaximumMotionSquared =
            Shader.PropertyToID("_MaximumMotionSquared");
        private static readonly int MaximumFallbackMotionSquared =
            Shader.PropertyToID("_MaximumFallbackMotionSquared");
        private static readonly int MaximumCameraDisagreementSquared =
            Shader.PropertyToID("_MaximumCameraDisagreementSquared");
        private static readonly int MotionComponentSign =
            Shader.PropertyToID("_MotionComponentSign");
        private static readonly int CurrentJitter =
            Shader.PropertyToID("_CurrentJitter");
        private static readonly int DepthTexture =
            Shader.PropertyToID("_DepthTexture");
        private static readonly int CurrentInverseViewProjection =
            Shader.PropertyToID("_CurrentInverseViewProjection");
        private static readonly int PreviousViewProjection =
            Shader.PropertyToID("_PreviousViewProjection");
        private static readonly int MatrixHistoryValid =
            Shader.PropertyToID("_MatrixHistoryValid");
        private static readonly int FrameCorruptionTexture =
            Shader.PropertyToID("_FrameCorruptionTexture");
        private static readonly int CorruptionMinimumSamplesProperty =
            Shader.PropertyToID("_CorruptionMinimumSamples");
        private static readonly int SanitizationEnabledProperty =
            Shader.PropertyToID("_SanitizationEnabled");
        private static readonly int UnityNonJitteredViewProjection =
            Shader.PropertyToID("_NonJitteredVP");
        private static readonly int UnityPreviousViewProjection =
            Shader.PropertyToID("_PreviousVP");

        private readonly ReduxLogger _logger;
        private readonly Action _availabilityChanged;

        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderHandleValid;
        private Shader _shader;
        private Material _material;
        private RenderTexture _sanitizedMotion;
        private RenderTexture _frameCorruption;
        private int _resourceWidth;
        private int _resourceHeight;
        private int _resourceBytesPerPixel;
        private bool _enabled = DefaultEnabled;
        private bool _disposed;
        private Matrix4x4 _currentViewProjection;
        private Matrix4x4 _currentInverseViewProjection;
        private Matrix4x4 _previousViewProjection;
        private bool _currentMatrixValid;
        private bool _matrixHistoryValid;
        private float _cameraFieldOfView;
        private float _cameraNearClipPlane;
        private float _cameraFarClipPlane;
        private float _cameraAspect;
        private Vector3 _cameraPosition;
        private Quaternion _cameraRotation;
        private Vector2 _currentJitterPixels;
        private Vector2 _currentJitterNormalized;
        private MotionVectorMatrixSnapshot _matrixSnapshot;
        private string _status = "Motion-vector sanitizer is loading.";

        public MotionVectorSanitizer(
            ReduxLogger logger,
            Action availabilityChanged)
        {
            _logger = logger;
            _availabilityChanged = availabilityChanged;
        }

        public bool Ready => _shader != null && _shader.isSupported;
        public bool Enabled => _enabled;
        public string Status => _enabled
            ? _status
            : "Bypassed: raw motion is passed through with only the configured " +
              "component-sign conversion.";
        public Texture SanitizedTexture => _sanitizedMotion;
        public Texture CorruptionTexture => _frameCorruption;
        public MotionVectorMatrixSnapshot MatrixSnapshot => _matrixSnapshot;
        public long EstimatedMemoryBytes => _sanitizedMotion == null
            ? 0L
            : (long)_sanitizedMotion.width * _sanitizedMotion.height *
              _resourceBytesPerPixel + (_frameCorruption == null ? 0L : 2L);

        public void Initialize()
        {
            if (_shaderHandleValid || _disposed)
            {
                return;
            }
            _shaderHandle = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
            _shaderHandleValid = true;
            _shaderHandle.Completed += OnShaderLoaded;
        }

        public bool SetEnabled(bool enabled)
        {
            if (_disposed || _enabled == enabled)
            {
                return false;
            }
            _enabled = enabled;
            ResetCameraHistory();
            _logger.LogInfo(
                "[ReduxBetterAA/Motion] Motion rejection and camera fallback " +
                (enabled ? "enabled." : "bypassed; component signs remain active.")
            );
            return true;
        }

        public void CaptureCamera(
            Camera camera,
            Matrix4x4 nonJitteredProjection)
        {
            if (_disposed || camera == null)
            {
                _currentMatrixValid = false;
                return;
            }

            _currentViewProjection = GL.GetGPUProjectionMatrix(
                nonJitteredProjection,
                camera.targetTexture != null
            ) * camera.worldToCameraMatrix;
            _currentInverseViewProjection = _currentViewProjection.inverse;
            _currentMatrixValid = MatrixIsFinite(_currentViewProjection) &&
                MatrixIsFinite(_currentInverseViewProjection);
            _cameraFieldOfView = camera.fieldOfView;
            _cameraNearClipPlane = camera.nearClipPlane;
            _cameraFarClipPlane = camera.farClipPlane;
            _cameraAspect = camera.aspect;
            _cameraPosition = camera.transform.position;
            _cameraRotation = camera.transform.rotation;
        }

        public bool TrySanitize(
            Texture source,
            Texture depth,
            int width,
            int height,
            Vector2 jitterPixels,
            bool invertX,
            bool invertY,
            out Texture sanitized)
        {
            sanitized = null;
            if (_disposed || !Ready || source == null || width <= 0 || height <= 0)
            {
                return false;
            }
            if (!EnsureResources(width, height))
            {
                return false;
            }

            _material.SetVector(
                SourceDimensions,
                new Vector4(width, height, 1.0f / width, 1.0f / height)
            );
            _currentJitterPixels = jitterPixels;
            _currentJitterNormalized = new Vector2(
                jitterPixels.x / width,
                jitterPixels.y / height
            );
            _material.SetVector(CurrentJitter, _currentJitterNormalized);
            _material.SetFloat(
                MaximumMotionSquared,
                MaximumMotionPixels * MaximumMotionPixels
            );
            _material.SetFloat(
                MaximumFallbackMotionSquared,
                MaximumFallbackMotionPixels * MaximumFallbackMotionPixels
            );
            _material.SetFloat(
                MaximumCameraDisagreementSquared,
                MaximumCameraDisagreementPixels *
                MaximumCameraDisagreementPixels
            );
            _material.SetVector(
                MotionComponentSign,
                new Vector4(
                    invertX ? -1.0f : 1.0f,
                    invertY ? -1.0f : 1.0f,
                    0.0f,
                    0.0f
                )
            );
            _material.SetTexture(DepthTexture, depth);
            _material.SetMatrix(
                CurrentInverseViewProjection,
                _currentInverseViewProjection
            );
            _material.SetMatrix(PreviousViewProjection, _previousViewProjection);
            _material.SetFloat(
                MatrixHistoryValid,
                _currentMatrixValid && _matrixHistoryValid ? 1.0f : 0.0f
            );
            _material.SetFloat(
                CorruptionMinimumSamplesProperty,
                CorruptionMinimumSamples
            );
            _material.SetFloat(
                SanitizationEnabledProperty,
                _enabled ? 1.0f : 0.0f
            );
            CaptureMatrixSnapshot();
            if (_enabled)
            {
                Graphics.Blit(source, _frameCorruption, _material, 1);
            }
            else
            {
                Graphics.Blit(Texture2D.blackTexture, _frameCorruption);
            }
            _material.SetTexture(FrameCorruptionTexture, _frameCorruption);
            Graphics.Blit(source, _sanitizedMotion, _material, 0);
            if (_currentMatrixValid)
            {
                _previousViewProjection = _currentViewProjection;
                _matrixHistoryValid = true;
            }
            sanitized = _sanitizedMotion;
            return true;
        }

        public void ResetCameraHistory()
        {
            _currentMatrixValid = false;
            _matrixHistoryValid = false;
        }

        public void ReleaseResources()
        {
            if (_sanitizedMotion != null)
            {
                _sanitizedMotion.Release();
                UnityEngine.Object.Destroy(_sanitizedMotion);
                _sanitizedMotion = null;
            }
            if (_frameCorruption != null)
            {
                _frameCorruption.Release();
                UnityEngine.Object.Destroy(_frameCorruption);
                _frameCorruption = null;
            }
            _resourceWidth = 0;
            _resourceHeight = 0;
            _resourceBytesPerPixel = 0;
            _matrixSnapshot = default(MotionVectorMatrixSnapshot);
            ResetCameraHistory();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReleaseResources();
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
                _material = null;
            }
            if (_shaderHandleValid)
            {
                _shaderHandle.Completed -= OnShaderLoaded;
                Addressables.Release(_shaderHandle);
                _shaderHandleValid = false;
            }
            _shader = null;
        }

        internal static bool IsMotionUsable(
            Vector2 normalizedMotion,
            int width,
            int height,
            float maximumMotionPixels = MaximumMotionPixels)
        {
            if (float.IsNaN(normalizedMotion.x) ||
                float.IsInfinity(normalizedMotion.x) ||
                float.IsNaN(normalizedMotion.y) ||
                float.IsInfinity(normalizedMotion.y) ||
                width <= 0 || height <= 0)
            {
                return false;
            }

            float x = normalizedMotion.x * width;
            float y = normalizedMotion.y * height;
            return x * x + y * y <= maximumMotionPixels * maximumMotionPixels;
        }

        internal static bool DisagreesWithCameraMotion(
            Vector2 normalizedMotion,
            Vector2 normalizedCameraMotion,
            int width,
            int height,
            float maximumDisagreementPixels = MaximumCameraDisagreementPixels)
        {
            if (width <= 0 || height <= 0 ||
                !VectorIsFinite(normalizedMotion) ||
                !VectorIsFinite(normalizedCameraMotion))
            {
                return true;
            }

            float x = (normalizedMotion.x - normalizedCameraMotion.x) * width;
            float y = (normalizedMotion.y - normalizedCameraMotion.y) * height;
            return x * x + y * y >
                maximumDisagreementPixels * maximumDisagreementPixels;
        }

        private bool EnsureResources(int width, int height)
        {
            if (_sanitizedMotion != null &&
                _resourceWidth == width &&
                _resourceHeight == height)
            {
                return true;
            }

            ReleaseResources();
            RenderTextureFormat format;
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf))
            {
                format = RenderTextureFormat.RGHalf;
                _resourceBytesPerPixel = 4;
            }
            else if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat))
            {
                format = RenderTextureFormat.RGFloat;
                _resourceBytesPerPixel = 8;
            }
            else
            {
                _status = "No supported two-channel floating-point motion texture format.";
                return false;
            }

            _sanitizedMotion = new RenderTexture(
                width,
                height,
                0,
                format,
                RenderTextureReadWrite.Linear
            )
            {
                name = "Redux Better AA Sanitized Motion Vectors",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = false,
                autoGenerateMips = false
            };
            _sanitizedMotion.Create();
            if (!_sanitizedMotion.IsCreated())
            {
                ReleaseResources();
                _status = "Sanitized motion-vector texture creation failed.";
                return false;
            }

            RenderTextureFormat corruptionFormat =
                SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf)
                    ? RenderTextureFormat.RHalf
                    : RenderTextureFormat.RFloat;
            _frameCorruption = new RenderTexture(
                1,
                1,
                0,
                corruptionFormat,
                RenderTextureReadWrite.Linear
            )
            {
                name = "Redux Better AA Motion Corruption Flag",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = false,
                autoGenerateMips = false
            };
            _frameCorruption.Create();
            if (!_frameCorruption.IsCreated())
            {
                ReleaseResources();
                _status = "Motion corruption flag texture creation failed.";
                return false;
            }

            _resourceWidth = width;
            _resourceHeight = height;
            _status = "Active: a 16-point same-frame detector replaces a " +
                "screen-wide corrupt field; coherent camera motion may exceed " +
                "256 px/frame, while invalid, unverified >256 px, or >96 px " +
                "camera disagreement uses a <=256 px fallback, otherwise zero.";
            _logger.LogInfo(
                "[ReduxBetterAA/Motion] Vendor motion sanitizer created for " +
                width + "x" + height + "; cutoff is " +
                MaximumMotionPixels + " px/frame; whole-frame threshold is " +
                CorruptionMinimumSamples + "/" + CorruptionSampleCount + "."
            );
            return true;
        }

        private void CaptureMatrixSnapshot()
        {
            Matrix4x4 unityCurrent = Shader.GetGlobalMatrix(
                UnityNonJitteredViewProjection
            );
            Matrix4x4 unityPrevious = Shader.GetGlobalMatrix(
                UnityPreviousViewProjection
            );
            bool valid = _currentMatrixValid && _matrixHistoryValid &&
                MatrixIsFinite(unityCurrent) && MatrixIsFinite(unityPrevious) &&
                !MatrixIsZero(unityCurrent) && !MatrixIsZero(unityPrevious) &&
                !MatrixIsIdentity(unityCurrent) &&
                !MatrixIsIdentity(unityPrevious);
            _matrixSnapshot = new MotionVectorMatrixSnapshot(
                Time.frameCount,
                valid,
                unityCurrent,
                unityPrevious,
                _currentViewProjection,
                _previousViewProjection,
                MatrixDifferenceMaxAbs(unityCurrent, _currentViewProjection),
                MatrixDifferenceMaxAbs(unityPrevious, _previousViewProjection),
                MatrixDifferenceMaxAbs(unityPrevious, unityCurrent),
                MatrixDifferenceMaxAbs(
                    _previousViewProjection,
                    _currentViewProjection
                ),
                _cameraFieldOfView,
                _cameraNearClipPlane,
                _cameraFarClipPlane,
                _cameraAspect,
                _currentJitterPixels,
                _currentJitterNormalized,
                _cameraPosition,
                _cameraRotation
            );
        }

        private static float MatrixDifferenceMaxAbs(
            Matrix4x4 left,
            Matrix4x4 right)
        {
            float maximum = 0.0f;
            for (int index = 0; index < 16; index++)
            {
                maximum = Mathf.Max(maximum, Mathf.Abs(left[index] - right[index]));
            }
            return maximum;
        }

        private static bool MatrixIsZero(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; index++)
            {
                if (matrix[index] != 0.0f)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatrixIsIdentity(Matrix4x4 matrix)
        {
            const float tolerance = 0.000001f;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    float expected = row == column ? 1.0f : 0.0f;
                    if (Mathf.Abs(matrix[row, column] - expected) > tolerance)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private void OnShaderLoaded(AsyncOperationHandle<Shader> operation)
        {
            if (_disposed)
            {
                return;
            }
            if (operation.Status == AsyncOperationStatus.Succeeded &&
                operation.Result != null && operation.Result.isSupported)
            {
                _shader = operation.Result;
                _material = new Material(_shader)
                {
                    name = "Redux Better AA Motion Sanitizer Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _status = "Ready: Unity-to-vendor component signs and camera " +
                    "fallback will be applied to vendor motion.";
                _logger.LogInfo(
                    "[ReduxBetterAA/Motion] Temporal motion-vector sanitizer loaded."
                );
            }
            else
            {
                _status = "Motion-vector sanitizer shader is unavailable or unsupported.";
                _logger.LogError(
                    "[ReduxBetterAA/Motion] Motion-vector sanitizer shader failed to load."
                );
            }
            _availabilityChanged?.Invoke();
        }

        private static bool MatrixIsFinite(Matrix4x4 matrix)
        {
            for (int index = 0; index < 16; index++)
            {
                float value = matrix[index];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool VectorIsFinite(Vector2 vector)
        {
            return !float.IsNaN(vector.x) && !float.IsInfinity(vector.x) &&
                   !float.IsNaN(vector.y) && !float.IsInfinity(vector.y);
        }
    }
}
