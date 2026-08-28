using System;
using System.IO;
using Newtonsoft.Json;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Diagnostics
{
    internal enum BufferDebugView
    {
        Off = -1,
        FinalColor = 0,
        LinearDepth = 1,
        MotionVectorsRaw = 2,
        MotionVectorsNormalized = 3,
        MotionVectorsMagnitudeAngle = 4,
        ContributionMask = 5,
        MotionVectorsValidity = 6,
        MotionSignAgreement = 7,
        SanitizedVendorMotion = 8,
        MotionSanitizerDecision = 9,
        DeJitteredLinearDepth = 10,
        MotionVectorsBuiltinPreviousVP = 11,
        MotionVectorsPerPassPreviousVP = 12,
        MotionVectorsManagedPreviousVP = 13,
        MotionSignReferenceAudit = 14
    }

    internal sealed class BufferVisualizer : IDisposable
    {
        private const string ShaderAddress =
            "Assets/ReduxBetterAA/Shaders/Phase1BufferDebug.shader";
        private const string StatisticsShaderAddress =
            "Assets/ReduxBetterAA/Shaders/Phase1MotionStatistics.shader";
        private const string MotionVectorPassProbeShaderAddress =
            "Assets/ReduxBetterAA/Shaders/Phase1MotionVectorPassProbe.shader";
        private const string CommandBufferName = "Redux Better AA Phase 1 Debug View";
        private const int MaximumStatisticsDimension = 320;
        private const float MotionQuietPixels = 0.1f;
        private const float MotionOutlierPixels = 64.0f;
        private const float MotionSignConfidencePixels = 0.75f;
        private const int WindowId = 0x52424131;
        private static readonly Rect OverlayRect = new Rect(12f, 12f, 720f, 32f);
        private static readonly string[] ViewLabels =
        {
            "Off",
            "Final Color",
            "Linear Depth (raw jittered)",
            "Motion Vectors: Raw",
            "Motion Vectors: Normalized",
            "Motion Vectors: Magnitude / Angle",
            "Camera Contribution Mask",
            "Motion Validity / Magnitude",
            "Motion: Raw Sign Agreement",
            "Motion: Sanitized Vendor Input",
            "Motion: Sanitizer Decision",
            "Linear Depth (jitter-compensated sample)",
            "Motion: unity_MatrixPreviousVP reconstruction",
            "Motion: _PrevViewProjMatrix reconstruction",
            "Motion: Camera.previousViewProjectionMatrix reconstruction",
            "Motion: Sign Reference Orientation Audit"
        };
        private static readonly string[] PanelTabs =
            {
                "Off",
                "FXAA Low",
                "FXAA High",
                "SMAA",
                "PPv2",
                "Custom",
                "DLAA",
                "FSR2 AA",
                "Buffers"
            };
        private static readonly string[] DlaaPresetLabels =
            { "F", "J", "K", "L", "M" };
        private static readonly string[] CustomDebugLabels =
        {
            "Final resolve",
            "Current color",
            "History color",
            "Reprojected history",
            "Depth rejection",
            "Reactive mask",
            "History weight",
            "Clamp extent",
            "Motion vectors",
            "Depth edges"
        };
        private static readonly BufferDebugView[] MotionDiagnosticBurstViews =
        {
            BufferDebugView.MotionVectorsRaw,
            BufferDebugView.MotionVectorsNormalized,
            BufferDebugView.MotionVectorsValidity,
            BufferDebugView.MotionSignAgreement,
            BufferDebugView.SanitizedVendorMotion,
            BufferDebugView.MotionSanitizerDecision
        };
        private static readonly int TemporaryTarget =
            Shader.PropertyToID("_ReduxBetterAAPhase1Temporary");
        private static readonly int DiagnosticPixelDimensions =
            Shader.PropertyToID("_DiagnosticPixelDimensions");
        private static readonly int MotionQuietPixelsProperty =
            Shader.PropertyToID("_MotionQuietPixels");
        private static readonly int MotionOutlierPixelsProperty =
            Shader.PropertyToID("_MotionOutlierPixels");
        private static readonly int MotionSignConfidencePixelsProperty =
            Shader.PropertyToID("_MotionSignConfidencePixels");
        private static readonly int MotionComponentSignProperty =
            Shader.PropertyToID("_MotionComponentSign");
        private static readonly int SanitizedMotionComponentSignProperty =
            Shader.PropertyToID("_SanitizedMotionComponentSign");
        private static readonly int CurrentInverseViewProjectionProperty =
            Shader.PropertyToID("_CurrentInverseViewProjection");
        private static readonly int PreviousViewProjectionProperty =
            Shader.PropertyToID("_PreviousViewProjection");
        private static readonly int ManagedPreviousViewProjectionProperty =
            Shader.PropertyToID("_ManagedPreviousViewProjection");
        private static readonly int CurrentInverseViewProjectionScreenProperty =
            Shader.PropertyToID("_CurrentInverseViewProjectionScreen");
        private static readonly int PreviousViewProjectionScreenProperty =
            Shader.PropertyToID("_PreviousViewProjectionScreen");
        private static readonly int
            CurrentInverseViewProjectionRenderTextureProperty =
                Shader.PropertyToID(
                    "_CurrentInverseViewProjectionRenderTexture"
                );
        private static readonly int PreviousViewProjectionRenderTextureProperty =
            Shader.PropertyToID("_PreviousViewProjectionRenderTexture");
        private static readonly int MatrixHistoryValidProperty =
            Shader.PropertyToID("_MatrixHistoryValid");
        private static readonly int SanitizedMotionTextureProperty =
            Shader.PropertyToID("_SanitizedMotionTexture");
        private static readonly int MotionCorruptionTextureProperty =
            Shader.PropertyToID("_MotionCorruptionTexture");
        private static readonly int MotionSanitizerAvailableProperty =
            Shader.PropertyToID("_MotionSanitizerAvailable");
        private static readonly int CurrentJitterProperty =
            Shader.PropertyToID("_CurrentJitter");
        private static readonly int MainTextureTexelSizeProperty =
            Shader.PropertyToID("_MainTex_TexelSize");
        private static readonly int MotionTextureTexelSizeProperty =
            Shader.PropertyToID("_CameraMotionVectorsTexture_TexelSize");
        private static readonly int DepthTextureTexelSizeProperty =
            Shader.PropertyToID("_CameraDepthTexture_TexelSize");
        private static readonly int MotionVectorPassProbeModeProperty =
            Shader.PropertyToID("_ReduxBetterAAMotionProbeMode");
        private static readonly int MotionVectorPassManagedPreviousProperty =
            Shader.PropertyToID("_ReduxBetterAAManagedPreviousVP");

        private readonly ReduxLogger _logger;
        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderHandleValid;
        private Shader _shader;
        private Material _material;
        private AsyncOperationHandle<Shader> _statisticsShaderHandle;
        private bool _statisticsShaderHandleValid;
        private Shader _statisticsShader;
        private Material _statisticsMaterial;
        private AsyncOperationHandle<Shader> _motionVectorPassProbeShaderHandle;
        private bool _motionVectorPassProbeShaderHandleValid;
        private Shader _motionVectorPassProbeShader;
        private Shader _originalMotionVectorShader;
        private BuiltinShaderMode _originalMotionVectorShaderMode;
        private bool _motionVectorPassProbeActive;
        private int _motionVectorPassProbeMode;
        private CommandBuffer _commandBuffer;
        private CameraEvent _attachedCameraEvent = CameraEvent.AfterEverything;
        private RenderTexture _statisticsTarget;
        private RenderTexture _statisticsReadbackTarget;
        private Camera[] _candidates = Array.Empty<Camera>();
        private string[] _candidateLabels = Array.Empty<string>();
        private int _candidateIndex = -1;
        private Camera _attachedCamera;
        private DepthTextureMode _originalDepthTextureMode;
        private BufferDebugView _view = BufferDebugView.Off;
        private string _overlayText = string.Empty;
        private bool _disposed;
        private bool _panelOpen;
        private bool _cameraRefreshRequested;
        private float _nextCameraRefresh;
        private bool _reportRequested;
        private bool _screenshotRequested;
        private bool _statisticsCaptureArmed;
        private bool _statisticsReadbackPending;
        private bool _releaseReadbackTargetOnCompletion;
        private MotionVectorStatisticsReport _pendingStatisticsReport;
        private string _statisticsOutputPath;
        private bool _panelSuspendedForScreenshot;
        private bool _motionDiagnosticBurstActive;
        private bool _motionDiagnosticBurstSettling;
        private bool _motionDiagnosticBurstPanelWasOpen;
        private int _motionDiagnosticBurstStep;
        private float _motionDiagnosticBurstNextTime;
        private BufferDebugView _motionDiagnosticBurstOriginalView;
        private string _screenshotStatus =
            "Screenshots are saved under diagnostics/screenshots.";
        private Rect _windowRect = new Rect(24f, 60f, 620f, 760f);
        private Vector2 _panelContentScroll;
        private Vector2 _cameraScroll;
        private Vector2 _customScroll;
        private Vector2 _dlaaScroll;
        private Vector2 _fsr2Scroll;
        private Vector2 _bufferScroll;
        private readonly GUI.WindowFunction _drawWindow;
        private bool _cursorStateCaptured;
        private CursorLockMode _previousCursorLockMode;
        private bool _previousCursorVisible;
        private EventSystem _suppressedEventSystem;
        private bool _eventSystemWasEnabled;
        private Func<string> _temporalStatus;
        private Func<BackendSelection> _requestedBackend;
        private Action<BackendSelection> _setRequestedBackend;
        private Func<TemporalBackendConfig> _ppv2Config;
        private Action<TemporalBackendConfig> _setPpv2Config;
        private Action _restorePpv2Preset;
        private Func<CustomTaaConfig> _customConfig;
        private Action<CustomTaaConfig> _setCustomConfig;
        private Action _restoreCustomPreset;
        private Func<long> _customMemoryBytes;
        private Func<DlaaConfig> _dlaaConfig;
        private Action<DlaaConfig> _setDlaaConfig;
        private Action _restoreDlaaPreset;
        private Func<string> _dlaaDetails;
        private Func<long> _dlaaMemoryBytes;
        private Func<Fsr2Config> _fsr2Config;
        private Action<Fsr2Config> _setFsr2Config;
        private Action _restoreFsr2Preset;
        private Func<string> _fsr2Details;
        private Func<long> _fsr2MemoryBytes;
        private Func<BackendSelection, PerformanceProfileSnapshot>
            _performanceProfile;
        private Action<BackendSelection> _startPerformanceProfile;
        private Action _cancelPerformanceProfile;
        private Action _resetTemporalHistory;
        private Func<bool> _mapViewAaEnabled;
        private Action<bool> _setMapViewAaEnabled;
        private Func<Texture> _sanitizedMotionTexture;
        private Func<Texture> _motionCorruptionTexture;
        private Func<Vector2> _currentJitterNormalized;
        private Func<bool> _vegetationMotionRepairEnabled;
        private Action<bool> _setVegetationMotionRepairEnabled;
        private Func<string> _vegetationMotionRepairStatus;
        private Func<long> _vegetationMotionRepairReroutedCalls;
        private Func<bool> _motionSanitizerEnabled;
        private Action<bool> _setMotionSanitizerEnabled;
        private Func<string> _motionSanitizerStatus;
        private Func<bool> _physicsInterpolationEnabled;
        private Action<bool> _setPhysicsInterpolationEnabled;
        private Func<string> _physicsInterpolationStatus;
        private Action _refreshPhysicsInterpolation;
        private int _panelTab;
        private BackendSelection _lastObservedBackend = (BackendSelection)(-1);
        private Matrix4x4 _currentViewProjection;
        private Matrix4x4 _currentInverseViewProjection;
        private Matrix4x4 _previousViewProjection;
        private Matrix4x4 _currentViewProjectionScreen;
        private Matrix4x4 _currentInverseViewProjectionScreen;
        private Matrix4x4 _previousViewProjectionScreen;
        private Matrix4x4 _currentViewProjectionRenderTexture;
        private Matrix4x4 _currentInverseViewProjectionRenderTexture;
        private Matrix4x4 _previousViewProjectionRenderTexture;
        private bool _currentMatrixValid;
        private bool _matrixHistoryValid;

        public BufferVisualizer(ReduxLogger logger)
        {
            _logger = logger;
            _drawWindow = DrawWindow;
        }

        public bool Active => _view != BufferDebugView.Off;
        public bool MotionStatisticsEnabled => _view == BufferDebugView.MotionVectorsValidity;
        public string OverlayText => _overlayText;

        public void Initialize()
        {
            _shaderHandle = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
            _shaderHandleValid = true;
            _shaderHandle.Completed += OnShaderLoaded;
            _statisticsShaderHandle = Addressables.LoadAssetAsync<Shader>(
                StatisticsShaderAddress
            );
            _statisticsShaderHandleValid = true;
            _statisticsShaderHandle.Completed += OnStatisticsShaderLoaded;
            _motionVectorPassProbeShaderHandle =
                Addressables.LoadAssetAsync<Shader>(
                    MotionVectorPassProbeShaderAddress
                );
            _motionVectorPassProbeShaderHandleValid = true;
            _motionVectorPassProbeShaderHandle.Completed +=
                OnMotionVectorPassProbeShaderLoaded;
        }

        /// <summary>
        /// Installs the diagnostic copy of Unity's built-in motion-vector shader.
        /// TestHarness is the only caller; production backends never use it.
        /// </summary>
        public bool TrySetMotionVectorPassProbe(int mode, out string reason)
        {
            reason = string.Empty;
            if (_motionVectorPassProbeShader == null ||
                !_motionVectorPassProbeShader.isSupported)
            {
                reason = "the motion-vector pass probe shader is not loaded";
                return false;
            }
            if (mode < 0 || mode > 18)
            {
                reason = "the motion-vector pass probe mode must be 0 through 18";
                return false;
            }

            if (!_motionVectorPassProbeActive)
            {
                _originalMotionVectorShader = GraphicsSettings.GetCustomShader(
                    BuiltinShaderType.MotionVectors
                );
                _originalMotionVectorShaderMode = GraphicsSettings.GetShaderMode(
                    BuiltinShaderType.MotionVectors
                );
                _motionVectorPassProbeActive = true;
            }
            _motionVectorPassProbeMode = mode;
            UpdateMotionVectorPassProbeGlobals(GetSelectedCamera());
            GraphicsSettings.SetCustomShader(
                BuiltinShaderType.MotionVectors,
                _motionVectorPassProbeShader
            );
            GraphicsSettings.SetShaderMode(
                BuiltinShaderType.MotionVectors,
                BuiltinShaderMode.UseCustom
            );
            VegetationMotionCompatibility.Current?
                .SetDiagnosticMotionVectorOverrideActive(true);
            _logger.LogInfo(
                "[ReduxBetterAA/Probe] Installed built-in motion-vector pass probe mode " +
                mode + "."
            );
            return true;
        }

        public void RestoreMotionVectorPassProbe()
        {
            if (!_motionVectorPassProbeActive)
            {
                return;
            }
            GraphicsSettings.SetCustomShader(
                BuiltinShaderType.MotionVectors,
                _originalMotionVectorShader
            );
            GraphicsSettings.SetShaderMode(
                BuiltinShaderType.MotionVectors,
                _originalMotionVectorShaderMode
            );
            Shader.SetGlobalInt(MotionVectorPassProbeModeProperty, 0);
            _motionVectorPassProbeActive = false;
            _motionVectorPassProbeMode = 0;
            _originalMotionVectorShader = null;
            VegetationMotionCompatibility.Current?
                .SetDiagnosticMotionVectorOverrideActive(false);
            _logger.LogInfo(
                "[ReduxBetterAA/Probe] Restored the original built-in motion-vector shader."
            );
        }

        public void SetTemporalControls(
            Func<string> status,
            Func<BackendSelection> requestedBackend,
            Action<BackendSelection> setRequestedBackend,
            Func<TemporalBackendConfig> ppv2Config,
            Action<TemporalBackendConfig> setPpv2Config,
            Action restorePpv2Preset,
            Func<CustomTaaConfig> customConfig,
            Action<CustomTaaConfig> setCustomConfig,
            Action restoreCustomPreset,
            Func<long> customMemoryBytes,
            Func<DlaaConfig> dlaaConfig,
            Action<DlaaConfig> setDlaaConfig,
            Action restoreDlaaPreset,
            Func<string> dlaaDetails,
            Func<long> dlaaMemoryBytes,
            Func<Fsr2Config> fsr2Config,
            Action<Fsr2Config> setFsr2Config,
            Action restoreFsr2Preset,
            Func<string> fsr2Details,
            Func<long> fsr2MemoryBytes,
            Func<BackendSelection, PerformanceProfileSnapshot> performanceProfile,
            Action<BackendSelection> startPerformanceProfile,
            Action cancelPerformanceProfile,
            Action resetHistory,
            Func<bool> mapViewAaEnabled,
            Action<bool> setMapViewAaEnabled)
        {
            _temporalStatus = status;
            _requestedBackend = requestedBackend;
            _setRequestedBackend = setRequestedBackend;
            _ppv2Config = ppv2Config;
            _setPpv2Config = setPpv2Config;
            _restorePpv2Preset = restorePpv2Preset;
            _customConfig = customConfig;
            _setCustomConfig = setCustomConfig;
            _restoreCustomPreset = restoreCustomPreset;
            _customMemoryBytes = customMemoryBytes;
            _dlaaConfig = dlaaConfig;
            _setDlaaConfig = setDlaaConfig;
            _restoreDlaaPreset = restoreDlaaPreset;
            _dlaaDetails = dlaaDetails;
            _dlaaMemoryBytes = dlaaMemoryBytes;
            _fsr2Config = fsr2Config;
            _setFsr2Config = setFsr2Config;
            _restoreFsr2Preset = restoreFsr2Preset;
            _fsr2Details = fsr2Details;
            _fsr2MemoryBytes = fsr2MemoryBytes;
            _performanceProfile = performanceProfile;
            _startPerformanceProfile = startPerformanceProfile;
            _cancelPerformanceProfile = cancelPerformanceProfile;
            _resetTemporalHistory = resetHistory;
            _mapViewAaEnabled = mapViewAaEnabled;
            _setMapViewAaEnabled = setMapViewAaEnabled;
        }

        public void SetMotionCadenceControls(
            Func<bool> interpolationEnabled,
            Action<bool> setInterpolationEnabled,
            Func<string> interpolationStatus,
            Action refreshInterpolation)
        {
            _physicsInterpolationEnabled = interpolationEnabled;
            _setPhysicsInterpolationEnabled = setInterpolationEnabled;
            _physicsInterpolationStatus = interpolationStatus;
            _refreshPhysicsInterpolation = refreshInterpolation;
        }

        public void SetMotionSanitizerDiagnostics(
            Func<Texture> sanitizedMotion,
            Func<Texture> corruptionFlag,
            Func<Vector2> currentJitterNormalized)
        {
            _sanitizedMotionTexture = sanitizedMotion;
            _motionCorruptionTexture = corruptionFlag;
            _currentJitterNormalized = currentJitterNormalized;
            UpdateMotionSanitizerMaterial();
            UpdateDepthDiagnosticMaterial();
        }

        public void SetMotionInputControls(
            Func<bool> vegetationRepairEnabled,
            Action<bool> setVegetationRepairEnabled,
            Func<string> vegetationRepairStatus,
            Func<long> vegetationRepairReroutedCalls,
            Func<bool> sanitizerEnabled,
            Action<bool> setSanitizerEnabled,
            Func<string> sanitizerStatus)
        {
            _vegetationMotionRepairEnabled = vegetationRepairEnabled;
            _setVegetationMotionRepairEnabled = setVegetationRepairEnabled;
            _vegetationMotionRepairStatus = vegetationRepairStatus;
            _vegetationMotionRepairReroutedCalls =
                vegetationRepairReroutedCalls;
            _motionSanitizerEnabled = sanitizerEnabled;
            _setMotionSanitizerEnabled = setSanitizerEnabled;
            _motionSanitizerStatus = sanitizerStatus;
        }

        public void SetCandidates(Camera[] candidates)
        {
            candidates = candidates ?? Array.Empty<Camera>();
            if (CandidatesEqual(_candidates, candidates))
            {
                return;
            }

            Camera selected = GetSelectedCamera();
            bool reattach = Active;
            if (reattach)
            {
                Detach();
            }

            _candidates = candidates;
            _candidateLabels = BuildCandidateLabels(_candidates);
            _candidateIndex = FindCandidateIndex(selected);
            if (_candidateIndex < 0 && _candidates.Length > 0)
            {
                _candidateIndex = _candidates.Length - 1;
            }

            if (reattach)
            {
                AttachSelected();
            }
        }

        public void TogglePanel()
        {
            _panelOpen = !_panelOpen;
            if (_panelOpen)
            {
                CapturePanelInputState();
                _cameraRefreshRequested = true;
            }
            else
            {
                RestorePanelInputState();
            }
        }

        public bool ConsumeReportRequest()
        {
            if (!_reportRequested)
            {
                return false;
            }

            _reportRequested = false;
            return true;
        }

        public bool ConsumeCameraRefreshRequest(float now)
        {
            if (!_panelOpen)
            {
                return false;
            }

            bool buffersSelected = _panelTab == PanelTabs.Length - 1;
            if (!_cameraRefreshRequested &&
                (!buffersSelected || now < _nextCameraRefresh))
            {
                return false;
            }

            _cameraRefreshRequested = false;
            _nextCameraRefresh = now + 1.0f;
            return true;
        }

        public bool ConsumeScreenshotRequest()
        {
            if (!_screenshotRequested)
            {
                return false;
            }

            _screenshotRequested = false;
            return true;
        }

        public bool RequestScreenshot()
        {
            if (_screenshotRequested)
            {
                return false;
            }
            if (MotionStatisticsEnabled &&
                (_statisticsCaptureArmed || _statisticsReadbackPending))
            {
                _screenshotStatus =
                    "Wait for the current motion statistics report before capturing again.";
                return false;
            }

            _screenshotRequested = true;
            _screenshotStatus = MotionStatisticsEnabled
                ? "Screenshot and motion statistics queued..."
                : "Screenshot queued...";
            return true;
        }

        public bool RequestMotionDiagnosticBurst()
        {
            if (_motionDiagnosticBurstActive || GetSelectedCamera() == null)
            {
                return false;
            }

            _motionDiagnosticBurstActive = true;
            _motionDiagnosticBurstSettling = false;
            _motionDiagnosticBurstStep = 0;
            _motionDiagnosticBurstNextTime = Time.unscaledTime + 1.0f;
            _motionDiagnosticBurstOriginalView = _view;
            _motionDiagnosticBurstPanelWasOpen = _panelOpen;
            if (_panelOpen)
            {
                _panelOpen = false;
                RestorePanelInputState();
            }
            _screenshotStatus =
                "Motion diagnosis armed: begin a smooth horizontal and vertical pan.";
            return true;
        }

        public void TickMotionDiagnosticBurst(float now)
        {
            if (!_motionDiagnosticBurstActive || now < _motionDiagnosticBurstNextTime ||
                _screenshotRequested || _statisticsCaptureArmed ||
                _statisticsReadbackPending)
            {
                return;
            }

            if (_motionDiagnosticBurstStep >= MotionDiagnosticBurstViews.Length)
            {
                SetView(_motionDiagnosticBurstOriginalView);
                _motionDiagnosticBurstActive = false;
                _motionDiagnosticBurstSettling = false;
                _screenshotStatus =
                    "Motion diagnosis burst complete: six screenshots and reports saved.";
                if (_motionDiagnosticBurstPanelWasOpen)
                {
                    _panelOpen = true;
                    CapturePanelInputState();
                }
                return;
            }

            if (!_motionDiagnosticBurstSettling)
            {
                SetView(MotionDiagnosticBurstViews[_motionDiagnosticBurstStep]);
                _motionDiagnosticBurstSettling = true;
                _motionDiagnosticBurstNextTime = now + 0.25f;
                return;
            }

            if (RequestScreenshot())
            {
                _motionDiagnosticBurstStep++;
                _motionDiagnosticBurstSettling = false;
                _motionDiagnosticBurstNextTime = now + 0.35f;
            }
        }

        public bool TryArmMotionStatistics(
            string outputPath,
            string screenshotFileName,
            out string unavailableReason)
        {
            unavailableReason = string.Empty;
            if (!MotionStatisticsEnabled)
            {
                unavailableReason = "select Motion Validity / Magnitude";
                return false;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                unavailableReason = "asynchronous GPU readback is unsupported";
                return false;
            }
            if (_statisticsTarget == null || !_statisticsTarget.IsCreated())
            {
                unavailableReason = "the statistics sample target is unavailable";
                return false;
            }
            if (_statisticsCaptureArmed || _statisticsReadbackPending)
            {
                unavailableReason = "a motion statistics readback is already pending";
                return false;
            }

            Camera camera = GetSelectedCamera();
            if (camera == null)
            {
                unavailableReason = "the selected camera is unavailable";
                return false;
            }

            int sourceWidth;
            int sourceHeight;
            GetCameraDimensions(camera, out sourceWidth, out sourceHeight);
            _pendingStatisticsReport = new MotionVectorStatisticsReport
            {
                schemaVersion = 3,
                capturedUtc = DateTime.UtcNow.ToString("O"),
                screenshotFile = screenshotFileName,
                view = CurrentViewName,
                camera = camera.name,
                sourceWidth = sourceWidth,
                sourceHeight = sourceHeight,
                sampleWidth = _statisticsTarget.width,
                sampleHeight = _statisticsTarget.height,
                quietThresholdPixels = MotionQuietPixels,
                outlierThresholdPixels = MotionOutlierPixels,
                fixedDeltaTimeMilliseconds = Time.fixedDeltaTime * 1000.0f,
                fixedUpdateHz = Time.fixedDeltaTime > 0.0f
                    ? 1.0f / Time.fixedDeltaTime
                    : 0.0f,
                experimentalRenderInterpolationEnabled =
                    KspPhysicsRenderInterpolation.Current != null &&
                    KspPhysicsRenderInterpolation.Current.Enabled,
                interpolatedKspPhysicsBodies =
                    KspPhysicsRenderInterpolation.Current == null
                        ? 0
                        : KspPhysicsRenderInterpolation.Current.TrackedBodyCount,
                interpolationStatus =
                    KspPhysicsRenderInterpolation.Current == null
                        ? "Unavailable"
                        : KspPhysicsRenderInterpolation.Current.Status,
                samplingNote =
                    "Uniform point-sampled diagnostic grid; coverage counts approximate screen area. " +
                    "The 16 anchors match the same-frame corruption classifier."
            };
            _statisticsOutputPath = outputPath;
            _statisticsCaptureArmed = true;
            return true;
        }

        public void SuspendPanelForScreenshot()
        {
            if (!_panelOpen)
            {
                return;
            }

            _panelOpen = false;
            _panelSuspendedForScreenshot = true;
        }

        public void ResumePanelAfterScreenshot()
        {
            if (!_panelSuspendedForScreenshot)
            {
                return;
            }

            _panelSuspendedForScreenshot = false;
            _panelOpen = true;
            UnlockCursor();
        }

        public void SetScreenshotStatus(string status)
        {
            _screenshotStatus = status;
        }

        public string CurrentViewName => ViewLabels[(int)_view + 1];

        public string SelectedCameraName
        {
            get
            {
                Camera camera = GetSelectedCamera();
                return camera == null ? "NoCamera" : camera.name;
            }
        }

        internal Camera SelectedCameraForDiagnostics => GetSelectedCamera();

        public MotionSignDiagnosticRecord CaptureMotionSignDiagnostic()
        {
            Camera camera = GetSelectedCamera();
            bool invertX;
            bool invertY;
            BackendSelection backend;
            GetConfiguredMotionInversion(out invertX, out invertY, out backend);
            Vector4 motionTexelSize = Shader.GetGlobalVector(
                MotionTextureTexelSizeProperty
            );
            Vector4 depthTexelSize = Shader.GetGlobalVector(
                DepthTextureTexelSizeProperty
            );
            Vector4 mainTexelSize = _material == null
                ? Vector4.zero
                : _material.GetVector(MainTextureTexelSizeProperty);
            if (camera == null)
            {
                return new MotionSignDiagnosticRecord
                {
                    view = CurrentViewName,
                    selectedCamera = "NoCamera",
                    cameraAvailable = false,
                    configuredInvertX = invertX,
                    configuredInvertY = invertY,
                    configuredBackend = backend.ToString(),
                    graphicsUvStartsAtTop = SystemInfo.graphicsUVStartsAtTop,
                    motionTextureTexelSize = VectorValues(motionTexelSize),
                    depthTextureTexelSize = VectorValues(depthTexelSize),
                    materialMainTextureTexelSize = VectorValues(mainTexelSize),
                    unityMotionConvention =
                        "current-minus-previous in Unity motion-texture UV axes",
                    vendorMotionConvention =
                        "current pixel to previous pixel; negate Unity motion",
                    referencePolicy =
                        "No selected camera; reference projection unavailable.",
                    texelSizeTelemetryNote =
                        "Motion/depth values are Unity global companion vectors. " +
                        "MainTex is material state only; the command-buffer blit " +
                        "may override it at execution time."
                };
            }

            Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
            Matrix4x4 screenProjection = GL.GetGPUProjectionMatrix(
                projection,
                false
            );
            Matrix4x4 renderTextureProjection = GL.GetGPUProjectionMatrix(
                projection,
                true
            );
            bool usesRenderTextureProjection =
                MotionSignDiagnosticPolicy.UseRenderTextureProjection(
                    camera.targetTexture != null,
                    camera.forceIntoRenderTexture
                );
            return new MotionSignDiagnosticRecord
            {
                view = CurrentViewName,
                selectedCamera = camera.name,
                cameraAvailable = true,
                targetTexturePresent = camera.targetTexture != null,
                forceIntoRenderTexture = camera.forceIntoRenderTexture,
                automaticReferenceUsesRenderTextureProjection =
                    usesRenderTextureProjection,
                graphicsUvStartsAtTop = SystemInfo.graphicsUVStartsAtTop,
                cameraProjectionYScale = projection.m11,
                screenGpuProjectionYScale = screenProjection.m11,
                renderTextureGpuProjectionYScale = renderTextureProjection.m11,
                motionTextureTexelSize = VectorValues(motionTexelSize),
                depthTextureTexelSize = VectorValues(depthTexelSize),
                materialMainTextureTexelSize = VectorValues(mainTexelSize),
                configuredInvertX = invertX,
                configuredInvertY = invertY,
                configuredBackend = backend.ToString(),
                unityMotionConvention =
                    "current-minus-previous in Unity motion-texture UV axes",
                vendorMotionConvention =
                    "current pixel to previous pixel; negate Unity motion",
                referencePolicy =
                    "Use render-texture GPU projection when targetTexture is set " +
                    "or forceIntoRenderTexture is true; independently orient " +
                    "motion and depth samples from their own texel sizes; mirror " +
                    "Unity's explicit top-origin Y conversion.",
                texelSizeTelemetryNote =
                    "Motion/depth values are Unity global companion vectors. " +
                    "MainTex is material state only; the command-buffer blit " +
                    "may override it at execution time."
            };
        }

        private static float[] VectorValues(Vector4 value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }

        public void DrawGui()
        {
            if (_panelOpen)
            {
                MaintainPanelInputState();
                float maximumWidth = Mathf.Max(260f, Screen.width - 16f);
                float maximumHeight = Mathf.Max(300f, Screen.height - 16f);
                _windowRect.width = Mathf.Min(460f, maximumWidth);
                _windowRect.height = Mathf.Min(760f, maximumHeight);
                ClampWindowToScreen();
                _windowRect = GUI.Window(
                    WindowId,
                    _windowRect,
                    _drawWindow,
                    "Redux Better AA"
                );
                ClampWindowToScreen();
            }
            else if (Active && !_panelSuspendedForScreenshot)
            {
                GUI.Box(OverlayRect, _overlayText);
            }
        }

        private void SelectCamera(int index)
        {
            if (index < 0 || index >= _candidates.Length || index == _candidateIndex)
            {
                return;
            }

            Detach();
            _candidateIndex = index;
            if (Active)
            {
                AttachSelected();
            }
            UpdateOverlayText();
            LogState();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _panelOpen = false;
            _panelSuspendedForScreenshot = false;
            RestorePanelInputState();
            RestoreMotionVectorPassProbe();
            Detach();
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
                _material = null;
            }
            if (_statisticsMaterial != null)
            {
                UnityEngine.Object.Destroy(_statisticsMaterial);
                _statisticsMaterial = null;
            }
            if (_shaderHandleValid)
            {
                _shaderHandle.Completed -= OnShaderLoaded;
                Addressables.Release(_shaderHandle);
                _shaderHandleValid = false;
            }
            if (_statisticsShaderHandleValid)
            {
                _statisticsShaderHandle.Completed -= OnStatisticsShaderLoaded;
                Addressables.Release(_statisticsShaderHandle);
                _statisticsShaderHandleValid = false;
            }
            if (_motionVectorPassProbeShaderHandleValid)
            {
                _motionVectorPassProbeShaderHandle.Completed -=
                    OnMotionVectorPassProbeShaderLoaded;
                Addressables.Release(_motionVectorPassProbeShaderHandle);
                _motionVectorPassProbeShaderHandleValid = false;
            }
            _shader = null;
            _statisticsShader = null;
            _motionVectorPassProbeShader = null;
            _candidates = Array.Empty<Camera>();
            _candidateLabels = Array.Empty<string>();
            _temporalStatus = null;
            _requestedBackend = null;
            _setRequestedBackend = null;
            _ppv2Config = null;
            _setPpv2Config = null;
            _restorePpv2Preset = null;
            _customConfig = null;
            _setCustomConfig = null;
            _restoreCustomPreset = null;
            _customMemoryBytes = null;
            _dlaaConfig = null;
            _setDlaaConfig = null;
            _restoreDlaaPreset = null;
            _dlaaDetails = null;
            _dlaaMemoryBytes = null;
            _fsr2Config = null;
            _setFsr2Config = null;
            _restoreFsr2Preset = null;
            _fsr2Details = null;
            _fsr2MemoryBytes = null;
            _performanceProfile = null;
            _startPerformanceProfile = null;
            _cancelPerformanceProfile = null;
            _resetTemporalHistory = null;
            _mapViewAaEnabled = null;
            _setMapViewAaEnabled = null;
            _physicsInterpolationEnabled = null;
            _setPhysicsInterpolationEnabled = null;
            _physicsInterpolationStatus = null;
            _refreshPhysicsInterpolation = null;
            _sanitizedMotionTexture = null;
            _motionCorruptionTexture = null;
            _currentJitterNormalized = null;
            _vegetationMotionRepairEnabled = null;
            _setVegetationMotionRepairEnabled = null;
            _vegetationMotionRepairStatus = null;
            _vegetationMotionRepairReroutedCalls = null;
            _motionSanitizerEnabled = null;
            _setMotionSanitizerEnabled = null;
            _motionSanitizerStatus = null;
        }

        private void SetView(BufferDebugView view)
        {
            Detach();
            _view = view;
            if (Active)
            {
                AttachSelected();
            }
            UpdateOverlayText();
            LogState();
        }

        private void DrawWindow(int windowId)
        {
            BackendSelection requested = _requestedBackend == null
                ? BackendSelection.Off
                : _requestedBackend();
            if (requested != _lastObservedBackend)
            {
                _panelTab = (int)requested;
                _panelContentScroll = Vector2.zero;
                _lastObservedBackend = requested;
            }

            bool previousEnabled = GUI.enabled;
            GUI.enabled = _setRequestedBackend != null;
            int selectedTab = GUILayout.Toolbar(
                _panelTab,
                PanelTabs,
                GUILayout.Height(28f)
            );
            GUI.enabled = previousEnabled;
            if (selectedTab != _panelTab)
            {
                _panelTab = selectedTab;
                _panelContentScroll = Vector2.zero;
                if (selectedTab == PanelTabs.Length - 1)
                {
                    _cameraRefreshRequested = true;
                }
                if (selectedTab >= (int)BackendSelection.Off &&
                    selectedTab <= (int)BackendSelection.AmdFsr2 &&
                    _setRequestedBackend != null)
                {
                    BackendSelection selectedBackend =
                        (BackendSelection)selectedTab;
                    _lastObservedBackend = selectedBackend;
                    _setRequestedBackend(selectedBackend);
                }
            }

            float contentHeight = Mathf.Max(120f, _windowRect.height - 145f);
            _panelContentScroll = GUILayout.BeginScrollView(
                _panelContentScroll,
                GUILayout.Height(contentHeight)
            );
            GUILayout.Label("AA mode and settings (F12 cycles modes)");
            GUILayout.Label(
                _temporalStatus == null
                    ? "Unavailable"
                    : _temporalStatus()
            );
            DrawMapViewAaControl();
            GUILayout.Space(8f);
            if (_panelTab == 0)
            {
                DrawOffTab();
            }
            else if (_panelTab == 1)
            {
                DrawSpatialAaTab(BackendSelection.FxaaLow);
            }
            else if (_panelTab == 2)
            {
                DrawSpatialAaTab(BackendSelection.FxaaHigh);
            }
            else if (_panelTab == 3)
            {
                DrawSpatialAaTab(BackendSelection.Smaa);
            }
            else if (_panelTab == 4)
            {
                DrawPpv2Tab();
            }
            else if (_panelTab == 5)
            {
                DrawCustomTab();
            }
            else if (_panelTab == 6)
            {
                DrawDlaaTab();
            }
            else if (_panelTab == 7)
            {
                DrawFsr2Tab();
            }
            else
            {
                DrawBufferTab();
            }
            if (_panelTab >= (int)BackendSelection.Off &&
                _panelTab <= (int)BackendSelection.AmdFsr2)
            {
                DrawPerformanceProfile((BackendSelection)_panelTab);
            }
            GUILayout.EndScrollView();
            DrawCommonControls();
            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 24f));
        }

        private void DrawMapViewAaControl()
        {
            bool enabled = _mapViewAaEnabled == null || _mapViewAaEnabled();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = _setMapViewAaEnabled != null;
            Color previousColor = GUI.backgroundColor;
            if (enabled)
            {
                GUI.backgroundColor = Color.cyan;
            }
            if (GUILayout.Button(
                    enabled
                        ? "Map-view AA: ON"
                        : "Map-view AA: OFF (flight setting preserved)",
                    GUILayout.Height(28f)))
            {
                _setMapViewAaEnabled(!enabled);
            }
            GUI.backgroundColor = previousColor;
            GUI.enabled = previousEnabled;
            GUILayout.Label(
                "This independently forces AA Off only while map view is active."
            );
        }

        private static void DrawOffTab()
        {
            GUILayout.Label(
                "Redux forces PPv2 anti-aliasing off while this mode is selected, " +
                "providing a true unfiltered baseline. The renderer's prior AA " +
                "state is restored when Redux releases the scene. Choose FXAA " +
                "Low, FXAA High, SMAA, PPv2, Custom, DLAA, or FSR2 AA above to " +
                "enable that mode and open its settings."
            );
        }

        private static void DrawSpatialAaTab(BackendSelection mode)
        {
            switch (mode)
            {
                case BackendSelection.FxaaLow:
                    GUILayout.Label("KSP stock Low / PPv2 FXAA fast mode");
                    GUILayout.Label(
                        "A fast spatial edge filter with no temporal history. " +
                        "This is the exact effect selected by KSP's Low setting."
                    );
                    break;
                case BackendSelection.Smaa:
                    GUILayout.Label("PPv2 SMAA (high quality spatial mode)");
                    GUILayout.Label(
                        "The existing Unity Post Processing Stack SMAA effect, " +
                        "using its shipped High quality preset. It does not use " +
                        "motion vectors or temporal history."
                    );
                    break;
                default:
                    GUILayout.Label("KSP stock High / PPv2 FXAA quality mode");
                    GUILayout.Label(
                        "The higher-quality spatial FXAA variant selected by KSP's " +
                        "High setting. It has no temporal history."
                    );
                    break;
            }
        }

        private void DrawPpv2Tab()
        {
            GUILayout.Label("Phase 2 / PPv2 TAA parameters");

            if (_ppv2Config == null || _setPpv2Config == null)
            {
                GUILayout.Label("PPv2 parameter controls are unavailable.");
                return;
            }

            TemporalBackendConfig config = _ppv2Config();
            float jitterSpread = DrawParameter(
                "Jitter spread",
                config.JitterSpread,
                0.1f,
                1.0f
            );
            float sharpness = DrawParameter(
                "Sharpness",
                config.Sharpness,
                0.0f,
                1.0f
            );
            float stationaryBlending = DrawParameter(
                "Stationary history",
                config.StationaryBlending,
                0.0f,
                0.99f
            );
            float motionBlending = DrawParameter(
                "Moving history",
                config.MotionBlending,
                0.0f,
                0.99f
            );

            var updated = new TemporalBackendConfig(
                jitterSpread,
                sharpness,
                stationaryBlending,
                motionBlending
            );
            if (!config.ValuesEqual(in updated))
            {
                _setPpv2Config(updated);
            }

            GUILayout.Space(10f);
            GUILayout.Label(
                "Changes apply immediately. Shared sharpness is saved; the other " +
                "engineering parameters remain session-only. Each temporal " +
                "parameter change resets history once."
            );
            GUILayout.Label(
                "Launchpad warning: high Moving history values can amplify the " +
                "observed motion-vector spikes."
            );
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Conservative preset", GUILayout.Height(28f)))
            {
                _restorePpv2Preset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && _resetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                _resetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawCustomTab()
        {
            GUILayout.Label("Phase 3 / project-owned custom TAA");
            if (_customConfig == null || _setCustomConfig == null)
            {
                GUILayout.Label("Custom TAA parameter controls are unavailable.");
                return;
            }

            _customScroll = GUILayout.BeginScrollView(
                _customScroll,
                GUILayout.Height(370f)
            );
            CustomTaaConfig config = _customConfig();
            float jitterSpread = DrawParameter(
                "Jitter spread", config.JitterSpread, 0.1f, 1.5f
            );
            int sequenceLength = Mathf.RoundToInt(DrawParameter(
                "Jitter sequence", config.SequenceLength, 4.0f, 32.0f
            ));
            float stationaryHistory = DrawParameter(
                "Stationary history", config.StationaryHistory, 0.0f, 0.99f
            );
            float movingHistory = DrawParameter(
                "Moving history", config.MovingHistory, 0.0f, 0.99f
            );
            float motionResponsePixels = DrawParameter(
                "Motion response (px)", config.MotionResponsePixels, 0.5f, 64.0f
            );
            float maximumMotionPixels = DrawParameter(
                "Reject motion above (px)", config.MaximumMotionPixels, 8.0f, 512.0f
            );
            float depthThreshold = DrawParameter(
                "Surface/depth threshold", config.DepthThreshold, 0.0001f, 0.1f
            );
            float depthEdgeStability = DrawParameter(
                "Depth-edge stability", config.DepthEdgeStability, 0.0f, 1.0f
            );
            float varianceGamma = DrawParameter(
                "Variance clip gamma", config.VarianceGamma, 0.5f, 3.0f
            );
            float reactiveScale = DrawParameter(
                "Inferred reactive scale", config.ReactiveScale, 0.0f, 10.0f
            );
            float sharpening = DrawParameter(
                "Sharpening", config.Sharpening, 0.0f, 1.0f
            );
            float noDepthHistory = DrawParameter(
                "No-depth history cap", config.NoDepthHistory, 0.0f, 0.99f
            );

            GUILayout.Space(6f);
            GUILayout.Label("Custom resolve debug output");
            int debugMode = GUILayout.SelectionGrid(
                (int)config.DebugView,
                CustomDebugLabels,
                2,
                GUILayout.Height(112f)
            );

            var updated = new CustomTaaConfig(
                jitterSpread,
                sequenceLength,
                stationaryHistory,
                movingHistory,
                motionResponsePixels,
                maximumMotionPixels,
                depthThreshold,
                depthEdgeStability,
                varianceGamma,
                reactiveScale,
                sharpening,
                noDepthHistory,
                (CustomTaaDebugView)debugMode
            );
            if (!config.ValuesEqual(in updated))
            {
                _setCustomConfig(updated);
            }
            GUILayout.EndScrollView();

            long bytes = _customMemoryBytes == null ? 0 : _customMemoryBytes();
            GUILayout.Label(
                bytes > 0
                    ? "Allocated custom history: " +
                      (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
                    : "Custom history is allocated when the backend first renders."
            );
            GUILayout.Label(
                "Motion above the configured limit is rejected, including the " +
                "launchpad outliers observed in Phase 1."
            );
            GUILayout.Label(
                "Depth-edge stability filters the clamp and depth match to the " +
                "current surface; set it to 0 for the legacy edge path."
            );
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Conservative preset", GUILayout.Height(28f)))
            {
                _restoreCustomPreset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && _resetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                _resetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        private bool IsTemporalBackendActive()
        {
            return _requestedBackend != null &&
                   _requestedBackend() != BackendSelection.Off;
        }

        private void DrawDlaaTab()
        {
            GUILayout.Label("Phase 4 / managed Unity NVIDIA DLAA");
            GUILayout.Label(
                _dlaaDetails == null
                    ? "DLAA runtime details are unavailable."
                    : _dlaaDetails()
            );
            if (_dlaaConfig == null || _setDlaaConfig == null)
            {
                GUILayout.Label("DLAA parameter controls are unavailable.");
                return;
            }

            _dlaaScroll = GUILayout.BeginScrollView(
                _dlaaScroll,
                GUILayout.Height(330f)
            );
            DlaaConfig config = _dlaaConfig();
            float jitterSpread = DrawParameter(
                "Jitter spread", config.JitterSpread, 0.1f, 1.5f
            );
            int sequenceLength = Mathf.RoundToInt(DrawParameter(
                "Jitter sequence", config.SequenceLength, 4.0f, 32.0f
            ));
            float sharpness = DrawParameter(
                "DLAA sharpness", config.Sharpness, 0.0f, 1.0f
            );
            float preExposure = DrawParameter(
                "Pre-exposure", config.PreExposure, 0.01f, 16.0f
            );
            bool autoExposure = GUILayout.Toggle(
                config.AutoExposure,
                " Automatic exposure"
            );
            bool preferPpv2Exposure = GUILayout.Toggle(
                config.PreferPpv2Exposure,
                " Prefer game / PPv2 exposure"
            );
            bool invertMotionX = GUILayout.Toggle(
                config.InvertMotionX,
                " Invert motion-vector X in sanitizer"
            );
            bool invertMotionY = GUILayout.Toggle(
                config.InvertMotionY,
                " Invert motion-vector Y in sanitizer"
            );
            bool allowSupersampling = GUILayout.Toggle(
                config.AllowSupersampling,
                " Allow Redux supersampling above 100%"
            );

            GUILayout.Space(6f);
            GUILayout.Label("DLAA preset hint");
            int presetIndex = GUILayout.SelectionGrid(
                DlaaPresetToIndex(config.Preset),
                DlaaPresetLabels,
                3,
                GUILayout.Height(50f)
            );
            var updated = new DlaaConfig(
                jitterSpread,
                sequenceLength,
                sharpness,
                preExposure,
                autoExposure,
                invertMotionX,
                invertMotionY,
                DlaaPresetFromIndex(presetIndex),
                allowSupersampling,
                preferPpv2Exposure
            );
            if (!config.ValuesEqual(in updated))
            {
                _setDlaaConfig(updated);
            }
            GUILayout.EndScrollView();

            long bytes = _dlaaMemoryBytes == null ? 0 : _dlaaMemoryBytes();
            GUILayout.Label(
                bytes > 0
                    ? "Project-owned DLAA output: " +
                      (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
                    : "The DLAA output is allocated when its context first renders."
            );
            GUILayout.Label(
                "Unity Built-in motion is previous-to-current, while DLAA expects " +
                "current-to-previous, so X and Y inversion should both be enabled. " +
                "These are shader transforms; NVIDIA's similarly named fields only " +
                "orient its optional status indicator. A same-frame detector " +
                "replaces screen-wide corruption. Coherent camera pans may exceed " +
                "256 px; invalid, unverified >256 px, or >96 px disagreement uses a " +
                "<=256 px camera fallback. The " +
                "supersampling option runs equal-size DLAA on Redux's larger " +
                "scene buffer before Redux downsamples it."
            );
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Conservative preset", GUILayout.Height(28f)))
            {
                _restoreDlaaPreset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && _resetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                _resetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        private static int DlaaPresetToIndex(DlaaPreset preset)
        {
            switch (preset)
            {
                case DlaaPreset.F:
                    return 0;
                case DlaaPreset.J:
                    return 1;
                case DlaaPreset.K:
                    return 2;
                case DlaaPreset.L:
                    return 3;
                case DlaaPreset.M:
                    return 4;
                default:
                    return 2;
            }
        }

        private static DlaaPreset DlaaPresetFromIndex(int index)
        {
            switch (index)
            {
                case 0:
                    return DlaaPreset.F;
                case 1:
                    return DlaaPreset.J;
                case 2:
                    return DlaaPreset.K;
                case 3:
                    return DlaaPreset.L;
                case 4:
                    return DlaaPreset.M;
                default:
                    return DlaaPreset.M;
            }
        }

        private void DrawFsr2Tab()
        {
            GUILayout.Label("Phase 5 experiment / Unity AMD FSR2 Native AA");
            GUILayout.Label(
                _fsr2Details == null
                    ? "FSR2 runtime details are unavailable."
                    : _fsr2Details()
            );
            if (_fsr2Config == null || _setFsr2Config == null)
            {
                GUILayout.Label("FSR2 parameter controls are unavailable.");
                return;
            }

            _fsr2Scroll = GUILayout.BeginScrollView(
                _fsr2Scroll,
                GUILayout.Height(330f)
            );
            Fsr2Config config = _fsr2Config();
            float jitterSpread = DrawParameter(
                "Jitter spread", config.JitterSpread, 0.1f, 1.5f
            );
            int sequenceLength = Mathf.RoundToInt(DrawParameter(
                "Jitter sequence", config.SequenceLength, 4.0f, 32.0f
            ));
            float sharpness = DrawParameter(
                "Sharpness", config.Sharpness, 0.0f, 1.0f
            );
            float preExposure = DrawParameter(
                "Pre-exposure", config.PreExposure, 0.01f, 16.0f
            );
            bool autoExposure = GUILayout.Toggle(
                config.AutoExposure,
                " Automatic exposure"
            );
            bool preferPpv2Exposure = GUILayout.Toggle(
                config.PreferPpv2Exposure,
                " Prefer game / PPv2 exposure"
            );
            bool invertMotionX = GUILayout.Toggle(
                config.InvertMotionX,
                " Invert motion-vector X in sanitizer"
            );
            bool invertMotionY = GUILayout.Toggle(
                config.InvertMotionY,
                " Invert motion-vector Y in sanitizer"
            );

            var updated = new Fsr2Config(
                jitterSpread,
                sequenceLength,
                sharpness,
                preExposure,
                autoExposure,
                invertMotionX,
                invertMotionY,
                preferPpv2Exposure
            );
            if (!config.ValuesEqual(in updated))
            {
                _setFsr2Config(updated);
            }
            GUILayout.EndScrollView();

            long bytes = _fsr2MemoryBytes == null ? 0 : _fsr2MemoryBytes();
            GUILayout.Label(
                bytes > 0
                    ? "Project-owned FSR2 output: " +
                      (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
                    : "The FSR2 output is allocated when its context first renders."
            );
            GUILayout.Label(
                "This first FSR2 mode is native-resolution AA only: render scale " +
                "must be 100%. It is selectable on AMD, NVIDIA, and Intel GPUs " +
                "when Unity's AMD runtime loads. Screen-wide corruption is " +
                "replaced in the same frame. Coherent camera pans may exceed " +
                "256 px; invalid, unverified >256 px, or >96 px disagreement uses " +
                "a <=256 px " +
                "camera fallback. " +
                "Unity-to-vendor X and Y inversion should both remain enabled."
            );
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Conservative preset", GUILayout.Height(28f)))
            {
                _restoreFsr2Preset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && _resetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                _resetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawBufferTab()
        {
            _bufferScroll = GUILayout.BeginScrollView(
                _bufferScroll,
                GUILayout.Height(500f)
            );
            DrawMotionInputControls();
            GUILayout.Space(10f);
            bool previousBurstEnabled = GUI.enabled;
            GUI.enabled = !_motionDiagnosticBurstActive &&
                GetSelectedCamera() != null;
            if (GUILayout.Button(
                    "Capture motion diagnosis burst (pan for ~5 seconds)",
                    GUILayout.Height(30f)))
            {
                RequestMotionDiagnosticBurst();
            }
            GUI.enabled = previousBurstEnabled;
            if (_motionDiagnosticBurstActive)
            {
                GUILayout.Label(
                    "Capturing " + (_motionDiagnosticBurstStep + 1) + "/" +
                    MotionDiagnosticBurstViews.Length + ". Keep panning smoothly."
                );
            }
            GUILayout.Label("Buffer view");
            for (int index = 0; index < ViewLabels.Length; index++)
            {
                BufferDebugView candidateView = (BufferDebugView)(index - 1);
                Color previousColor = GUI.backgroundColor;
                if (candidateView == _view)
                {
                    GUI.backgroundColor = Color.cyan;
                }

                if (GUILayout.Button(ViewLabels[index], GUILayout.Height(24f)))
                {
                    SetView(candidateView);
                }
                GUI.backgroundColor = previousColor;
            }

            if (MotionStatisticsEnabled)
            {
                GUILayout.Label(
                    "Legend: blue=no depth/quiet, magenta=no depth/moving, " +
                    "green=covered/quiet, cyan=covered/moving, " +
                    "yellow=covered >64 px diagnostic threshold, red=invalid."
                );
            }
            else if (_view == BufferDebugView.MotionSignAgreement)
            {
                GUILayout.Label(
                    "Pan horizontally and vertically over static, depth-covered " +
                    "geometry. This scores the raw source before sanitization: " +
                    "left half is X and right half is Y. Green agrees with camera " +
                    "reprojection, red is a confident local reversal, and dark blue " +
                    "is too small or too close to an axis zero-crossing to decide. " +
                    "The camera reference mirrors Unity's built-in top-origin Y " +
                    "conversion and uses the camera's actual screen/render-texture " +
                    "projection policy. " +
                    "A red patch at one view angle does not mean the global sign " +
                    "changed; only coherent red during a deliberate single-axis pan " +
                    "would contradict Unity's fixed convention. Both vendor inversion " +
                    "toggles should remain enabled."
                );
            }
            else if (_view == BufferDebugView.MotionSignReferenceAudit)
            {
                GUILayout.Label(
                    "Pan vertically over static, depth-covered terrain. The full " +
                    "scene repeats four times: left=screen GPU projection, " +
                    "right=render-texture GPU projection; upper=Unity's explicit " +
                    "top-origin Y conversion, lower=no Y conversion control. Green " +
                    "means raw Unity Y agrees with that camera-only reference, red " +
                    "means reversal, and dark blue is undecidable. The quadrant " +
                    "matching the report's automatic projection should be green in " +
                    "the upper row before using Raw Sign Agreement to judge the " +
                    "vendor Invert X/Y settings."
                );
            }
            else if (_view == BufferDebugView.SanitizedVendorMotion)
            {
                GUILayout.Label(
                    "This is the sanitized motion texture actually sent to " +
                    "Custom, DLAA, or FSR2. DLAA/FSR2 apply their configured " +
                    "component signs; Custom retains Unity's raw convention. " +
                    "Dark blue means the selected AA mode does not currently own " +
                    "a live sanitizer texture."
                );
            }
            else if (_view == BufferDebugView.MotionSanitizerDecision)
            {
                GUILayout.Label(
                    "Green keeps raw motion; yellow uses camera reprojection; " +
                    "red rejects to zero. Orange means the same-frame detector " +
                    "classified the screen-wide field as corrupt. Dark blue means " +
                    "the selected AA mode does not currently own a live sanitizer " +
                    "texture; Off and PPv2 cannot produce this diagnostic."
                );
            }
            else if (_view == BufferDebugView.LinearDepth)
            {
                GUILayout.Label(
                    "This is the rasterized depth buffer. With Custom, DLAA, " +
                    "or FSR2 active, stationary edges should move by the active " +
                    "subpixel jitter sequence. This is expected."
                );
            }
            else if (_view == BufferDebugView.DeJitteredLinearDepth)
            {
                GUILayout.Label(
                    "This samples the same depth at output-aligned UVs using " +
                    "the active backend jitter. It is still a point sample of a " +
                    "single-sample raster, so hard edges can toggle as coverage " +
                    "changes; matching raw motion does not prove camera shake. " +
                    "Use AA Off plus raw depth to test upstream stability."
                );
            }
            GUILayout.Space(6f);
            GUILayout.Label("Camera");
            _cameraScroll = GUILayout.BeginScrollView(
                _cameraScroll,
                GUILayout.Height(190f)
            );
            if (_candidateLabels.Length == 0)
            {
                GUILayout.Label("No enabled game camera is available.");
            }
            for (int index = 0; index < _candidateLabels.Length; index++)
            {
                Color previousColor = GUI.backgroundColor;
                if (index == _candidateIndex)
                {
                    GUI.backgroundColor = Color.cyan;
                }

                if (GUILayout.Button(_candidateLabels[index], GUILayout.Height(24f)))
                {
                    SelectCamera(index);
                }
                GUI.backgroundColor = previousColor;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8f);
            GUILayout.Label("Physics-motion cadence experiment");
            float fixedDeltaTime = Time.fixedDeltaTime;
            GUILayout.Label(
                "Game fixed step: " + (fixedDeltaTime * 1000.0f).ToString("0.###") +
                " ms (" + (fixedDeltaTime > 0.0f
                    ? 1.0f / fixedDeltaTime
                    : 0.0f).ToString("0.##") + " Hz)."
            );
            GUILayout.Label(
                "At high render FPS, stock physics poses can remain quiet for " +
                "several frames and then jump on a fixed update. This experiment " +
                "interpolates the rendered KSP physics poses, so color, depth, " +
                "and motion vectors remain matched. It does not blur vectors alone."
            );

            bool interpolationEnabled = _physicsInterpolationEnabled != null &&
                _physicsInterpolationEnabled();
            bool previousInterpolationEnabled = GUI.enabled;
            GUI.enabled = _setPhysicsInterpolationEnabled != null;
            Color previousInterpolationColor = GUI.backgroundColor;
            if (interpolationEnabled)
            {
                GUI.backgroundColor = Color.cyan;
            }
            if (GUILayout.Button(
                    interpolationEnabled
                        ? "KSP physics interpolation: ON (experimental)"
                        : "KSP physics interpolation: OFF (experimental)",
                    GUILayout.Height(28f)))
            {
                _setPhysicsInterpolationEnabled(!interpolationEnabled);
            }
            GUI.backgroundColor = previousInterpolationColor;
            GUI.enabled = previousInterpolationEnabled;
            GUILayout.Label(
                _physicsInterpolationStatus == null
                    ? "Interpolation controls are unavailable."
                    : _physicsInterpolationStatus()
            );

            previousInterpolationEnabled = GUI.enabled;
            GUI.enabled = interpolationEnabled &&
                _refreshPhysicsInterpolation != null;
            if (GUILayout.Button("Refresh active physics bodies", GUILayout.Height(24f)))
            {
                _refreshPhysicsInterpolation();
            }
            GUI.enabled = previousInterpolationEnabled;
            GUILayout.Label(
                "Disabled by default. Compare launch, docking/staging, time warp, " +
                "floating-origin changes, and landing before treating it as production-safe. " +
                "Unity interpolation may add about one fixed step of visual latency."
            );
            GUILayout.EndScrollView();
        }

        private void DrawMotionInputControls()
        {
            GUILayout.Label("Motion-input compatibility");
            GUILayout.Label(
                "These test the source repair and the fallback independently. " +
                "Changing either option resets the active temporal history."
            );

            bool vegetationEnabled = _vegetationMotionRepairEnabled != null &&
                _vegetationMotionRepairEnabled();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = _setVegetationMotionRepairEnabled != null;
            Color previousColor = GUI.backgroundColor;
            if (vegetationEnabled)
            {
                GUI.backgroundColor = Color.cyan;
            }
            if (GUILayout.Button(
                    vegetationEnabled
                        ? "Indirect vegetation motion repair: ON (default)"
                        : "Indirect vegetation motion repair: OFF",
                    GUILayout.Height(28f)))
            {
                _setVegetationMotionRepairEnabled(!vegetationEnabled);
            }
            GUI.backgroundColor = previousColor;
            GUI.enabled = previousEnabled;
            GUILayout.Label(
                _vegetationMotionRepairStatus == null
                    ? "Vegetation repair controls are unavailable."
                    : _vegetationMotionRepairStatus()
            );
            if (_vegetationMotionRepairReroutedCalls != null)
            {
                GUILayout.Label(
                    "Rerouted direct vegetation calls since load: " +
                    _vegetationMotionRepairReroutedCalls().ToString()
                );
            }

            GUILayout.Space(6f);
            bool sanitizerEnabled = _motionSanitizerEnabled != null &&
                _motionSanitizerEnabled();
            previousEnabled = GUI.enabled;
            GUI.enabled = _setMotionSanitizerEnabled != null;
            previousColor = GUI.backgroundColor;
            if (sanitizerEnabled)
            {
                GUI.backgroundColor = Color.cyan;
            }
            if (GUILayout.Button(
                    sanitizerEnabled
                        ? "Motion sanitizer + camera fallback: ON"
                        : "Motion sanitizer + camera fallback: OFF (default)",
                    GUILayout.Height(28f)))
            {
                _setMotionSanitizerEnabled(!sanitizerEnabled);
            }
            GUI.backgroundColor = previousColor;
            GUI.enabled = previousEnabled;
            GUILayout.Label(
                _motionSanitizerStatus == null
                    ? "Sanitizer controls are unavailable."
                    : _motionSanitizerStatus()
            );
            GUILayout.Label(
                "When OFF, rejection and camera substitution are bypassed. " +
                "Required Unity-to-vendor component signs remain active."
            );
        }

        private void DrawPerformanceProfile(BackendSelection mode)
        {
            GUILayout.Space(10f);
            GUILayout.Label("Performance profile (30 warm-up + 240 measured frames)");
            if (_performanceProfile == null || _startPerformanceProfile == null)
            {
                GUILayout.Label("Performance profiling is unavailable.");
                return;
            }

            PerformanceProfileSnapshot profile = _performanceProfile(mode);
            switch (profile.State)
            {
                case PerformanceProfileState.WarmingUp:
                    GUILayout.Label(
                        "Warming up: " + profile.WarmupFramesRemaining +
                        " frames remaining."
                    );
                    break;
                case PerformanceProfileState.Sampling:
                    GUILayout.Label(
                        "Sampling: " + profile.Samples + "/" +
                        profile.TargetSamples + " frames."
                    );
                    break;
                case PerformanceProfileState.Complete:
                    DrawCompletedPerformanceProfile(mode, in profile);
                    break;
                case PerformanceProfileState.BackendUnavailable:
                    GUILayout.Label(
                        "Profile stopped: this requested mode was unavailable or " +
                        "fell back to another backend."
                    );
                    break;
                case PerformanceProfileState.Cancelled:
                    GUILayout.Label("Profile cancelled before completion.");
                    break;
                default:
                    GUILayout.Label("No completed profile for this mode.");
                    break;
            }

            if (profile.Running)
            {
                if (GUILayout.Button("Cancel performance profile", GUILayout.Height(28f)))
                {
                    _cancelPerformanceProfile?.Invoke();
                }
            }
            else if (GUILayout.Button(
                         "Profile 240 frames (panel closes)",
                         GUILayout.Height(28f)))
            {
                _startPerformanceProfile(mode);
                _panelOpen = false;
                RestorePanelInputState();
            }
        }

        private void DrawCompletedPerformanceProfile(
            BackendSelection mode,
            in PerformanceProfileSnapshot profile)
        {
            GUILayout.Label(
                "Whole frame CPU: " +
                profile.AverageCpuFrameMilliseconds.ToString("0.00") +
                " ms average, " +
                profile.PeakCpuFrameMilliseconds.ToString("0.00") + " ms peak."
            );
            GUILayout.Label(
                profile.GpuSamples > 0
                    ? "Whole frame GPU: " +
                      profile.AverageGpuFrameMilliseconds.ToString("0.00") +
                      " ms average, " +
                      profile.PeakGpuFrameMilliseconds.ToString("0.00") +
                      " ms peak (" + profile.GpuSamples + " samples)."
                    : "Whole frame GPU timing was unavailable from Unity."
            );

            if (profile.ResolveSamples > 0)
            {
                GUILayout.Label(
                    "Mod resolve CPU submission: " +
                    profile.AverageResolveCpuMilliseconds.ToString("0.000") +
                    " ms average, " +
                    profile.PeakResolveCpuMilliseconds.ToString("0.000") +
                    " ms peak."
                );
            }
            else if (mode == BackendSelection.Ppv2Taa)
            {
                GUILayout.Label(
                    "PPv2 resolve-only timing is unavailable because Unity owns " +
                    "the internal post-process pass. Use whole-frame comparison."
                );
            }

            if (mode == BackendSelection.Off)
            {
                GUILayout.Label(
                    "Saved as the Off baseline for later mode comparisons."
                );
                return;
            }

            PerformanceProfileSnapshot baseline = _performanceProfile(
                BackendSelection.Off
            );
            if (baseline.State != PerformanceProfileState.Complete)
            {
                GUILayout.Label(
                    "Profile Off in the same scene to display approximate deltas."
                );
                return;
            }

            double cpuDelta = profile.AverageCpuFrameMilliseconds -
                baseline.AverageCpuFrameMilliseconds;
            GUILayout.Label(
                "Versus saved Off baseline: CPU " + FormatSigned(cpuDelta) + " ms" +
                FormatGpuDelta(in profile, in baseline) + "."
            );
        }

        private static string FormatGpuDelta(
            in PerformanceProfileSnapshot profile,
            in PerformanceProfileSnapshot baseline)
        {
            if (profile.GpuSamples <= 0 || baseline.GpuSamples <= 0)
            {
                return ", GPU unavailable";
            }
            double delta = profile.AverageGpuFrameMilliseconds -
                baseline.AverageGpuFrameMilliseconds;
            return ", GPU " + FormatSigned(delta) + " ms";
        }

        private static string FormatSigned(double value)
        {
            return value >= 0.0
                ? "+" + value.ToString("0.00")
                : value.ToString("0.00");
        }

        private void DrawCommonControls()
        {
            GUILayout.Label(_screenshotStatus);
            GUILayout.BeginHorizontal();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = !_screenshotRequested;
            if (GUILayout.Button("Capture screenshot (F10)", GUILayout.Height(28f)))
            {
                RequestScreenshot();
            }
            GUI.enabled = previousEnabled;
            if (GUILayout.Button("Write report", GUILayout.Height(28f)))
            {
                _reportRequested = true;
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Close panel (Ctrl+F10)", GUILayout.Height(28f)))
            {
                TogglePanel();
            }
        }

        private void ClampWindowToScreen()
        {
            _windowRect.x = Mathf.Clamp(
                _windowRect.x,
                0.0f,
                Mathf.Max(0.0f, Screen.width - _windowRect.width)
            );
            _windowRect.y = Mathf.Clamp(
                _windowRect.y,
                0.0f,
                Mathf.Max(0.0f, Screen.height - _windowRect.height)
            );
        }

        private static float DrawParameter(
            string label,
            float value,
            float minimum,
            float maximum)
        {
            GUILayout.Label(label + ": " + value.ToString("0.000"));
            return GUILayout.HorizontalSlider(value, minimum, maximum);
        }

        private void AttachSelected()
        {
            Camera camera = GetSelectedCamera();
            if (camera == null)
            {
                UpdateOverlayText();
                return;
            }
            if (_shader == null)
            {
                _logger.LogWarning(
                    "[ReduxBetterAA/Visualizer] Diagnostic shader is not loaded; view remains detached."
                );
                return;
            }

            if (_material == null)
            {
                _material = new Material(_shader)
                {
                    name = "Redux Better AA Phase 1 Debug Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            _attachedCamera = camera;
            _originalDepthTextureMode = camera.depthTextureMode;
            if (_view == BufferDebugView.LinearDepth ||
                _view == BufferDebugView.DeJitteredLinearDepth ||
                _view == BufferDebugView.ContributionMask)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
            }
            else if (_view == BufferDebugView.MotionVectorsRaw ||
                      _view == BufferDebugView.MotionVectorsNormalized ||
                      _view == BufferDebugView.MotionVectorsMagnitudeAngle ||
                      _view == BufferDebugView.MotionVectorsValidity ||
                      _view == BufferDebugView.MotionSignAgreement ||
                       _view == BufferDebugView.MotionVectorsBuiltinPreviousVP ||
                       _view == BufferDebugView.MotionVectorsPerPassPreviousVP ||
                       _view == BufferDebugView.MotionVectorsManagedPreviousVP ||
                       _view == BufferDebugView.MotionSignReferenceAudit ||
                       _view == BufferDebugView.SanitizedVendorMotion ||
                      _view == BufferDebugView.MotionSanitizerDecision)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            }

            int sourceWidth;
            int sourceHeight;
            GetCameraDimensions(camera, out sourceWidth, out sourceHeight);
            _material.SetVector(
                DiagnosticPixelDimensions,
                new Vector4(
                    sourceWidth,
                    sourceHeight,
                    1.0f / sourceWidth,
                    1.0f / sourceHeight
                )
            );
            _material.SetFloat(MotionQuietPixelsProperty, MotionQuietPixels);
            _material.SetFloat(MotionOutlierPixelsProperty, MotionOutlierPixels);
            _material.SetFloat(
                MotionSignConfidencePixelsProperty,
                MotionSignConfidencePixels
            );
            UpdateMotionSignMaterial();
            UpdateMotionSanitizerMaterial();
            UpdateDepthDiagnosticMaterial();

            if (_view == BufferDebugView.MotionVectorsValidity &&
                _statisticsMaterial != null)
            {
                CreateStatisticsTarget(sourceWidth, sourceHeight);
            }

            _commandBuffer = new CommandBuffer { name = CommandBufferName };
            _commandBuffer.GetTemporaryRT(
                TemporaryTarget,
                -1,
                -1,
                0,
                FilterMode.Point,
                RenderTextureFormat.Default
            );
            _commandBuffer.Blit(BuiltinRenderTextureType.CurrentActive, TemporaryTarget);
            _commandBuffer.Blit(TemporaryTarget, BuiltinRenderTextureType.CameraTarget, _material, (int)_view);
            if (_statisticsTarget != null && _statisticsMaterial != null)
            {
                _commandBuffer.Blit(
                    TemporaryTarget,
                    _statisticsTarget,
                    _statisticsMaterial,
                    0
                );
            }
            _commandBuffer.ReleaseTemporaryRT(TemporaryTarget);
            _attachedCameraEvent =
                _view == BufferDebugView.MotionVectorsBuiltinPreviousVP ||
                _view == BufferDebugView.MotionVectorsPerPassPreviousVP ||
                _view == BufferDebugView.MotionVectorsManagedPreviousVP
                    ? CameraEvent.BeforeImageEffects
                    : CameraEvent.AfterEverything;
            camera.AddCommandBuffer(_attachedCameraEvent, _commandBuffer);
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
        }

        private void Detach()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            _statisticsCaptureArmed = false;
            if (!_statisticsReadbackPending)
            {
                _pendingStatisticsReport = null;
                _statisticsOutputPath = null;
            }
            if (_attachedCamera != null)
            {
                if (_commandBuffer != null)
                {
                    _attachedCamera.RemoveCommandBuffer(
                        _attachedCameraEvent,
                        _commandBuffer
                    );
                }
                _attachedCamera.depthTextureMode = _originalDepthTextureMode;
            }
            if (_commandBuffer != null)
            {
                _commandBuffer.Release();
                _commandBuffer = null;
                _attachedCameraEvent = CameraEvent.AfterEverything;
            }
            _attachedCamera = null;
            _currentMatrixValid = false;
            _matrixHistoryValid = false;
            ReleaseStatisticsTarget();
        }

        private void CreateStatisticsTarget(int sourceWidth, int sourceHeight)
        {
            ReleaseStatisticsTarget();
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                return;
            }

            int largestDimension = Math.Max(sourceWidth, sourceHeight);
            float scale = largestDimension > MaximumStatisticsDimension
                ? (float)MaximumStatisticsDimension / largestDimension
                : 1.0f;
            int sampleWidth = Math.Max(1, Mathf.RoundToInt(sourceWidth * scale));
            int sampleHeight = Math.Max(1, Mathf.RoundToInt(sourceHeight * scale));
            RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(
                RenderTextureFormat.ARGBFloat
            )
                ? RenderTextureFormat.ARGBFloat
                : RenderTextureFormat.ARGBHalf;

            _statisticsTarget = new RenderTexture(
                sampleWidth,
                sampleHeight,
                0,
                format,
                RenderTextureReadWrite.Linear
            )
            {
                name = "Redux Better AA Phase 1 Motion Statistics",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!_statisticsTarget.Create())
            {
                _logger.LogWarning(
                    "[ReduxBetterAA/Statistics] Motion statistics target creation failed."
                );
                DestroyRenderTexture(_statisticsTarget);
                _statisticsTarget = null;
            }
        }

        private void ReleaseStatisticsTarget()
        {
            if (_statisticsTarget == null)
            {
                return;
            }
            if (_statisticsReadbackPending &&
                _statisticsTarget == _statisticsReadbackTarget)
            {
                _releaseReadbackTargetOnCompletion = true;
                _statisticsTarget = null;
                return;
            }

            DestroyRenderTexture(_statisticsTarget);
            _statisticsTarget = null;
        }

        private static void DestroyRenderTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }
            if (texture.IsCreated())
            {
                texture.Release();
            }
            UnityEngine.Object.Destroy(texture);
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (camera != _attachedCamera || _material == null)
            {
                return;
            }
            if (_motionVectorPassProbeActive)
            {
                UpdateMotionVectorPassProbeGlobals(camera);
            }
            if (_view == BufferDebugView.SanitizedVendorMotion ||
                _view == BufferDebugView.MotionSanitizerDecision)
            {
                // The sanitizer recreates its textures on output-size changes.
                // Refresh references without allocating so a diagnostic left open
                // never samples a released texture.
                UpdateMotionSanitizerMaterial();
                return;
            }
            if (_view == BufferDebugView.DeJitteredLinearDepth)
            {
                UpdateDepthDiagnosticMaterial();
                return;
            }
            if (_view != BufferDebugView.MotionSignAgreement &&
                _view != BufferDebugView.MotionSignReferenceAudit &&
                _view != BufferDebugView.MotionVectorsBuiltinPreviousVP &&
                _view != BufferDebugView.MotionVectorsPerPassPreviousVP &&
                _view != BufferDebugView.MotionVectorsManagedPreviousVP)
            {
                return;
            }

            UpdateDepthDiagnosticMaterial();
            Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
            Matrix4x4 view = camera.worldToCameraMatrix;
            _currentViewProjectionScreen = GL.GetGPUProjectionMatrix(
                projection,
                false
            ) * view;
            _currentInverseViewProjectionScreen =
                _currentViewProjectionScreen.inverse;
            _currentViewProjectionRenderTexture = GL.GetGPUProjectionMatrix(
                projection,
                true
            ) * view;
            _currentInverseViewProjectionRenderTexture =
                _currentViewProjectionRenderTexture.inverse;
            bool useRenderTextureProjection =
                MotionSignDiagnosticPolicy.UseRenderTextureProjection(
                    camera.targetTexture != null,
                    camera.forceIntoRenderTexture
                );
            _currentViewProjection = useRenderTextureProjection
                ? _currentViewProjectionRenderTexture
                : _currentViewProjectionScreen;
            _currentInverseViewProjection = _currentViewProjection.inverse;
            _currentMatrixValid =
                MatrixIsFinite(_currentViewProjectionScreen) &&
                MatrixIsFinite(_currentInverseViewProjectionScreen) &&
                MatrixIsFinite(_currentViewProjectionRenderTexture) &&
                MatrixIsFinite(_currentInverseViewProjectionRenderTexture) &&
                MatrixIsFinite(_currentViewProjection) &&
                MatrixIsFinite(_currentInverseViewProjection);

            _material.SetMatrix(
                CurrentInverseViewProjectionProperty,
                _currentInverseViewProjection
            );
            _material.SetMatrix(
                PreviousViewProjectionProperty,
                _previousViewProjection
            );
            _material.SetMatrix(
                ManagedPreviousViewProjectionProperty,
                camera.previousViewProjectionMatrix
            );
            _material.SetMatrix(
                CurrentInverseViewProjectionScreenProperty,
                _currentInverseViewProjectionScreen
            );
            _material.SetMatrix(
                PreviousViewProjectionScreenProperty,
                _previousViewProjectionScreen
            );
            _material.SetMatrix(
                CurrentInverseViewProjectionRenderTextureProperty,
                _currentInverseViewProjectionRenderTexture
            );
            _material.SetMatrix(
                PreviousViewProjectionRenderTextureProperty,
                _previousViewProjectionRenderTexture
            );
            _material.SetFloat(
                MatrixHistoryValidProperty,
                _currentMatrixValid && _matrixHistoryValid ? 1.0f : 0.0f
            );
            UpdateMotionSignMaterial();
        }

        private void UpdateMotionSanitizerMaterial()
        {
            if (_material == null)
            {
                return;
            }

            Texture sanitized = _sanitizedMotionTexture == null
                ? null
                : _sanitizedMotionTexture();
            Texture corruption = _motionCorruptionTexture == null
                ? null
                : _motionCorruptionTexture();
            bool available = sanitized != null && corruption != null;
            _material.SetTexture(
                SanitizedMotionTextureProperty,
                sanitized == null ? Texture2D.blackTexture : sanitized
            );
            _material.SetTexture(
                MotionCorruptionTextureProperty,
                corruption == null ? Texture2D.blackTexture : corruption
            );
            _material.SetFloat(
                MotionSanitizerAvailableProperty,
                available ? 1.0f : 0.0f
            );
        }

        private void UpdateDepthDiagnosticMaterial()
        {
            if (_material == null)
            {
                return;
            }
            Vector2 jitter = _currentJitterNormalized == null
                ? Vector2.zero
                : _currentJitterNormalized();
            _material.SetVector(CurrentJitterProperty, jitter);
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera != _attachedCamera)
            {
                return;
            }

            if ((_view == BufferDebugView.MotionSignAgreement ||
                 _view == BufferDebugView.MotionSignReferenceAudit) &&
                _currentMatrixValid)
            {
                _previousViewProjection = _currentViewProjection;
                _previousViewProjectionScreen = _currentViewProjectionScreen;
                _previousViewProjectionRenderTexture =
                    _currentViewProjectionRenderTexture;
                _matrixHistoryValid = true;
            }
            if (!_statisticsCaptureArmed)
            {
                return;
            }

            _statisticsCaptureArmed = false;
            if (_statisticsTarget == null || !_statisticsTarget.IsCreated())
            {
                CompleteStatisticsFailure("StatisticsTargetUnavailable", null);
                return;
            }

            try
            {
                _statisticsReadbackPending = true;
                _statisticsReadbackTarget = _statisticsTarget;
                AsyncGPUReadback.Request(
                    _statisticsReadbackTarget,
                    0,
                    TextureFormat.RGBAFloat,
                    OnStatisticsReadback
                );
            }
            catch (Exception exception)
            {
                _statisticsReadbackPending = false;
                _statisticsReadbackTarget = null;
                CompleteStatisticsFailure(
                    exception.GetType().Name,
                    exception.Message
                );
            }
        }

        private void UpdateMotionSignMaterial()
        {
            if (_material == null)
            {
                return;
            }

            bool invertX;
            bool invertY;
            BackendSelection backend;
            GetConfiguredMotionInversion(out invertX, out invertY, out backend);
            bool sanitizedInvertX = true;
            bool sanitizedInvertY = true;
            if (backend == BackendSelection.NvidiaDlaa && _dlaaConfig != null)
            {
                sanitizedInvertX = invertX;
                sanitizedInvertY = invertY;
            }
            else if (backend == BackendSelection.AmdFsr2 && _fsr2Config != null)
            {
                sanitizedInvertX = invertX;
                sanitizedInvertY = invertY;
            }
            else if (backend == BackendSelection.CustomTaa)
            {
                sanitizedInvertX = false;
                sanitizedInvertY = false;
            }

            _material.SetVector(
                MotionComponentSignProperty,
                new Vector4(
                    invertX ? -1.0f : 1.0f,
                    invertY ? -1.0f : 1.0f,
                    0.0f,
                    0.0f
                )
            );
            _material.SetVector(
                SanitizedMotionComponentSignProperty,
                new Vector4(
                    sanitizedInvertX ? -1.0f : 1.0f,
                    sanitizedInvertY ? -1.0f : 1.0f,
                    0.0f,
                    0.0f
                )
            );
        }

        private void GetConfiguredMotionInversion(
            out bool invertX,
            out bool invertY,
            out BackendSelection backend)
        {
            invertX = true;
            invertY = true;
            backend = _requestedBackend == null
                ? BackendSelection.Off
                : _requestedBackend();
            if (backend == BackendSelection.NvidiaDlaa && _dlaaConfig != null)
            {
                DlaaConfig config = _dlaaConfig();
                invertX = config.InvertMotionX;
                invertY = config.InvertMotionY;
            }
            else if (backend == BackendSelection.AmdFsr2 && _fsr2Config != null)
            {
                Fsr2Config config = _fsr2Config();
                invertX = config.InvertMotionX;
                invertY = config.InvertMotionY;
            }
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

        private void OnStatisticsReadback(AsyncGPUReadbackRequest request)
        {
            MotionVectorStatisticsReport report = _pendingStatisticsReport;
            string outputPath = _statisticsOutputPath;
            RenderTexture completedTarget = _statisticsReadbackTarget;
            bool releaseCompletedTarget = _releaseReadbackTargetOnCompletion;

            _statisticsReadbackPending = false;
            _statisticsReadbackTarget = null;
            _releaseReadbackTargetOnCompletion = false;
            _pendingStatisticsReport = null;
            _statisticsOutputPath = null;

            try
            {
                if (_disposed || report == null || string.IsNullOrEmpty(outputPath))
                {
                    return;
                }
                if (request.hasError)
                {
                    report.status = "GpuReadbackError";
                    report.error = "Unity reported an asynchronous GPU readback error.";
                }
                else
                {
                    PopulateStatistics(request, report);
                    report.status = "Complete";
                }

                File.WriteAllText(
                    outputPath,
                    JsonConvert.SerializeObject(report, Formatting.Indented)
                );
                string reportFileName = Path.GetFileName(outputPath);
                _screenshotStatus = request.hasError
                    ? "Screenshot saved; statistics readback failed: " + reportFileName
                    : "Saved screenshot + statistics: " + reportFileName;
                _logger.LogInfo(
                    "[ReduxBetterAA/Statistics] Motion report written to " + outputPath
                );
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    _screenshotStatus =
                        "Screenshot saved; statistics failed: " + exception.GetType().Name;
                    _logger.LogError(
                        "[ReduxBetterAA/Statistics] Motion report failed safely: " +
                        exception.GetType().Name + ": " + exception.Message
                    );
                }
            }
            finally
            {
                if (releaseCompletedTarget)
                {
                    DestroyRenderTexture(completedTarget);
                }
            }
        }

        private void CompleteStatisticsFailure(string errorType, string errorMessage)
        {
            MotionVectorStatisticsReport report = _pendingStatisticsReport;
            string outputPath = _statisticsOutputPath;
            _pendingStatisticsReport = null;
            _statisticsOutputPath = null;
            if (report == null || string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            report.status = "Failed";
            report.error = string.IsNullOrEmpty(errorMessage)
                ? errorType
                : errorType + ": " + errorMessage;
            try
            {
                File.WriteAllText(
                    outputPath,
                    JsonConvert.SerializeObject(report, Formatting.Indented)
                );
                _screenshotStatus = "Screenshot saved; statistics unavailable: " + errorType;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "[ReduxBetterAA/Statistics] Failure report could not be written: " +
                    exception.GetType().Name + ": " + exception.Message
                );
            }
        }

        private static void PopulateStatistics(
            AsyncGPUReadbackRequest request,
            MotionVectorStatisticsReport report)
        {
            var samples = request.GetData<Color>();
            int sampleCount = samples.Length;
            var allMagnitudes = new float[sampleCount];
            var coveredMagnitudes = new float[sampleCount];
            var uncoveredMagnitudes = new float[sampleCount];
            int allCount = 0;
            int coveredCount = 0;
            int uncoveredCount = 0;
            float minimumX = float.PositiveInfinity;
            float maximumX = float.NegativeInfinity;
            float minimumY = float.PositiveInfinity;
            float maximumY = float.NegativeInfinity;

            report.sampleCount = sampleCount;
            for (int index = 0; index < sampleCount; index++)
            {
                Color sample = samples[index];
                if (sample.a < 0.5f)
                {
                    report.invalidMotionCount++;
                    continue;
                }

                float motionX = sample.r * report.sourceWidth;
                float motionY = sample.g * report.sourceHeight;
                float magnitude = Mathf.Sqrt(motionX * motionX + motionY * motionY);
                if (float.IsNaN(magnitude) || float.IsInfinity(magnitude))
                {
                    report.invalidMotionCount++;
                    continue;
                }

                report.finiteMotionCount++;
                allMagnitudes[allCount++] = magnitude;
                minimumX = Math.Min(minimumX, motionX);
                maximumX = Math.Max(maximumX, motionX);
                minimumY = Math.Min(minimumY, motionY);
                maximumY = Math.Max(maximumY, motionY);

                bool covered = sample.b < 0.99999f;
                bool moving = magnitude > report.quietThresholdPixels;
                bool outlier = magnitude > report.outlierThresholdPixels;
                if (covered)
                {
                    report.depthCoveredCount++;
                    coveredMagnitudes[coveredCount++] = magnitude;
                    if (moving)
                    {
                        report.depthCoveredMovingCount++;
                    }
                    if (outlier)
                    {
                        report.depthCoveredOutlierCount++;
                    }
                }
                else
                {
                    report.noDepthCount++;
                    uncoveredMagnitudes[uncoveredCount++] = magnitude;
                    if (moving)
                    {
                        report.noDepthMovingCount++;
                    }
                    if (outlier)
                    {
                        report.noDepthOutlierCount++;
                    }
                }
            }

            report.depthCoverageRatio = report.finiteMotionCount > 0
                ? (float)report.depthCoveredCount / report.finiteMotionCount
                : 0.0f;
            report.noDepthMovingRatio = report.noDepthCount > 0
                ? (float)report.noDepthMovingCount / report.noDepthCount
                : 0.0f;
            report.depthCoveredMovingRatio = report.depthCoveredCount > 0
                ? (float)report.depthCoveredMovingCount / report.depthCoveredCount
                : 0.0f;
            report.minimumMotionXPixels = allCount > 0 ? minimumX : 0.0f;
            report.maximumMotionXPixels = allCount > 0 ? maximumX : 0.0f;
            report.minimumMotionYPixels = allCount > 0 ? minimumY : 0.0f;
            report.maximumMotionYPixels = allCount > 0 ? maximumY : 0.0f;
            report.allMotionPixels = BuildMagnitudeSummary(allMagnitudes, allCount);
            report.depthCoveredMotionPixels = BuildMagnitudeSummary(
                coveredMagnitudes,
                coveredCount
            );
            report.noDepthMotionPixels = BuildMagnitudeSummary(
                uncoveredMagnitudes,
                uncoveredCount
            );
            PopulateAnchorSamples(samples, report);
        }

        private static void PopulateAnchorSamples(
            Unity.Collections.NativeArray<Color> samples,
            MotionVectorStatisticsReport report)
        {
            const int anchorsPerAxis = 4;
            var anchors = new MotionVectorAnchorSample[
                anchorsPerAxis * anchorsPerAxis
            ];
            int anchorIndex = 0;
            int sampleWidth = Math.Max(1, report.sampleWidth);
            int sampleHeight = Math.Max(1, report.sampleHeight);
            for (int y = 0; y < anchorsPerAxis; y++)
            {
                for (int x = 0; x < anchorsPerAxis; x++)
                {
                    float uvX = (x * 2.0f + 1.0f) / (anchorsPerAxis * 2.0f);
                    float uvY = (y * 2.0f + 1.0f) / (anchorsPerAxis * 2.0f);
                    int sampleX = Mathf.Clamp(
                        Mathf.FloorToInt(uvX * sampleWidth),
                        0,
                        sampleWidth - 1
                    );
                    int sampleY = Mathf.Clamp(
                        Mathf.FloorToInt(uvY * sampleHeight),
                        0,
                        sampleHeight - 1
                    );
                    int sampleIndex = sampleY * sampleWidth + sampleX;
                    Color sample = sampleIndex >= 0 && sampleIndex < samples.Length
                        ? samples[sampleIndex]
                        : new Color(0.0f, 0.0f, 1.0f, 0.0f);
                    bool finite = sample.a >= 0.5f &&
                        !float.IsNaN(sample.r) && !float.IsInfinity(sample.r) &&
                        !float.IsNaN(sample.g) && !float.IsInfinity(sample.g);
                    float motionX = finite ? sample.r * report.sourceWidth : 0.0f;
                    float motionY = finite ? sample.g * report.sourceHeight : 0.0f;
                    float magnitude = Mathf.Sqrt(
                        motionX * motionX + motionY * motionY
                    );
                    bool overLimit = !finite ||
                        magnitude > report.outlierThresholdPixels;
                    if (overLimit)
                    {
                        report.anchorOutlierCount++;
                    }
                    anchors[anchorIndex++] = new MotionVectorAnchorSample
                    {
                        uvX = uvX,
                        uvY = uvY,
                        sampleX = sampleX,
                        sampleY = sampleY,
                        finite = finite,
                        hasSceneDepth = sample.b < 0.99999f,
                        linearDepth = sample.b,
                        motionXPixels = motionX,
                        motionYPixels = motionY,
                        magnitudePixels = magnitude,
                        overLimit = overLimit
                    };
                }
            }
            report.anchorSamples = anchors;
        }

        private static MotionMagnitudeSummary BuildMagnitudeSummary(
            float[] values,
            int count)
        {
            var summary = new MotionMagnitudeSummary { sampleCount = count };
            if (count <= 0)
            {
                return summary;
            }

            Array.Sort(values, 0, count);
            double sum = 0.0;
            for (int index = 0; index < count; index++)
            {
                sum += values[index];
            }
            summary.mean = (float)(sum / count);
            summary.p50 = ReadPercentile(values, count, 0.50f);
            summary.p95 = ReadPercentile(values, count, 0.95f);
            summary.p99 = ReadPercentile(values, count, 0.99f);
            summary.maximum = values[count - 1];
            return summary;
        }

        private static float ReadPercentile(float[] values, int count, float percentile)
        {
            float position = (count - 1) * percentile;
            int lower = Mathf.FloorToInt(position);
            int upper = Mathf.CeilToInt(position);
            return Mathf.Lerp(values[lower], values[upper], position - lower);
        }

        private static void GetCameraDimensions(
            Camera camera,
            out int width,
            out int height)
        {
            RenderTexture target = camera.targetTexture;
            width = Math.Max(1, target == null ? camera.pixelWidth : target.width);
            height = Math.Max(1, target == null ? camera.pixelHeight : target.height);
        }

        private Camera GetSelectedCamera()
        {
            return _candidateIndex >= 0 && _candidateIndex < _candidates.Length
                ? _candidates[_candidateIndex]
                : null;
        }

        private int FindCandidateIndex(Camera camera)
        {
            if (camera == null)
            {
                return -1;
            }
            for (int index = 0; index < _candidates.Length; index++)
            {
                if (_candidates[index] == camera)
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool CandidatesEqual(Camera[] left, Camera[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static string[] BuildCandidateLabels(Camera[] cameras)
        {
            var labels = new string[cameras.Length];
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera camera = cameras[index];
                labels[index] = camera == null
                    ? index.ToString() + ": unavailable"
                    : index.ToString() + ": " + camera.name +
                      " (depth " + camera.depth.ToString("R") + ")";
            }
            return labels;
        }

        private void CapturePanelInputState()
        {
            if (!_cursorStateCaptured)
            {
                _previousCursorLockMode = Cursor.lockState;
                _previousCursorVisible = Cursor.visible;
                _cursorStateCaptured = true;
            }

            SuppressCurrentEventSystem();
            UnlockCursor();
        }

        private static void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void MaintainPanelInputState()
        {
            SuppressCurrentEventSystem();
            UnlockCursor();
        }

        private void SuppressCurrentEventSystem()
        {
            EventSystem current = EventSystem.current;
            if (current == _suppressedEventSystem)
            {
                if (_suppressedEventSystem != null)
                {
                    _suppressedEventSystem.enabled = false;
                }
                return;
            }

            RestoreEventSystemState();
            if (current == null)
            {
                return;
            }

            _suppressedEventSystem = current;
            _eventSystemWasEnabled = current.enabled;
            current.enabled = false;
        }

        private void RestorePanelInputState()
        {
            RestoreEventSystemState();
            if (_cursorStateCaptured)
            {
                Cursor.lockState = _previousCursorLockMode;
                Cursor.visible = _previousCursorVisible;
                _cursorStateCaptured = false;
            }
        }

        private void RestoreEventSystemState()
        {
            if (_suppressedEventSystem != null)
            {
                _suppressedEventSystem.enabled = _eventSystemWasEnabled;
            }
            _suppressedEventSystem = null;
            _eventSystemWasEnabled = false;
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
                _logger.LogInfo("[ReduxBetterAA/Visualizer] Diagnostic shader loaded; view remains off.");
                if (Active)
                {
                    AttachSelected();
                }
            }
            else
            {
                _logger.LogError("[ReduxBetterAA/Visualizer] Diagnostic shader failed to load.");
            }
        }

        private void OnStatisticsShaderLoaded(
            AsyncOperationHandle<Shader> operation)
        {
            if (_disposed)
            {
                return;
            }
            if (operation.Status == AsyncOperationStatus.Succeeded &&
                operation.Result != null && operation.Result.isSupported)
            {
                _statisticsShader = operation.Result;
                _statisticsMaterial = new Material(_statisticsShader)
                {
                    name = "Redux Better AA Motion Statistics Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _logger.LogInfo(
                    "[ReduxBetterAA/Statistics] Dedicated motion statistics shader loaded."
                );
                if (_view == BufferDebugView.MotionVectorsValidity &&
                    _attachedCamera != null)
                {
                    Detach();
                    AttachSelected();
                }
            }
            else
            {
                _logger.LogError(
                    "[ReduxBetterAA/Statistics] Motion statistics shader failed to load."
                );
            }
        }

        private void OnMotionVectorPassProbeShaderLoaded(
            AsyncOperationHandle<Shader> operation)
        {
            if (_disposed)
            {
                return;
            }
            if (operation.Status == AsyncOperationStatus.Succeeded &&
                operation.Result != null && operation.Result.isSupported)
            {
                _motionVectorPassProbeShader = operation.Result;
                _logger.LogInfo(
                    "[ReduxBetterAA/Probe] Built-in motion-vector pass probe loaded."
                );
            }
            else
            {
                _logger.LogError(
                    "[ReduxBetterAA/Probe] Built-in motion-vector pass probe failed to load."
                );
            }
        }

        private void UpdateMotionVectorPassProbeGlobals(Camera camera)
        {
            Shader.SetGlobalInt(
                MotionVectorPassProbeModeProperty,
                _motionVectorPassProbeMode
            );
            if (camera != null)
            {
                Shader.SetGlobalMatrix(
                    MotionVectorPassManagedPreviousProperty,
                    camera.previousViewProjectionMatrix
                );
            }
        }

        private void UpdateOverlayText()
        {
            Camera camera = GetSelectedCamera();
            _overlayText = camera == null
                ? "Redux Better AA Phase 1 | " + _view + " | no camera"
                : "Redux Better AA Phase 1 | " + _view + " | " + camera.name +
                  " | Ctrl+F10 panel | F10 capture";
        }

        private void LogState()
        {
            Camera camera = GetSelectedCamera();
            _logger.LogInfo(
                camera == null
                    ? "[ReduxBetterAA/Visualizer] " + _view + "; no active camera selected."
                    : "[ReduxBetterAA/Visualizer] " + _view + " on camera " + camera.name + "."
            );
        }
    }

    internal static class MotionSignDiagnosticPolicy
    {
        public static bool UseRenderTextureProjection(
            bool targetTexturePresent,
            bool forceIntoRenderTexture)
        {
            return targetTexturePresent || forceIntoRenderTexture;
        }
    }

    [Serializable]
    internal sealed class MotionVectorStatisticsReport
    {
        public int schemaVersion;
        public string capturedUtc;
        public string screenshotFile;
        public string view;
        public string camera;
        public string status;
        public string error;
        public int sourceWidth;
        public int sourceHeight;
        public int sampleWidth;
        public int sampleHeight;
        public string samplingNote;
        public float quietThresholdPixels;
        public float outlierThresholdPixels;
        public float fixedDeltaTimeMilliseconds;
        public float fixedUpdateHz;
        public bool experimentalRenderInterpolationEnabled;
        public int interpolatedKspPhysicsBodies;
        public string interpolationStatus;
        public int sampleCount;
        public int finiteMotionCount;
        public int invalidMotionCount;
        public int depthCoveredCount;
        public int noDepthCount;
        public int depthCoveredMovingCount;
        public int noDepthMovingCount;
        public int depthCoveredOutlierCount;
        public int noDepthOutlierCount;
        public float depthCoverageRatio;
        public float depthCoveredMovingRatio;
        public float noDepthMovingRatio;
        public float minimumMotionXPixels;
        public float maximumMotionXPixels;
        public float minimumMotionYPixels;
        public float maximumMotionYPixels;
        public MotionMagnitudeSummary allMotionPixels;
        public MotionMagnitudeSummary depthCoveredMotionPixels;
        public MotionMagnitudeSummary noDepthMotionPixels;
        public int anchorOutlierCount;
        public MotionVectorAnchorSample[] anchorSamples;
    }

    [Serializable]
    internal sealed class MotionVectorAnchorSample
    {
        public float uvX;
        public float uvY;
        public int sampleX;
        public int sampleY;
        public bool finite;
        public bool hasSceneDepth;
        public float linearDepth;
        public float motionXPixels;
        public float motionYPixels;
        public float magnitudePixels;
        public bool overLimit;
    }

    [Serializable]
    internal sealed class MotionMagnitudeSummary
    {
        public int sampleCount;
        public float mean;
        public float p50;
        public float p95;
        public float p99;
        public float maximum;
    }
}
