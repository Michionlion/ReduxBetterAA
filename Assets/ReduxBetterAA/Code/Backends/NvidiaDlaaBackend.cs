using System;
using ReduxBetterAA.Backends.Nvidia;
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
    /// Phase 4 equal-input/output DLAA backend. UnityEngine.NVIDIA is reached only
    /// through NvidiaDlaaApi so an absent managed or native module remains non-fatal.
    /// </summary>
    internal sealed class NvidiaDlaaBackend : ITemporalBackend
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
        private readonly NvidiaDlaaApi _api = new NvidiaDlaaApi();
        private readonly Ppv2ExposureReader _exposureReader;

        private DlaaConfig _config = DlaaConfig.Conservative;
        private Camera _resolveCamera;
        private PostProcessLayer _resolveLayer;
        private Camera _sharedJitterCamera;
        private PostProcessLayer _sharedJitterLayer;
        private NvidiaDlaaRenderHook _hook;
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
        private bool _projectionJitterSupported;
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

        public NvidiaDlaaBackend(
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

        public string Id => "NVIDIA DLAA";
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
                ? "NVIDIA auto exposure"
                : "manual pre-exposure");
        public float EffectivePreExposure => _effectivePreExposure;
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

        public void ApplyConfig(in DlaaConfig config)
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
                unsupportedReason = "previous DLAA execution failed: " + _lastFailure;
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
            if (cameras.RenderScalePercent < 100)
            {
                unsupportedReason =
                    "DLAA requires at least 100% render scale; lower scales need a DLSS upscaler";
                return false;
            }
            if (cameras.RenderScalePercent > 100 && !_config.AllowSupersampling)
            {
                unsupportedReason =
                    "DLAA supersampling is disabled; use 100% render scale or enable its opt-in setting";
                return false;
            }
            GraphicsDeviceType graphicsApi = SystemInfo.graphicsDeviceType;
            if (graphicsApi != GraphicsDeviceType.Direct3D11 &&
                graphicsApi != GraphicsDeviceType.Direct3D12)
            {
                unsupportedReason = "Unity NVIDIA DLAA requires Direct3D 11 or Direct3D 12";
                return false;
            }
            if (SystemInfo.graphicsDeviceVendorID != 0x10DE &&
                SystemInfo.graphicsDeviceVendor.IndexOf(
                    "NVIDIA",
                    StringComparison.OrdinalIgnoreCase
                ) < 0)
            {
                unsupportedReason = "the active graphics device is not an NVIDIA GPU";
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
            _projectionJitterSupported = cameras.ProjectionJitterSupported;
            _originalResolveDepthMode = _resolveCamera.depthTextureMode;
            if (_resolveLayer != null)
            {
                _originalResolveMode = _resolveLayer.antialiasingMode;
                _resolveLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
                _resolveLayer.ResetHistory();
            }
            _resolveCamera.depthTextureMode |=
                DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

            if (_sharedJitterLayer != null && _sharedJitterLayer != _resolveLayer)
            {
                _originalSharedMode = _sharedJitterLayer.antialiasingMode;
                _sharedJitterLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
                _sharedJitterLayer.ResetHistory();
            }
            if (_sharedJitterCamera != null && _sharedJitterCamera != _resolveCamera)
            {
                _originalSharedDepthMode = _sharedJitterCamera.depthTextureMode;
                _sharedJitterCamera.depthTextureMode |=
                    DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            }

            _commandBuffer = new CommandBuffer
            {
                name = "Redux Better AA NVIDIA DLAA"
            };
            _hook = _resolveCamera.gameObject.AddComponent<NvidiaDlaaRenderHook>();
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
                BackendSelection.NvidiaDlaa
            );
            try
            {
                RenderCore(source, destination);
            }
            finally
            {
                _performanceProfiler.EndResolve(
                    BackendSelection.NvidiaDlaa,
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
                    FailRuntime("camera depth does not match the DLAA color input");
                    return;
                }
                if (!TextureMatches(motionVectors, source.width, source.height))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime("camera motion vectors do not match the DLAA color input");
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
                _sharedJitterLayer.ResetHistory();
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
            _projectionJitterSupported = false;
            _jitterPixels = Vector2.zero;
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
                _lastFailure = "DLAA output format " +
                    descriptor.graphicsFormat + " does not support random write";
                return false;
            }
            _output = new RenderTexture(descriptor)
            {
                name = "Redux Better AA NVIDIA DLAA Output",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _output.Create();
            if (!_output.IsCreated())
            {
                DestroyOutput();
                _lastFailure = "DLAA output texture creation failed";
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
                    _config.Preset,
                    out reason))
            {
                DestroyOutput();
                _lastFailure = "DLAA context creation failed: " + reason;
                return false;
            }

            _estimatedMemoryBytes = (long)source.width * source.height *
                EstimateColorBytes(source.format);
            _contextUsesVendorAutoExposure = useVendorAutoExposure;
            _usingPpv2Exposure =
                _config.AutoExposure && !useVendorAutoExposure;
            _historyResetPending = true;
            _logger.LogInfo(
                "[ReduxBetterAA/DLAA] Equal-input/output context created for " +
                source.width + "x" + source.height + ", preset " +
                _config.Preset + ", output " + _output.graphicsFormat +
                " (random-write)" +
                "; exposure: " + ExposureSource +
                (_config.AllowSupersampling ? "; supersampling allowed." : ".")
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
            // Unity's 6000.4 DLSS integration explicitly creates the output as a
            // compute/UAV resource. A render target without random-write support
            // accepts context creation but leaves the output unwritten (black).
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
                _jitterPixels = _projectionJitterSupported
                    ? SharedJitterSequence.GetCustomOffset(
                        _frameIndex,
                        _config.JitterSpread,
                        _config.SequenceLength
                    )
                    : Vector2.zero;
                ProjectionState projectionState = camera == _sharedJitterCamera
                    ? _sharedProjection
                    : _resolveProjection;
                _motionVectorSanitizer.CaptureCamera(
                    camera,
                    _projectionJitterSupported
                        ? projectionState.Projection
                        : camera.projectionMatrix
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
                ? "unknown DLAA execution failure"
                : reason;
            _active = false;
            _logger.LogError(
                "[ReduxBetterAA/DLAA] Disabled after a runtime failure: " +
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
