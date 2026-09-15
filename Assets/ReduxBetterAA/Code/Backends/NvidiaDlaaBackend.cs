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
    /// Native DLAA and DLSS reconstruction. UnityEngine.NVIDIA is reached only
    /// through NvidiaDlaaApi so an absent managed or native module remains non-fatal.
    /// </summary>
    internal sealed class NvidiaDlaaBackend : ITemporalBackend, ISceneResolve, IProjectionJitterSource
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
        private readonly ResolvedFrameCapture _resolvedCapture = new ResolvedFrameCapture();

        private DlaaConfig _config = DlaaConfig.Conservative;
        private ReconstructionQuality _reconstructionQuality = ReconstructionQuality.Native;
        private ReconstructionQuality _contextQuality;
        private DlaaPreset _contextPreset;
        private bool _contextHdr;
        private int _reconstructionSequenceLength = 32;
        private Camera _resolveCamera;
        private PostProcessLayer _resolveLayer;
        private Camera _sharedJitterCamera;
        private PostProcessLayer _sharedJitterLayer;
        private TemporalRenderHook _hook;
        private SceneCameraState _resolveState;
        private SceneCameraState _sharedState;
        private CommandBuffer _commandBuffer;
        private RenderTexture _output;
        private CameraProjectionState _resolveProjection;
        private CameraProjectionState _sharedProjection;
        private uint _frameIndex;
        private Vector2 _jitterPixels;
        private int _resourceWidth;
        private int _resourceHeight;
        private GraphicsFormat _resourceGraphicsFormat;
        private bool _resourceSrgb;
        private bool _historyResetPending;
        private bool _projectionJitterSupported;
        private bool _jitterTransparentRendering;
        private bool _active;
        private bool _disposed;
        private bool _runtimeFailureLatched;
        private string _lastFailure = string.Empty;
        private long _estimatedMemoryBytes;
        private bool _contextUsesVendorAutoExposure;
        private bool _usingPpv2Exposure;
        private float _effectivePreExposure = 1.0f;
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

        public string Id => IsSuperResolution ? "NVIDIA DLSS" : "NVIDIA DLAA";
        private bool IsSuperResolution => _reconstructionQuality != ReconstructionQuality.Native;
        private BackendSelection Selection => IsSuperResolution
            ? BackendSelection.NvidiaDlss : BackendSelection.NvidiaDlaa;
        internal bool RenderEnabled = true;
        public bool Active => _active && RenderEnabled;

        public bool TryGetRasterProjection(Camera camera, out Matrix4x4 projection)
        {
            projection = default;
            if (!Active || !_projectionJitterSupported || camera == null ||
                (camera != _resolveCamera && camera != _sharedJitterCamera)) return false;
            CameraProjectionState state = camera == _sharedJitterCamera ? _sharedProjection : _resolveProjection;
            projection = state.GetRasterProjection(camera, GetJitterOffset());
            return true;
        }
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
        public bool ContextUsesHdr => _api.ContextCreated && _contextHdr;
        public long EstimatedMemoryBytes => _estimatedMemoryBytes;
        public string LastFailure => _lastFailure;
        public string ExposureSource => IsSuperResolution
            ? "post-PPv2 color (HDR processing, pre-exposure 1)"
            : _usingPpv2Exposure
            ? "PPv2 GPU exposure"
            : (_contextUsesVendorAutoExposure
                ? "NVIDIA auto exposure"
                : "manual pre-exposure");
        public float EffectivePreExposure => _effectivePreExposure;
        public Vector2 CurrentJitterNormalized =>
            _resourceWidth > 0 && _resourceHeight > 0
                ? new Vector2(_jitterPixels.x / _resourceWidth, _jitterPixels.y / _resourceHeight)
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

        public void ConfigureReconstruction(ReconstructionQuality quality)
        {
            NvidiaDlaaApi.GetDlssQualityValue(quality); // Reject unknown persisted values.
            if (_reconstructionQuality == quality) return;
            _reconstructionQuality = quality;
            _historyResetPending = true;
        }

        public bool TryGetOptimalRenderSize(int outputWidth, int outputHeight,
            ReconstructionQuality quality, out int width, out int height, out string reason) =>
            _api.TryGetOptimalRenderSize(outputWidth, outputHeight, quality,
                out width, out height, out reason);

        public bool TryGetRenderPercent(int outputWidth, int outputHeight,
            ReconstructionQuality quality, out int percent, out string reason) =>
            _api.TryGetRenderPercent(outputWidth, outputHeight, quality, out percent, out reason);

        public void ClearRuntimeFailure()
        {
            _runtimeFailureLatched = false;
            _lastFailure = string.Empty;
        }

        public bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason)
        {
            if (_runtimeFailureLatched)
            {
                unsupportedReason = "previous NVIDIA reconstruction failed: " + _lastFailure;
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
            if (IsSuperResolution && (cameras.SceneKind != TemporalSceneKind.Flight &&
                cameras.SceneKind != TemporalSceneKind.KerbalSpaceCenter))
            {
                unsupportedReason = "DLSS upscaling currently requires the flight/KSC scene-output contract";
                return false;
            }
            if (!IsSuperResolution && cameras.RenderScalePercent < 100)
            {
                unsupportedReason =
                    "DLAA requires at least 100% render scale; lower scales need a DLSS upscaler";
                return false;
            }
            if (!IsSuperResolution && cameras.RenderScalePercent > 100 && !_config.AllowSupersampling)
            {
                unsupportedReason =
                    "DLAA supersampling is disabled; use 100% render scale or enable its opt-in setting";
                return false;
            }
            GraphicsDeviceType graphicsApi = SystemInfo.graphicsDeviceType;
            if (graphicsApi != GraphicsDeviceType.Direct3D11 &&
                graphicsApi != GraphicsDeviceType.Direct3D12)
            {
                unsupportedReason = "Unity NVIDIA reconstruction requires Direct3D 11 or Direct3D 12";
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
            _jitterTransparentRendering = cameras.JitterTransparentRendering;
            if (IsSuperResolution)
            {
                var sceneOutput = ReduxSceneOutput.Current;
                if (sceneOutput == null)
                {
                    failureReason = "DLSS scene-output ownership is unavailable";
                    return false;
                }
                if (!sceneOutput.TryGetFrame(_resolveCamera, out SceneOutputFrame frame, out failureReason))
                    return false;
                _reconstructionSequenceLength = GetReconstructionSequenceLength(
                    frame.RenderWidth, frame.RenderHeight, frame.OutputWidth, frame.OutputHeight);
            }
            _resolveState.Capture(_resolveCamera, _resolveLayer);
            _sharedState.Capture(
                _sharedJitterCamera != _resolveCamera ? _sharedJitterCamera : null,
                _sharedJitterLayer != _resolveLayer ? _sharedJitterLayer : null);

            _commandBuffer = new CommandBuffer
            {
                name = "Redux Better AA " + Id
            };
            _hook = TemporalRenderHook.Attach(_resolveCamera, this);
            _exposureReader.Configure(_resolveLayer);
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
            _historyResetPending = true;
            _active = true;
            _resolvedCapture.Configure(_resolveCamera, Selection);
            failureReason = string.Empty;
            return true;
        }

        public void Tick(uint frameIndex)
        {
            _frameIndex = frameIndex;
        }

        public void ResetHistory(HistoryResetReason reason)
        {
            _resolvedCapture.Reset(reason);
            _historyResetPending = true;
            _motionVectorSanitizer.ResetCameraHistory();
        }

        public void Render(RenderTexture source, RenderTexture destination)
        {
            long start = _performanceProfiler.BeginResolve(
                Selection
            );
            try
            {
                RenderCore(source, destination);
            }
            finally
            {
                _performanceProfiler.EndResolve(
                    Selection,
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
                SceneOutputFrame frame = default;
                ReduxSceneOutput sceneOutput = null;
                int outputWidth = source.width;
                int outputHeight = source.height;
                if (IsSuperResolution)
                {
                    sceneOutput = ReduxSceneOutput.Current;
                    if (sceneOutput == null)
                    {
                        Graphics.Blit(source, destination);
                        FailRuntime("DLSS scene-output ownership was lost");
                        return;
                    }
                    if (!sceneOutput.TryGetFrame(_resolveCamera, out frame, out string reason))
                    {
                        Graphics.Blit(source, destination);
                        FailRuntime(reason);
                        return;
                    }
                    if (source.width != frame.RenderWidth || source.height != frame.RenderHeight)
                    {
                        Graphics.Blit(source, destination);
                        FailRuntime("DLSS color input does not match the claimed scene render dimensions");
                        return;
                    }
                    outputWidth = frame.OutputWidth;
                    outputHeight = frame.OutputHeight;
                }
                float ppv2Exposure = 1.0f;
                bool usePpv2Exposure = !IsSuperResolution && _config.AutoExposure &&
                    _config.PreferPpv2Exposure &&
                    _exposureReader.TryGetExposure(out ppv2Exposure);
                bool useVendorAutoExposure =
                    !IsSuperResolution && _config.AutoExposure && !usePpv2Exposure;
                // SR runs after PPv2, so no scene pre-exposure is applied again.
                // HDR processing preserves the input's linear encoding and values
                // above one; it does not move this hook before tone mapping.
                _effectivePreExposure = IsSuperResolution ? 1.0f : usePpv2Exposure
                    ? Mathf.Clamp(ppv2Exposure, 0.2f, 2.0f)
                    : (_config.AutoExposure ? 1.0f : _config.PreExposure);

                if (!EnsureResources(source, outputWidth, outputHeight, useVendorAutoExposure))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime(_lastFailure);
                    return;
                }

                Texture depth = Shader.GetGlobalTexture(CameraDepthTexture);
                Texture motionVectors = Shader.GetGlobalTexture(
                    CameraMotionVectorsTexture
                );
                if (!TemporalTextures.Matches(depth, source.width, source.height))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime("camera depth does not match the NVIDIA color input");
                    return;
                }
                if (!TemporalTextures.Matches(motionVectors, source.width, source.height))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime("camera motion vectors do not match the NVIDIA color input");
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
                if (IsSuperResolution)
                {
                    if (!sceneOutput.Submit(in frame, _output, out string reason))
                    {
                        Graphics.Blit(source, destination);
                        FailRuntime(reason);
                        return;
                    }
                    // Redux presents the full-sized output directly. This copy
                    // only completes Unity's original low-resolution image chain.
                    Graphics.Blit(source, destination);
                }
                else
                {
                    Graphics.Blit(_output, destination);
                }
                _resolvedCapture.PublishResolved(_output, depth, sanitizedMotion, _historyResetPending,
                    _effectivePreExposure, new Vector2(_config.InvertMotionX ? -1 : 1, _config.InvertMotionY ? -1 : 1),
                    IsSuperResolution, in frame);
                _historyResetPending = false;
            }
            catch (Exception exception)
            {
                Graphics.Blit(source, destination);
                FailRuntime(exception.GetType().Name + ": " + exception.Message);
            }
        }

        public void Deactivate()
        {
            _resolvedCapture.Deactivate();
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            _resolveProjection.Restore();
            _sharedProjection.Restore();

            TemporalRenderHook.Detach(ref _hook);
            _sharedState.Restore();
            _resolveState.Restore();

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
            _jitterTransparentRendering = false;
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
            int outputWidth,
            int outputHeight,
            bool useVendorAutoExposure)
        {
            // NVIDIA requires nonlinear, bounded [0,1] input for LDR processing
            // (DLSS Programming Guide 3.1.2). Redux's post-PPv2 SR input exceeds
            // that range. Use high-precision processing while retaining exposure 1.
            bool hdr = IsSuperResolution || TemporalTextures.IsHdr(source.format);
            if (TemporalTextures.IsCreated(_output) && _api.ContextCreated &&
                _resourceWidth == source.width &&
                _resourceHeight == source.height &&
                _resourceGraphicsFormat == source.graphicsFormat &&
                _resourceSrgb == source.sRGB &&
                _output.width == outputWidth && _output.height == outputHeight &&
                _contextQuality == _reconstructionQuality && _contextHdr == hdr &&
                (IsSuperResolution || _contextPreset == _config.Preset) &&
                _contextUsesVendorAutoExposure == useVendorAutoExposure)
            {
                return true;
            }

            ReleaseResources();
            RenderTextureDescriptor descriptor = BuildOutputDescriptor(
                source.descriptor, outputWidth, outputHeight
            );
            if (!SystemInfo.IsFormatSupported(
                    descriptor.graphicsFormat,
                    GraphicsFormatUsage.LoadStore))
            {
                _lastFailure = "NVIDIA output format " +
                    descriptor.graphicsFormat + " does not support random write";
                return false;
            }
            _output = new RenderTexture(descriptor)
            {
                name = "Redux Better AA " + Id + " Output",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            _output.Create();
            if (!_output.IsCreated())
            {
                TemporalTextures.Release(ref _output);
                _lastFailure = "NVIDIA output texture creation failed";
                return false;
            }

            _resourceWidth = source.width;
            _resourceHeight = source.height;
            _resourceGraphicsFormat = source.graphicsFormat;
            _resourceSrgb = source.sRGB;
            string reason;
            if (!_api.TryCreateContext(
                    _commandBuffer,
                    source.width,
                    source.height,
                    outputWidth,
                    outputHeight,
                    _reconstructionQuality,
                    hdr,
                    useVendorAutoExposure,
                    _config.Preset,
                    out reason))
            {
                TemporalTextures.Release(ref _output);
                _lastFailure = "NVIDIA context creation failed: " + reason;
                return false;
            }

            _estimatedMemoryBytes = (long)outputWidth * outputHeight *
                TemporalTextures.EstimateColorBytes(_output.format);
            _contextQuality = _reconstructionQuality;
            _contextPreset = _config.Preset;
            _contextHdr = hdr;
            _contextUsesVendorAutoExposure = useVendorAutoExposure;
            _usingPpv2Exposure =
                !IsSuperResolution && _config.AutoExposure && !useVendorAutoExposure;
            _historyResetPending = true;
            _logger.LogInfo(
                "[ReduxBetterAA/NVIDIA] " + Id + " context created for " +
                source.width + "x" + source.height + " -> " + outputWidth + "x" + outputHeight +
                ", quality " + _reconstructionQuality +
                (IsSuperResolution ? ", vendor preset default" : ", preset " + _config.Preset) +
                ", output " + _output.graphicsFormat +
                " (random-write)" +
                "; exposure: " + ExposureSource +
                (!IsSuperResolution && _config.AllowSupersampling ? "; supersampling allowed." : ".")
            );
            return true;
        }

        internal static RenderTextureDescriptor BuildOutputDescriptor(RenderTextureDescriptor source) =>
            TemporalTextures.VendorOutputDescriptor(source);

        internal static RenderTextureDescriptor BuildOutputDescriptor(
            RenderTextureDescriptor source, int outputWidth, int outputHeight)
        {
            if (outputWidth <= 0 || outputHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputWidth));
            source = BuildOutputDescriptor(source);
            source.width = outputWidth;
            source.height = outputHeight;
            return source;
        }

        internal static int GetReconstructionSequenceLength(
            int renderWidth, int renderHeight, int outputWidth, int outputHeight)
        {
            if (renderWidth <= 0 || renderHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
                return 32;
            double ratio = (double)outputWidth / renderWidth * outputHeight / renderHeight;
            return (int)Math.Min(32, Math.Max(8, Math.Ceiling(8 * ratio)));
        }

        private Vector2 GetJitterOffset() => SharedJitterSequence.GetCustomOffset(
            _frameIndex, IsSuperResolution ? 1.0f : _config.JitterSpread,
            IsSuperResolution ? _reconstructionSequenceLength : _config.SequenceLength);

        private void ReleaseResources()
        {
            if (_commandBuffer != null)
            {
                _api.DestroyContext(_commandBuffer);
            }
            TemporalTextures.Release(ref _output);
            _resourceWidth = 0;
            _resourceHeight = 0;
            _resourceGraphicsFormat = GraphicsFormat.None;
            _estimatedMemoryBytes = 0;
            _contextUsesVendorAutoExposure = false;
            _usingPpv2Exposure = false;
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (!Active || camera == null)
            {
                return;
            }
            if (_projectionJitterSupported &&
                camera == _sharedJitterCamera)
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
                    ? GetJitterOffset()
                    : Vector2.zero;
                CameraProjectionState projectionState = camera == _sharedJitterCamera
                    ? _sharedProjection
                    : _resolveProjection;
                _motionVectorSanitizer.CaptureCamera(
                    camera,
                    _projectionJitterSupported
                        ? projectionState.Projection
                        : camera.projectionMatrix
                );
                _resolvedCapture.Snapshot(camera, _projectionJitterSupported
                    ? projectionState.Projection : camera.projectionMatrix, _jitterPixels);
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
            state.Apply(camera, GetJitterOffset(),
                jitterTransparentRendering: _jitterTransparentRendering);
        }

        private void FailRuntime(string reason)
        {
            // A resize/Redux graph rebuild invalidates the frame, not the GPU.
            // The coordinator reacquires it before the next vendor context.
            if (IsSuperResolution && ReduxSceneOutput.Current?.ReacquisitionPending == true) return;
            if (_runtimeFailureLatched)
            {
                return;
            }
            _runtimeFailureLatched = true;
            _lastFailure = string.IsNullOrEmpty(reason)
                ? "unknown NVIDIA reconstruction failure"
                : reason;
            _active = false;
            _logger.LogError(
                "[ReduxBetterAA/NVIDIA] Disabled after a runtime failure: " +
                _lastFailure + "."
            );
            _runtimeFailure?.Invoke(_lastFailure);
        }

    }
}
