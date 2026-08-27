using System;
using ReduxBetterAA.Backends.Amd;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Backends
{
    /// <summary>
    /// Experimental native-resolution FSR2 AA backend using Unity's managed AMD
    /// module. It does not alter Redux render scale or include UI in history.
    /// </summary>
    internal sealed class AmdFsr2Backend : ITemporalBackend
    {
        private static readonly int CameraDepthTexture =
            Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraMotionVectorsTexture =
            Shader.PropertyToID("_CameraMotionVectorsTexture");

        private readonly ReduxLogger _logger;
        private readonly Action<string> _runtimeFailure;
        private readonly BackendPerformanceProfiler _performanceProfiler;
        private readonly MotionVectorSanitizer _motionVectorSanitizer;
        private readonly DepthDisocclusionMask _depthDisocclusionMask;
        private readonly AmdFsr2Api _api = new AmdFsr2Api();
        private readonly Ppv2ExposureReader _exposureReader;

        private Fsr2Config _config = Fsr2Config.Conservative;
        private Camera _resolveCamera;
        private PostProcessLayer _resolveLayer;
        private Camera _sharedJitterCamera;
        private PostProcessLayer _sharedJitterLayer;
        private AmdFsr2RenderHook _hook;
        private CommandBuffer _commandBuffer;
        private RenderTexture _output;
        private PostProcessLayer.Antialiasing _originalResolveMode;
        private PostProcessLayer.Antialiasing _originalSharedMode;
        private DepthTextureMode _originalResolveDepthMode;
        private DepthTextureMode _originalSharedDepthMode;
        private ProjectionState _resolveProjection;
        private ProjectionState _sharedProjection;
        private uint _frameIndex;
        private Vector2 _jitterPixels;
        private int _resourceWidth;
        private int _resourceHeight;
        private GraphicsFormat _resourceGraphicsFormat;
        private bool _resourceSrgb;
        private bool _historyResetPending;
        private bool _active;
        private bool _disposed;
        private bool _runtimeFailureLatched;
        private string _lastFailure = string.Empty;
        private long _estimatedMemoryBytes;
        private bool _contextUsesVendorAutoExposure;
        private bool _usingPpv2Exposure;
        private float _effectivePreExposure = 1.0f;

        private struct ProjectionState
        {
            public bool Applied;
            public int AppliedFrame;
            public Camera Camera;
            public Matrix4x4 Projection;
            public Matrix4x4 NonJitteredProjection;
            public bool TransparentJitter;
        }

        public AmdFsr2Backend(
            ReduxLogger logger,
            Action<string> runtimeFailure,
            BackendPerformanceProfiler performanceProfiler,
            MotionVectorSanitizer motionVectorSanitizer,
            DepthDisocclusionMask depthDisocclusionMask)
        {
            _logger = logger;
            _runtimeFailure = runtimeFailure;
            _performanceProfiler = performanceProfiler;
            _motionVectorSanitizer = motionVectorSanitizer;
            _depthDisocclusionMask = depthDisocclusionMask;
            _exposureReader = new Ppv2ExposureReader(logger);
        }

        public string Id => "FSR2 Native AA";
        public bool Active => _active;
        public bool ManagedSurfaceAvailable { get; private set; }
        public bool ContextCreated => _api.ContextCreated;
        public uint DeviceVersion => _api.DeviceVersion;
        public int InputWidth => _resourceWidth;
        public int InputHeight => _resourceHeight;
        public int OutputWidth => _output == null ? 0 : _output.width;
        public int OutputHeight => _output == null ? 0 : _output.height;
        public string OutputGraphicsFormat => _output == null
            ? string.Empty
            : _output.graphicsFormat.ToString();
        public bool OutputRandomWrite => _output != null && _output.enableRandomWrite;
        public long EstimatedMemoryBytes => _estimatedMemoryBytes;
        public string LastFailure => _lastFailure;
        public string ExposureSource => _usingPpv2Exposure
            ? "PPv2 GPU exposure"
            : (_contextUsesVendorAutoExposure
                ? "FSR2 auto exposure"
                : "manual pre-exposure");
        public float EffectivePreExposure => _effectivePreExposure;
        public Vector2 ProjectionJitterPixels => _jitterPixels;
        public Vector2 DispatchJitterPixels =>
            AmdFsr2Api.ToDispatchJitter(_jitterPixels);
        public Vector2 CurrentJitterNormalized =>
            _resourceWidth > 0 && _resourceHeight > 0
                ? new Vector2(
                    _jitterPixels.x / _resourceWidth,
                    _jitterPixels.y / _resourceHeight
                )
                : Vector2.zero;

        public void Initialize()
        {
            string reason;
            ManagedSurfaceAvailable = _api.TryBindManagedSurface(out reason);
            if (!ManagedSurfaceAvailable)
            {
                _lastFailure = reason;
            }
        }

        public void ApplyConfig(in Fsr2Config config)
        {
            _config = config;
        }

        public void ClearRuntimeFailure()
        {
            _runtimeFailureLatched = false;
            _lastFailure = string.Empty;
        }

        public bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason)
        {
            if (_runtimeFailureLatched)
            {
                unsupportedReason = "previous FSR2 execution failed: " + _lastFailure;
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
            if (cameras.RenderScalePercent != 100)
            {
                unsupportedReason =
                    "FSR2 Native AA requires 100% render scale (equal input/output dimensions)";
                return false;
            }
            GraphicsDeviceType graphicsApi = SystemInfo.graphicsDeviceType;
            if (graphicsApi != GraphicsDeviceType.Direct3D11 &&
                graphicsApi != GraphicsDeviceType.Direct3D12)
            {
                unsupportedReason =
                    "this Unity AMD FSR2 prototype requires Direct3D 11 or Direct3D 12";
                return false;
            }
            if (!SystemInfo.supportsMotionVectors)
            {
                unsupportedReason = "Unity reports no motion-vector support";
                return false;
            }
            if (!_motionVectorSanitizer.Ready)
            {
                unsupportedReason = _motionVectorSanitizer.Status;
                return false;
            }
            if (!_api.TryInitialize(out unsupportedReason))
            {
                _lastFailure = unsupportedReason;
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
            _originalResolveDepthMode = _resolveCamera.depthTextureMode;
            if (_resolveLayer != null)
            {
                _originalResolveMode = _resolveLayer.antialiasingMode;
                _resolveLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            }
            _resolveCamera.depthTextureMode |=
                DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

            if (_sharedJitterLayer != null && _sharedJitterLayer != _resolveLayer)
            {
                _originalSharedMode = _sharedJitterLayer.antialiasingMode;
                _sharedJitterLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            }
            if (_sharedJitterCamera != null && _sharedJitterCamera != _resolveCamera)
            {
                _originalSharedDepthMode = _sharedJitterCamera.depthTextureMode;
                _sharedJitterCamera.depthTextureMode |=
                    DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            }

            _commandBuffer = new CommandBuffer
            {
                name = "Redux Better AA AMD FSR2 Native AA"
            };
            _hook = _resolveCamera.gameObject.AddComponent<AmdFsr2RenderHook>();
            _hook.hideFlags = HideFlags.HideAndDontSave;
            _hook.Owner = this;
            _exposureReader.Configure(_resolveLayer);
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
            _historyResetPending = true;
            _active = true;
            failureReason = string.Empty;
            return true;
        }

        public void Tick(uint frameIndex)
        {
            _frameIndex = frameIndex;
        }

        public void ResetHistory(HistoryResetReason reason)
        {
            _historyResetPending = true;
            _motionVectorSanitizer.ResetCameraHistory();
        }

        public void Render(RenderTexture source, RenderTexture destination)
        {
            long start = _performanceProfiler.BeginResolve(
                BackendSelection.AmdFsr2
            );
            try
            {
                RenderCore(source, destination);
            }
            finally
            {
                _performanceProfiler.EndResolve(
                    BackendSelection.AmdFsr2,
                    start
                );
            }
        }

        private void RenderCore(RenderTexture source, RenderTexture destination)
        {
            if (!_active || source == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            try
            {
                bool usePpv2Exposure = false;
                float ppv2Exposure = 1.0f;
                if (_config.AutoExposure && _config.PreferPpv2Exposure)
                {
                    bool hasPpv2Exposure = _exposureReader.TryGetExposure(
                        out ppv2Exposure
                    );
                    usePpv2Exposure = hasPpv2Exposure;
                    if (!hasPpv2Exposure)
                    {
                        ppv2Exposure = 1.0f;
                    }
                }
                bool useVendorAutoExposure =
                    _config.AutoExposure && !usePpv2Exposure;
                _effectivePreExposure = usePpv2Exposure
                    ? Mathf.Clamp(ppv2Exposure, 0.2f, 2.0f)
                    : (_config.AutoExposure ? 1.0f : _config.PreExposure);

                if (!EnsureResources(source, useVendorAutoExposure))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime(_lastFailure);
                    return;
                }

                Texture depth = Shader.GetGlobalTexture(CameraDepthTexture);
                Texture motionVectors = Shader.GetGlobalTexture(
                    CameraMotionVectorsTexture
                );
                if (!TextureMatches(depth, source.width, source.height))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime("camera depth does not match the FSR2 color input");
                    return;
                }
                if (!TextureMatches(motionVectors, source.width, source.height))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime("camera motion vectors do not match the FSR2 color input");
                    return;
                }
                Texture sanitizedMotion;
                if (!_motionVectorSanitizer.TrySanitize(
                        motionVectors,
                        depth,
                        source.width,
                        source.height,
                        _jitterPixels,
                        _config.InvertMotionX,
                        _config.InvertMotionY,
                        out sanitizedMotion))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime(
                        "motion-vector sanitization failed: " +
                        _motionVectorSanitizer.Status
                    );
                    return;
                }

                Texture biasColorMask;
                _depthDisocclusionMask.TryGenerate(
                    source,
                    depth,
                    sanitizedMotion,
                    source.width,
                    source.height,
                    out biasColorMask
                );

                _api.Execute(
                    _commandBuffer,
                    source,
                    _output,
                    depth,
                    sanitizedMotion,
                    biasColorMask,
                    source.width,
                    source.height,
                    _jitterPixels,
                    _resolveCamera,
                    Time.unscaledDeltaTime * 1000.0f,
                    _effectivePreExposure,
                    in _config,
                    _historyResetPending
                );
                _historyResetPending = false;
                Graphics.Blit(_output, destination);
            }
            catch (Exception exception)
            {
                Graphics.Blit(source, destination);
                FailRuntime(exception.GetType().Name + ": " + exception.Message);
            }
        }

        public void Deactivate()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            RestoreProjection(ref _resolveProjection);
            RestoreProjection(ref _sharedProjection);

            if (_hook != null)
            {
                _hook.enabled = false;
                _hook.Owner = null;
                UnityEngine.Object.Destroy(_hook);
                _hook = null;
            }
            if (_resolveLayer != null)
            {
                _resolveLayer.antialiasingMode = _originalResolveMode;
                _resolveLayer.ResetHistory();
            }
            if (_resolveCamera != null)
            {
                _resolveCamera.depthTextureMode = _originalResolveDepthMode;
            }
            if (_sharedJitterLayer != null && _sharedJitterLayer != _resolveLayer)
            {
                _sharedJitterLayer.antialiasingMode = _originalSharedMode;
            }
            if (_sharedJitterCamera != null && _sharedJitterCamera != _resolveCamera)
            {
                _sharedJitterCamera.depthTextureMode = _originalSharedDepthMode;
            }

            ReleaseResources();
            if (_commandBuffer != null)
            {
                _commandBuffer.Release();
                _commandBuffer = null;
            }
            _resolveCamera = null;
            _resolveLayer = null;
            _sharedJitterCamera = null;
            _sharedJitterLayer = null;
            _historyResetPending = false;
            _motionVectorSanitizer.ResetCameraHistory();
            _exposureReader.Deactivate();
            _contextUsesVendorAutoExposure = false;
            _usingPpv2Exposure = false;
            _effectivePreExposure = 1.0f;
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
            _exposureReader.Dispose();
        }

        private bool EnsureResources(
            RenderTexture source,
            bool useVendorAutoExposure)
        {
            if (_output != null &&
                _resourceWidth == source.width &&
                _resourceHeight == source.height &&
                _resourceGraphicsFormat == source.graphicsFormat &&
                _resourceSrgb == source.sRGB &&
                _contextUsesVendorAutoExposure == useVendorAutoExposure)
            {
                return true;
            }

            ReleaseResources();
            RenderTextureDescriptor descriptor = BuildOutputDescriptor(
                source.descriptor
            );
            if (!SystemInfo.IsFormatSupported(
                    descriptor.graphicsFormat,
                    GraphicsFormatUsage.LoadStore))
            {
                _lastFailure = "FSR2 output format " +
                    descriptor.graphicsFormat + " does not support random write";
                return false;
            }
            _output = new RenderTexture(descriptor)
            {
                name = "Redux Better AA FSR2 Native Output",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _output.Create();
            if (!_output.IsCreated())
            {
                DestroyOutput();
                _lastFailure = "FSR2 output texture creation failed";
                return false;
            }

            _resourceWidth = source.width;
            _resourceHeight = source.height;
            _resourceGraphicsFormat = source.graphicsFormat;
            _resourceSrgb = source.sRGB;
            bool hdr = IsHdrFormat(source.format);
            string reason;
            if (!_api.TryCreateContext(
                    _commandBuffer,
                    source.width,
                    source.height,
                    hdr,
                    useVendorAutoExposure,
                    out reason))
            {
                DestroyOutput();
                _lastFailure = "FSR2 context creation failed: " + reason;
                return false;
            }

            _estimatedMemoryBytes = (long)source.width * source.height *
                EstimateColorBytes(source.format);
            _contextUsesVendorAutoExposure = useVendorAutoExposure;
            _usingPpv2Exposure =
                _config.AutoExposure && !useVendorAutoExposure;
            _historyResetPending = true;
            _logger.LogInfo(
                "[ReduxBetterAA/FSR2] Native-resolution context created for " +
                source.width + "x" + source.height + ", API v" +
                _api.DeviceVersion + ", output " + _output.graphicsFormat +
                " (random-write); exposure: " + ExposureSource + "."
            );
            return true;
        }

        internal static RenderTextureDescriptor BuildOutputDescriptor(
            RenderTextureDescriptor sourceDescriptor)
        {
            sourceDescriptor.depthBufferBits = 0;
            sourceDescriptor.msaaSamples = 1;
            sourceDescriptor.bindMS = false;
            sourceDescriptor.graphicsFormat = GraphicsFormatUtility.GetLinearFormat(
                sourceDescriptor.graphicsFormat
            );
            sourceDescriptor.enableRandomWrite = true;
            sourceDescriptor.useMipMap = false;
            sourceDescriptor.autoGenerateMips = false;
            sourceDescriptor.useDynamicScale = false;
            return sourceDescriptor;
        }

        private void ReleaseResources()
        {
            if (_commandBuffer != null)
            {
                _api.DestroyContext(_commandBuffer);
            }
            DestroyOutput();
            _resourceWidth = 0;
            _resourceHeight = 0;
            _resourceGraphicsFormat = GraphicsFormat.None;
            _estimatedMemoryBytes = 0;
            _contextUsesVendorAutoExposure = false;
            _usingPpv2Exposure = false;
        }

        private void DestroyOutput()
        {
            if (_output == null)
            {
                return;
            }
            _output.Release();
            UnityEngine.Object.Destroy(_output);
            _output = null;
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (!_active || camera == null)
            {
                return;
            }
            if (camera == _sharedJitterCamera)
            {
                ApplyJitter(camera, ref _sharedProjection);
            }
            if (camera == _resolveCamera)
            {
                if (camera != _sharedJitterCamera)
                {
                    ApplyJitter(camera, ref _resolveProjection);
                }
                _jitterPixels = SharedJitterSequence.GetCustomOffset(
                    _frameIndex,
                    _config.JitterSpread,
                    _config.SequenceLength
                );
                ProjectionState projectionState = camera == _sharedJitterCamera
                    ? _sharedProjection
                    : _resolveProjection;
                _motionVectorSanitizer.CaptureCamera(
                    camera,
                    projectionState.Projection
                );
            }
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera == _resolveCamera)
            {
                RestoreProjection(ref _resolveProjection);
            }
            if (camera == _sharedJitterCamera)
            {
                RestoreProjection(ref _sharedProjection);
            }
        }

        private void ApplyJitter(Camera camera, ref ProjectionState state)
        {
            if (state.Applied)
            {
                if (state.AppliedFrame == Time.frameCount)
                {
                    return;
                }

                // onPostRender can be skipped when Unity aborts a camera render.
                // Never carry that frame's projection jitter into a later frame.
                RestoreProjection(ref state);
            }
            Vector2 jitter = SharedJitterSequence.GetCustomOffset(
                _frameIndex,
                _config.JitterSpread,
                _config.SequenceLength
            );
            state.Applied = true;
            state.AppliedFrame = Time.frameCount;
            state.Camera = camera;
            state.Projection = camera.projectionMatrix;
            state.NonJitteredProjection = camera.nonJitteredProjectionMatrix;
            state.TransparentJitter =
                camera.useJitteredProjectionMatrixForTransparentRendering;
            camera.nonJitteredProjectionMatrix = state.Projection;
            camera.projectionMatrix = camera.orthographic
                ? RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera, jitter)
                : RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera, jitter);
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
        }

        private static void RestoreProjection(ref ProjectionState state)
        {
            if (!state.Applied)
            {
                return;
            }
            if (state.Camera != null)
            {
                state.Camera.projectionMatrix = state.Projection;
                state.Camera.nonJitteredProjectionMatrix = state.NonJitteredProjection;
                state.Camera.useJitteredProjectionMatrixForTransparentRendering =
                    state.TransparentJitter;
            }
            state.Applied = false;
            state.AppliedFrame = -1;
            state.Camera = null;
        }

        private void FailRuntime(string reason)
        {
            if (_runtimeFailureLatched)
            {
                return;
            }
            _runtimeFailureLatched = true;
            _lastFailure = string.IsNullOrEmpty(reason)
                ? "unknown FSR2 execution failure"
                : reason;
            _active = false;
            _logger.LogError(
                "[ReduxBetterAA/FSR2] Disabled after a runtime failure: " +
                _lastFailure + "."
            );
            _runtimeFailure?.Invoke(_lastFailure);
        }

        private static bool TextureMatches(Texture texture, int width, int height)
        {
            return texture != null && texture.width == width && texture.height == height;
        }

        private static bool IsHdrFormat(RenderTextureFormat format)
        {
            return format == RenderTextureFormat.ARGBHalf ||
                   format == RenderTextureFormat.ARGBFloat ||
                   format == RenderTextureFormat.RGB111110Float ||
                   format == RenderTextureFormat.DefaultHDR;
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
                default:
                    return 4;
            }
        }
    }
}
