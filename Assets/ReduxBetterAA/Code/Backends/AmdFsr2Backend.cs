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
    /// AMD native AA through the bundled FSR SDK bridge.
    /// The stable class/selection names remain for saved-setting compatibility.
    /// </summary>
    internal sealed class AmdFsr2Backend : ITemporalBackend, ISceneResolve, IProjectionJitterSource
    {
        private int _outputWidth, _outputHeight;
        private Vector2 Jitter => SharedJitterSequence.GetCustomOffset(_frameIndex,
            _config.JitterSpread, _config.SequenceLength);
        private static readonly int CameraDepthTexture =
            Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraMotionVectorsTexture =
            Shader.PropertyToID("_CameraMotionVectorsTexture");

        private readonly ReduxLogger _logger;
        private readonly Action<string> _runtimeFailure;
        private readonly BackendPerformanceProfiler _performanceProfiler;
        private readonly MotionVectorSanitizer _motionVectorSanitizer;
        private readonly AmdFsrNativeApi _nativeApi = new AmdFsrNativeApi();
        private readonly Ppv2ExposureReader _exposureReader;
        private bool _usingPpv2Exposure;
        private float _effectivePreExposure = 1.0f;
        private readonly Action _availabilityChanged;
        private string _providerName = "AMD FSR";

        private Fsr2Config _config = Fsr2Config.Conservative;
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

        public AmdFsr2Backend(
            ReduxLogger logger,
            Action<string> runtimeFailure,
            BackendPerformanceProfiler performanceProfiler,
            MotionVectorSanitizer motionVectorSanitizer,
            DepthDisocclusionMask depthDisocclusionMask,
            Action availabilityChanged = null)
        {
            _availabilityChanged = availabilityChanged;
            _logger = logger;
            _runtimeFailure = runtimeFailure;
            _performanceProfiler = performanceProfiler;
            _motionVectorSanitizer = motionVectorSanitizer;
            _exposureReader = new Ppv2ExposureReader(logger);
        }

        public string Id => _providerName + " Native AA";
        internal bool RenderEnabled = true;
        public bool Active => _active && RenderEnabled;

        public bool TryGetRasterProjection(Camera camera, out Matrix4x4 projection)
        {
            projection = default;
            if (!Active || !_projectionJitterSupported || camera == null ||
                (camera != _resolveCamera && camera != _sharedJitterCamera)) return false;
            CameraProjectionState state = camera == _sharedJitterCamera ? _sharedProjection : _resolveProjection;
            projection = state.GetRasterProjection(camera, Jitter);
            return true;
        }
        public bool ManagedSurfaceAvailable { get; private set; }
        public bool ContextCreated => _nativeApi.ContextCreated;
        public bool? ContextUsesHdr => _nativeApi.ContextCreated
            ? (bool?)_nativeApi.ContextUsesHdr : null;
        public uint DeviceVersion => 0u;
        public int InputWidth => _resourceWidth;
        public int InputHeight => _resourceHeight;
        public int OutputWidth => _output == null ? 0 : _output.width;
        public int OutputHeight => _output == null ? 0 : _output.height;
        public string OutputGraphicsFormat => _output == null
            ? string.Empty
            : _output.graphicsFormat.ToString();
        public bool OutputRandomWrite => _output != null && _output.enableRandomWrite;
        public long EstimatedMemoryBytes => _estimatedMemoryBytes + _nativeApi.EstimatedMemoryBytes;
        public string LastFailure => _lastFailure;
        public string ExposureSource => _usingPpv2Exposure ? "PPv2 GPU exposure" :
            _nativeApi.ContextUsesAutoExposure ? "FSR auto exposure" : "manual pre-exposure";
        public float EffectivePreExposure => _effectivePreExposure;
        public Vector2 ProjectionJitterPixels => _jitterPixels;
        public Vector2 DispatchJitterPixels =>
            AmdFsrNativeApi.ToDispatchJitter(_jitterPixels);
        public Vector2 CurrentJitterNormalized =>
            _resourceWidth > 0 && _resourceHeight > 0
                ? new Vector2(
                    _jitterPixels.x / _resourceWidth,
                    _jitterPixels.y / _resourceHeight
                )
                : Vector2.zero;

        public void Initialize()
        {
            ManagedSurfaceAvailable = AmdFsrNativeApi.Probe(out string provider, out string reason);
            if (ManagedSurfaceAvailable)
            {
                _providerName = provider;
                _nativeApi.Initialize(_availabilityChanged);
                return;
            }
            _lastFailure = reason;
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
            if (!ProjectionOverride.Available)
            {
                unsupportedReason = "Unity projection ownership is unavailable";
                return false;
            }
            if (_runtimeFailureLatched)
            {
                unsupportedReason = "previous " + _providerName + " execution failed: " + _lastFailure;
                return false;
            }
            if (!ManagedSurfaceAvailable)
            {
                unsupportedReason = string.IsNullOrEmpty(_lastFailure)
                    ? "the bundled AMD FSR runtime is unavailable" : _lastFailure;
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
                    _providerName + " Native AA requires 100% render scale (equal input/output dimensions)";
                return false;
            }
            GraphicsDeviceType graphicsApi = SystemInfo.graphicsDeviceType;
            if (graphicsApi != GraphicsDeviceType.Direct3D11)
            {
                unsupportedReason =
                    "the AMD FSR bridge currently requires Direct3D 11";
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
            unsupportedReason = _nativeApi.Ready ? string.Empty : "FSR bridge input shader is loading or unavailable";
            return _nativeApi.Ready;
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
                _outputWidth = source.width;
                _outputHeight = source.height;
                bool hasPpv2Exposure = _config.AutoExposure && _config.PreferPpv2Exposure &&
                    _exposureReader.TryGetExposure(out _);
                SelectExposure(in _config, hasPpv2Exposure, _exposureReader.Exposure,
                    out _usingPpv2Exposure, out bool vendorAutoExposure, out _effectivePreExposure);
                if (!EnsureResources(source, vendorAutoExposure))
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
                    FailRuntime("camera depth does not match the FSR color input");
                    return;
                }
                if (!TemporalTextures.Matches(motionVectors, source.width, source.height))
                {
                    Graphics.Blit(source, destination);
                    FailRuntime("camera motion vectors do not match the FSR color input");
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

                if (!_nativeApi.Execute(_commandBuffer, source, _output, depth, sanitizedMotion,
                    _jitterPixels, _resolveCamera, _effectivePreExposure, in _config,
                    _historyResetPending, out string nativeReason))
                { Graphics.Blit(source, destination); FailRuntime(nativeReason); return; }
                Graphics.Blit(_output, destination);
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
            _nativeApi.Dispose();
            _exposureReader.Dispose();
        }

        // Retain the established native-AA normalization policy for both FSR
        // sizes. This is vendor pre-exposure, not another scene brightness pass.
        internal static void SelectExposure(in Fsr2Config config, bool hasPpv2Exposure,
            float ppv2Exposure, out bool usePpv2, out bool vendorAutoExposure, out float preExposure)
        {
            usePpv2 = config.AutoExposure && config.PreferPpv2Exposure && hasPpv2Exposure &&
                Ppv2ExposureReader.IsUsableExposure(ppv2Exposure);
            vendorAutoExposure = config.AutoExposure && !usePpv2;
            preExposure = usePpv2 ? Mathf.Clamp(ppv2Exposure, 0.2f, 2.0f) :
                config.AutoExposure ? 1.0f :
                Ppv2ExposureReader.IsUsableExposure(config.PreExposure) ? config.PreExposure : 1.0f;
        }

        private bool EnsureResources(RenderTexture source, bool vendorAutoExposure)
        {
            if (TemporalTextures.IsCreated(_output) && ContextCreated &&
                _output.width == _outputWidth && _output.height == _outputHeight &&
                _resourceWidth == source.width &&
                _resourceHeight == source.height &&
                _resourceGraphicsFormat == source.graphicsFormat &&
                _resourceSrgb == source.sRGB &&
                _nativeApi.ContextUsesAutoExposure == vendorAutoExposure)
            {
                return true;
            }

            ReleaseResources();
            RenderTextureDescriptor descriptor = BuildOutputDescriptor(
                source.descriptor
            );
            descriptor.width = _outputWidth;
            descriptor.height = _outputHeight;
            if (!SystemInfo.IsFormatSupported(
                    descriptor.graphicsFormat,
                    GraphicsFormatUsage.LoadStore))
            {
                _lastFailure = "FSR output format " +
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
                _lastFailure = "FSR output texture creation failed";
                return false;
            }

            _resourceWidth = source.width;
            _resourceHeight = source.height;
            _resourceGraphicsFormat = source.graphicsFormat;
            _resourceSrgb = source.sRGB;
            if (!_nativeApi.TryCreateContext(_commandBuffer, source.width, source.height,
                _outputWidth, _outputHeight, vendorAutoExposure, out string reason))
            {
                TemporalTextures.Release(ref _output);
                _lastFailure = _providerName + " context creation failed: " + reason;
                return false;
            }

            _estimatedMemoryBytes = (long)_outputWidth * _outputHeight * 8;
            _historyResetPending = true;
            _logger.LogInfo(
                "[ReduxBetterAA/FSR] " + Id + " context created for " +
                source.width + "x" + source.height + " -> " + _outputWidth + "x" + _outputHeight +
                ", SDK provider " + AmdFsrNativeApi.ProviderName + ", output " + _output.graphicsFormat +
                " (random-write); exposure: " + ExposureSource + "."
            );
            return true;
        }

        internal static RenderTextureDescriptor BuildOutputDescriptor(RenderTextureDescriptor source)
        {
            RenderTextureDescriptor output = TemporalTextures.VendorOutputDescriptor(source);
            output.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            return output;
        }

        private void ReleaseResources()
        {
            if (_commandBuffer != null)
            {
                _nativeApi.DestroyContext(_commandBuffer);
            }
            TemporalTextures.Release(ref _output);
            _resourceWidth = 0;
            _resourceHeight = 0;
            _resourceGraphicsFormat = GraphicsFormat.None;
            _estimatedMemoryBytes = 0;
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
                _jitterPixels = _projectionJitterSupported
                    ? Jitter
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
            state.Apply(camera, Jitter,
                jitterTransparentRendering: _jitterTransparentRendering);
        }

        private void FailRuntime(string reason)
        {
            if (_runtimeFailureLatched)
            {
                return;
            }
            _runtimeFailureLatched = true;
            _lastFailure = string.IsNullOrEmpty(reason)
                ? "unknown FSR execution failure"
                : reason;
            _active = false;
            _logger.LogError(
                "[ReduxBetterAA/FSR] " + Id + " disabled after a runtime failure: " +
                _lastFailure + "."
            );
            _runtimeFailure?.Invoke(_lastFailure);
        }

    }
}
