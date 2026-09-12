using System;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.ResourceManagement.AsyncOperations;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Backends
{
    internal sealed class CustomTaaBackend : ITemporalBackend, ISceneResolve, IProjectionJitterSource
    {
        private const string ShaderAddress =
            "Assets/ReduxBetterAA/Shaders/CustomTaa.shader";
        private const int ResolvePass = 0;
        private const int CopyDepthPass = 1;
        private const int SharpenPass = 2;
        private const int DebugPass = 3;

        private static readonly int HistoryTexture = Shader.PropertyToID("_HistoryTex");
        private static readonly int HistoryDepthTexture =
            Shader.PropertyToID("_HistoryDepthTex");
        private static readonly int CameraDepthTexture =
            Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraMotionVectorsTexture =
            Shader.PropertyToID("_CameraMotionVectorsTexture");
        private static readonly int SanitizedMotionTexture =
            Shader.PropertyToID("_ReduxBetterAAMotionVectors");
        private static readonly int SourceDimensions =
            Shader.PropertyToID("_SourceDimensions");
        private static readonly int Jitter = Shader.PropertyToID("_Jitter");
        private static readonly int StationaryHistory =
            Shader.PropertyToID("_StationaryHistory");
        private static readonly int MovingHistory = Shader.PropertyToID("_MovingHistory");
        private static readonly int MotionResponsePixels =
            Shader.PropertyToID("_MotionResponsePixels");
        private static readonly int MaximumMotionPixels =
            Shader.PropertyToID("_MaximumMotionPixels");
        private static readonly int DepthThreshold = Shader.PropertyToID("_DepthThreshold");
        private static readonly int DepthEdgeStability =
            Shader.PropertyToID("_DepthEdgeStability");
        private static readonly int VarianceGamma = Shader.PropertyToID("_VarianceGamma");
        private static readonly int ReactiveScale = Shader.PropertyToID("_ReactiveScale");
        private static readonly int Sharpening = Shader.PropertyToID("_Sharpening");
        private static readonly int NoDepthHistory = Shader.PropertyToID("_NoDepthHistory");
        private static readonly int HistoryValid = Shader.PropertyToID("_HistoryValid");
        private static readonly int DebugMode = Shader.PropertyToID("_DebugMode");
        private static readonly int CurrentInverseViewProjection =
            Shader.PropertyToID("_CurrentInverseViewProjection");
        private static readonly int PreviousViewProjection =
            Shader.PropertyToID("_PreviousViewProjection");
        private static readonly int MatrixHistoryValid =
            Shader.PropertyToID("_MatrixHistoryValid");

        private readonly ReduxLogger _logger;
        private readonly Action _availabilityChanged;
        private readonly Action<string> _runtimeFailure;
        private string _lastFailure;
        private readonly BackendPerformanceProfiler _performanceProfiler;
        private readonly MotionVectorSanitizer _motionVectorSanitizer;

        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderHandleValid;
        private Shader _shader;
        private Material _material;
        private Camera _resolveCamera;
        private PostProcessLayer _resolveLayer;
        private Camera _sharedJitterCamera;
        private PostProcessLayer _sharedJitterLayer;
        private TemporalRenderHook _hook;
        private SceneCameraState _resolveState;
        private SceneCameraState _sharedState;

        private RenderTexture _historyColorA;
        private RenderTexture _historyColorB;
        private RenderTexture _historyDepthA;
        private RenderTexture _historyDepthB;
        private bool _historyReadA = true;
        private bool _historyValid;
        private int _resourceWidth;
        private int _resourceHeight;
        private RenderTextureFormat _resourceFormat;
        private bool _resourceSrgb;
        private bool _resourceCreationFailed;
        private long _estimatedMemoryBytes;

        private CustomTaaConfig _config = CustomTaaConfig.Conservative;
        private uint _frameIndex;
        private Vector2 _jitterNormalized;
        private Matrix4x4 _currentViewProjection;
        private Matrix4x4 _currentInverseViewProjection;
        private Matrix4x4 _previousViewProjection;
        private bool _currentMatrixValid;
        private bool _matrixHistoryValid;
        private bool _projectionJitterSupported;
        private bool _jitterTransparentRendering;
        private CameraProjectionState _resolveProjection;
        private CameraProjectionState _sharedProjection;
        private bool _active;
        private bool _disposed;

        public CustomTaaBackend(
            ReduxLogger logger,
            Action availabilityChanged,
            BackendPerformanceProfiler performanceProfiler,
            MotionVectorSanitizer motionVectorSanitizer,
            Action<string> runtimeFailure)
        {
            _logger = logger;
            _availabilityChanged = availabilityChanged;
            _runtimeFailure = runtimeFailure;
            _performanceProfiler = performanceProfiler;
            _motionVectorSanitizer = motionVectorSanitizer;
        }

        public string Id => "Custom TAA";
        internal bool RenderEnabled = true;
        public bool Active => _active && RenderEnabled;
        public bool ShaderReady => _shader != null && _shader.isSupported;
        public long EstimatedMemoryBytes => _estimatedMemoryBytes;
        public Vector2 CurrentJitterNormalized => _jitterNormalized;

        public bool TryGetRasterProjection(Camera camera, out Matrix4x4 projection)
        {
            projection = default;
            if (!Active || !_projectionJitterSupported || camera == null ||
                (camera != _resolveCamera && camera != _sharedJitterCamera)) return false;
            CameraProjectionState state = camera == _sharedJitterCamera ? _sharedProjection : _resolveProjection;
            projection = state.GetRasterProjection(camera, SharedJitterSequence.GetCustomOffset(
                _frameIndex, _config.JitterSpread, _config.SequenceLength));
            return true;
        }

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

        public void ApplyConfig(in CustomTaaConfig config)
        {
            _config = config;
            ApplyMaterialConfig();
        }

        public bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason)
        {
            if (_lastFailure != null)
            {
                unsupportedReason = _lastFailure;
                return false;
            }
            if (!ShaderReady)
            {
                unsupportedReason = _shaderHandleValid && !_shaderHandle.IsDone
                    ? "the custom TAA shader is still loading"
                    : "the custom TAA shader is unavailable or unsupported";
                return false;
            }
            if (cameras == null || cameras.SceneKind == TemporalSceneKind.Unsupported)
            {
                unsupportedReason = "the active game state has no supported scene output";
                return false;
            }
            if (cameras.ResolveCamera == null)
            {
                unsupportedReason = "the final scene camera is unavailable";
                return false;
            }
            if (!cameras.ResolveCamera.isActiveAndEnabled)
            {
                unsupportedReason = "the final scene camera is disabled";
                return false;
            }
            if (!SystemInfo.supportsMotionVectors)
            {
                unsupportedReason = "Unity reports no motion-vector support";
                return false;
            }
            if (_motionVectorSanitizer == null || !_motionVectorSanitizer.Ready)
            {
                unsupportedReason = "the shared motion-vector sanitizer is unavailable";
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
            _projectionJitterSupported = cameras.ProjectionJitterSupported;
            _jitterTransparentRendering = cameras.JitterTransparentRendering;
            _resolveState.Capture(_resolveCamera, _resolveLayer);
            _sharedState.Capture(
                _sharedJitterCamera != _resolveCamera ? _sharedJitterCamera : null,
                _sharedJitterLayer != _resolveLayer ? _sharedJitterLayer : null);

            if (_material == null)
            {
                _material = new Material(_shader)
                {
                    name = "Redux Better AA Custom TAA Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            ApplyMaterialConfig();

            _hook = TemporalRenderHook.Attach(_resolveCamera, this);

            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
            _historyValid = false;
            _active = true;
            return true;
        }

        public void Tick(uint frameIndex)
        {
            _frameIndex = frameIndex;
        }

        public void ResetHistory(HistoryResetReason reason)
        {
            _historyValid = false;
            _matrixHistoryValid = false;
            _motionVectorSanitizer.ResetCameraHistory();
        }

        public void Render(RenderTexture source, RenderTexture destination)
        {
            long start = _performanceProfiler.BeginResolve(
                BackendSelection.CustomTaa
            );
            try
            {
                RenderCore(source, destination);
            }
            catch (Exception exception)
            {
                FailRuntime("Custom TAA execution failed: " + exception.GetType().Name);
                Graphics.Blit(source, destination);
            }
            finally
            {
                _performanceProfiler.EndResolve(
                    BackendSelection.CustomTaa,
                    start
                );
            }
        }

        private void RenderCore(RenderTexture source, RenderTexture destination)
        {
            if (!_active || _material == null || source == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            EnsureResources(source);
            if (_historyColorA == null)
            {
                Graphics.Blit(source, destination);
                FailRuntime("Custom TAA render targets could not be created");
                return;
            }

            RenderTexture historyRead = _historyReadA ? _historyColorA : _historyColorB;
            RenderTexture historyWrite = _historyReadA ? _historyColorB : _historyColorA;
            RenderTexture depthRead = _historyReadA ? _historyDepthA : _historyDepthB;
            RenderTexture depthWrite = _historyReadA ? _historyDepthB : _historyDepthA;

            Texture depth = Shader.GetGlobalTexture(CameraDepthTexture);
            Texture rawMotion = Shader.GetGlobalTexture(CameraMotionVectorsTexture);
            Texture sanitizedMotion;
            if (!TemporalTextures.Matches(depth, source.width, source.height) ||
                !TemporalTextures.Matches(rawMotion, source.width, source.height) ||
                !_motionVectorSanitizer.TrySanitize(
                    rawMotion,
                    depth,
                    source.width,
                    source.height,
                    new Vector2(
                        _jitterNormalized.x * source.width,
                        _jitterNormalized.y * source.height
                    ),
                    false,
                    false,
                    out sanitizedMotion))
            {
                Graphics.Blit(source, destination);
                _historyValid = false;
                FailRuntime("Custom TAA depth/motion input or sanitization is unavailable");
                return;
            }

            _material.SetTexture(HistoryTexture, historyRead);
            _material.SetTexture(HistoryDepthTexture, depthRead);
            _material.SetTexture(SanitizedMotionTexture, sanitizedMotion);
            _material.SetVector(
                SourceDimensions,
                new Vector4(
                    source.width,
                    source.height,
                    1.0f / source.width,
                    1.0f / source.height
                )
            );
            _material.SetVector(Jitter, _jitterNormalized);
            _material.SetFloat(HistoryValid, _historyValid ? 1.0f : 0.0f);
            _material.SetMatrix(
                CurrentInverseViewProjection,
                _currentInverseViewProjection
            );
            _material.SetMatrix(PreviousViewProjection, _previousViewProjection);
            _material.SetFloat(
                MatrixHistoryValid,
                _currentMatrixValid && _matrixHistoryValid ? 1.0f : 0.0f
            );

            // Read and write histories are distinct. Resolve directly to the next
            // history, then sharpen only the presented output, never the history.
            Graphics.Blit(source, historyWrite, _material, ResolvePass);
            Graphics.Blit(source, depthWrite, _material, CopyDepthPass);

            if (_config.DebugView != CustomTaaDebugView.FinalResolve)
            {
                _material.SetFloat(DebugMode, (float)_config.DebugView);
                Graphics.Blit(source, destination, _material, DebugPass);
            }
            else if (_config.Sharpening > 0.0001f)
            {
                Graphics.Blit(historyWrite, destination, _material, SharpenPass);
            }
            else
            {
                Graphics.Blit(historyWrite, destination);
            }

            _historyReadA = !_historyReadA;
            _historyValid = true;
            if (_currentMatrixValid)
            {
                _previousViewProjection = _currentViewProjection;
                _matrixHistoryValid = true;
            }
        }

        public void Deactivate()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            _resolveProjection.Restore();
            _sharedProjection.Restore();

            TemporalRenderHook.Detach(ref _hook);
            _sharedState.Restore();
            _resolveState.Restore();

            ReleaseResources();
            _resolveCamera = null;
            _resolveLayer = null;
            _sharedJitterCamera = null;
            _sharedJitterLayer = null;
            _projectionJitterSupported = false;
            _jitterTransparentRendering = false;
            _jitterNormalized = Vector2.zero;
            _historyValid = false;
            _currentMatrixValid = false;
            _matrixHistoryValid = false;
            _motionVectorSanitizer.ResetCameraHistory();
            _active = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Deactivate();
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

        private void ApplyMaterialConfig()
        {
            if (_material == null)
            {
                return;
            }
            _material.SetFloat(StationaryHistory, _config.StationaryHistory);
            _material.SetFloat(MovingHistory, _config.MovingHistory);
            _material.SetFloat(MotionResponsePixels, _config.MotionResponsePixels);
            _material.SetFloat(MaximumMotionPixels, _config.MaximumMotionPixels);
            _material.SetFloat(DepthThreshold, _config.DepthThreshold);
            _material.SetFloat(DepthEdgeStability, _config.DepthEdgeStability);
            _material.SetFloat(VarianceGamma, _config.VarianceGamma);
            _material.SetFloat(ReactiveScale, _config.ReactiveScale);
            _material.SetFloat(Sharpening, _config.Sharpening);
            _material.SetFloat(NoDepthHistory, _config.NoDepthHistory);
            _material.SetFloat(DebugMode, (float)_config.DebugView);
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (!Active || camera == null)
            {
                return;
            }
            if (_projectionJitterSupported && camera == _sharedJitterCamera)
            {
                ApplyJitter(camera, ref _sharedProjection);
            }
            if (camera == _resolveCamera)
            {
                if (_projectionJitterSupported &&
                    camera != _sharedJitterCamera)
                {
                    ApplyJitter(camera, ref _resolveProjection);
                }
                int width = Math.Max(1, camera.targetTexture == null
                    ? camera.pixelWidth
                    : camera.targetTexture.width);
                int height = Math.Max(1, camera.targetTexture == null
                    ? camera.pixelHeight
                    : camera.targetTexture.height);
                Vector2 jitterPixels = _projectionJitterSupported
                    ? SharedJitterSequence.GetCustomOffset(
                        _frameIndex,
                        _config.JitterSpread,
                        _config.SequenceLength
                    )
                    : Vector2.zero;
                _jitterNormalized = new Vector2(
                    jitterPixels.x / width,
                    jitterPixels.y / height
                );
                CameraProjectionState projectionState = camera == _sharedJitterCamera
                    ? _sharedProjection
                    : _resolveProjection;
                Matrix4x4 nonJitteredProjection = _projectionJitterSupported
                    ? projectionState.Projection
                    : camera.projectionMatrix;
                _currentViewProjection = GL.GetGPUProjectionMatrix(
                    nonJitteredProjection,
                    camera.targetTexture != null
                ) * camera.worldToCameraMatrix;
                _currentInverseViewProjection = _currentViewProjection.inverse;
                _currentMatrixValid = MatrixIsFinite(_currentViewProjection) &&
                    MatrixIsFinite(_currentInverseViewProjection);
                _motionVectorSanitizer.CaptureCamera(
                    camera,
                    nonJitteredProjection
                );
            }
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == _resolveCamera)
            {
                _resolveProjection.Restore();
            }
            if (camera == _sharedJitterCamera)
            {
                _sharedProjection.Restore();
            }
        }

        private void ApplyJitter(Camera camera, ref CameraProjectionState state)
        {
            state.Apply(camera, SharedJitterSequence.GetCustomOffset(
                _frameIndex, _config.JitterSpread, _config.SequenceLength),
                jitterTransparentRendering: _jitterTransparentRendering);
        }

        private void EnsureResources(RenderTexture source)
        {
            if (((TemporalTextures.IsCreated(_historyColorA) &&
                  TemporalTextures.IsCreated(_historyColorB) &&
                  TemporalTextures.IsCreated(_historyDepthA) &&
                  TemporalTextures.IsCreated(_historyDepthB)) || _resourceCreationFailed) &&
                _resourceWidth == source.width &&
                _resourceHeight == source.height &&
                _resourceFormat == source.format &&
                _resourceSrgb == source.sRGB)
            {
                return;
            }

            ReleaseResources();
            RenderTextureDescriptor colorDescriptor = source.descriptor;
            colorDescriptor.depthBufferBits = 0;
            colorDescriptor.msaaSamples = 1;
            colorDescriptor.bindMS = false;
            colorDescriptor.enableRandomWrite = false;
            colorDescriptor.useMipMap = false;
            colorDescriptor.autoGenerateMips = false;
            colorDescriptor.useDynamicScale = false;
            colorDescriptor.memoryless = UnityEngine.RenderTextureMemoryless.None;

            _historyColorA = CreateTexture(colorDescriptor, "Custom TAA History Color A");
            _historyColorB = CreateTexture(colorDescriptor, "Custom TAA History Color B");

            RenderTextureFormat depthFormat = SystemInfo.SupportsRenderTextureFormat(
                RenderTextureFormat.RFloat
            )
                ? RenderTextureFormat.RFloat
                : RenderTextureFormat.RHalf;
            _historyDepthA = CreateDepthTexture(
                source.width,
                source.height,
                depthFormat,
                "Custom TAA History Depth A"
            );
            _historyDepthB = CreateDepthTexture(
                source.width,
                source.height,
                depthFormat,
                "Custom TAA History Depth B"
            );

            _resourceWidth = source.width;
            _resourceHeight = source.height;
            _resourceFormat = source.format;
            _resourceSrgb = source.sRGB;
            if (_historyColorA == null || _historyColorB == null ||
                _historyDepthA == null ||
                _historyDepthB == null)
            {
                TemporalTextures.Release(ref _historyColorA);
                TemporalTextures.Release(ref _historyColorB);
                TemporalTextures.Release(ref _historyDepthA);
                TemporalTextures.Release(ref _historyDepthB);
                _resourceCreationFailed = true;
                _logger.LogError(
                    "[ReduxBetterAA/Resources] Custom TAA resource creation failed; " +
                    "the frame will pass through and the backend will fall back to Off."
                );
                return;
            }

            int colorBytes = EstimateColorBytes(source.format);
            int depthBytes = depthFormat == RenderTextureFormat.RFloat ? 4 : 2;
            _estimatedMemoryBytes = (long)source.width * source.height *
                (colorBytes * 2 + depthBytes * 2);
            _historyReadA = true;
            _historyValid = false;
            _logger.LogInfo(
                "[ReduxBetterAA/Resources] Custom TAA resources created: " +
                source.width + "x" + source.height + ", approximately " +
                (_estimatedMemoryBytes / (1024L * 1024L)) + " MiB."
            );
        }

        private static RenderTexture CreateTexture(
            RenderTextureDescriptor descriptor,
            string name)
        {
            var texture = new RenderTexture(descriptor)
            {
                name = "Redux Better AA " + name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            if (texture.IsCreated())
            {
                return texture;
            }
            UnityEngine.Object.Destroy(texture);
            return null;
        }

        private static RenderTexture CreateDepthTexture(
            int width,
            int height,
            RenderTextureFormat format,
            string name)
        {
            var texture = new RenderTexture(
                width,
                height,
                0,
                format,
                RenderTextureReadWrite.Linear
            )
            {
                name = "Redux Better AA " + name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            if (texture.IsCreated())
            {
                return texture;
            }
            UnityEngine.Object.Destroy(texture);
            return null;
        }

        private void ReleaseResources()
        {
            TemporalTextures.Release(ref _historyColorA);
            TemporalTextures.Release(ref _historyColorB);
            TemporalTextures.Release(ref _historyDepthA);
            TemporalTextures.Release(ref _historyDepthB);
            _resourceWidth = 0;
            _resourceHeight = 0;
            _estimatedMemoryBytes = 0;
            _historyValid = false;
            _matrixHistoryValid = false;
            _resourceCreationFailed = false;
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

        private static int EstimateColorBytes(RenderTextureFormat format)
        {
            switch (format)
            {
                case RenderTextureFormat.ARGBFloat:
                    return 16;
                case RenderTextureFormat.ARGBHalf:
                case RenderTextureFormat.RGFloat:
                    return 8;
                case RenderTextureFormat.RFloat:
                case RenderTextureFormat.RGHalf:
                case RenderTextureFormat.ARGB32:
                case RenderTextureFormat.ARGB2101010:
                    return 4;
                default:
                    return 8;
            }
        }

        private void OnShaderLoaded(AsyncOperationHandle<Shader> operation)
        {
            if (_disposed)
            {
                return;
            }
            if (operation.Status == AsyncOperationStatus.Succeeded)
            {
                _shader = operation.Result;
                _logger.LogInfo(
                    "[ReduxBetterAA/CustomTAA] Shader loaded; backend remains mutually exclusive and opt-in."
                );
            }
            else
            {
                _logger.LogError("[ReduxBetterAA/CustomTAA] Shader failed to load.");
            }
            _availabilityChanged?.Invoke();
        }

        internal void ClearRuntimeFailure() { _lastFailure = null; }

        private void FailRuntime(string reason)
        {
            if (_lastFailure != null)
                return;
            _lastFailure = reason;
            _active = false;
            _logger.LogError("[ReduxBetterAA/CustomTAA] " + reason);
            _runtimeFailure?.Invoke(reason);
        }
    }
}
