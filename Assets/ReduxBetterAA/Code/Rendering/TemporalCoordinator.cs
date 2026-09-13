using System;
using KSP.Game;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Configuration;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.PostProcessing;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    internal sealed class TemporalCoordinator : IDisposable
    {
        private const float DiscoveryRetrySeconds = 1.0f;
        private const int MaximumDiscoveryRetries = 5;
        private const float HitchThresholdSeconds = 0.03f;
        private const float HitchLogCooldownSeconds = 1.0f;
        private const float GameStatePollSeconds = 0.25f;

        public static TemporalCoordinator Current;
        private bool _comparisonSuspended;
        internal event Action<HistoryResetReason> ComparisonReset;

        internal void SuspendForComparison(bool suspended)
        {
            if (_disposed) return;
            _comparisonSuspended = suspended;
            if (suspended)
            {
                DisableActiveBackend();
                _renderScale.Apply(100);
            }
            else MarkDirty(HistoryResetReason.BackendChanged);
        }

        private readonly ReduxLogger _logger;
        private readonly BackendPerformanceProfiler _performanceProfiler =
            new BackendPerformanceProfiler();
        private readonly Ppv2SpatialAaBackend _fxaaLowBackend =
            new Ppv2SpatialAaBackend(
                "FXAA Low",
                PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                true
            );
        private readonly Ppv2SpatialAaBackend _smaaBackend =
            new Ppv2SpatialAaBackend(
                "SMAA",
                PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                false
            );
        private readonly Ppv2SpatialAaBackend _fxaaHighBackend =
            new Ppv2SpatialAaBackend(
                "FXAA High",
                PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                false
            );
        private readonly MotionVectorSanitizer _motionVectorSanitizer;
        private readonly DepthDisocclusionMask _depthDisocclusionMask;
        private readonly CustomTaaBackend _customBackend;
        private readonly NvidiaDlaaBackend _dlaaBackend;
        private readonly AmdFsr2Backend _fsr2Backend;
        private readonly NvidiaDlaaBackend _dlssBackend;
        private readonly AmdFsr2Backend _fsrUpscalingBackend;
        private readonly ReduxSceneOutput _sceneOutput = new ReduxSceneOutput();
        private ReconstructionQuality _reconstructionQuality = ReconstructionQuality.Quality;
        public ReconstructionQuality UpscalingQuality => _reconstructionQuality;

        private static bool IsUpscaling(BackendSelection mode) =>
            mode == BackendSelection.NvidiaDlss || mode == BackendSelection.AmdFsrUpscaling;

        public void SetReconstructionQuality(ReconstructionQuality quality)
        {
            quality = ReconstructionPolicy.Normalize(quality);
            if (quality == ReconstructionQuality.Native) quality = ReconstructionQuality.Quality;
            if (_reconstructionQuality == quality) return;
            _reconstructionQuality = quality;
            _renderScale.Reclaim();
            if (IsUpscaling(_requestedBackend)) MarkDirty(HistoryResetReason.RenderScaleChanged);
        }
        private readonly DisabledBackend _disabledBackend = new DisabledBackend();
        private readonly SupersamplingBackend _supersamplingBackend = new SupersamplingBackend();
        private readonly RenderScaleOwnership _renderScale = new RenderScaleOwnership();
        private int _supersamplingPercent = 150;
        internal bool VegetationRepairRequested = true;
        public int SupersamplingPercent => _supersamplingPercent;
        public int AppliedRenderScalePercent => _renderScale.AppliedPercent;

        public void SetSupersamplingPercent(int percent)
        {
            percent = Mathf.Clamp(percent, 125, 200);
            if (percent == _supersamplingPercent) return;
            _supersamplingPercent = percent;
            _renderScale.Reclaim();
            if (_requestedBackend == BackendSelection.Supersampling)
                MarkDirty(HistoryResetReason.RenderScaleChanged);
        }
        private readonly HistoryResetTracker _resetTracker = new HistoryResetTracker();
        private readonly TemporalGameEvents _gameEvents;

        private readonly ITemporalBackend[] _backends;
        private ITemporalBackend _activeBackend;
        private TemporalCameraSet _cameras;
        private MapIconOverlay _mapIcons;
        private BackendSelection _requestedBackend;
        private HistoryResetReason _pendingResetReasons;
        private bool _dirty;
        private bool _disposed;
        private bool _mapViewAaEnabled = true;
        private bool _originRebasedPending;
        private float _discoverAfter;
        private float _nextHitchLogTime;
        private float _nextGameStatePollTime;
        private uint _frameIndex;
        private int _remainingDiscoveryRetries;
        private int _lastResetUnityFrame = -1;
        private int _lastOriginRebaseUnityFrame = -1;
        private HistoryResetReason _lastResetReason;
        private GameState _lastGameState = GameState.Invalid;
        private CustomTaaConfig _customConfig = CustomTaaConfig.Conservative;
        private readonly DlaaSceneSettings _dlaaSettings = new DlaaSceneSettings();
        private Fsr2Config _fsr2Config = Fsr2Config.Conservative;
        private string _status = "Off";

        public TemporalCoordinator(ReduxLogger logger)
        {
            _logger = logger;
            _gameEvents = new TemporalGameEvents(MarkDirty);
            _motionVectorSanitizer = new MotionVectorSanitizer(
                logger,
                OnTemporalResourceAvailabilityChanged
            );
            _depthDisocclusionMask = new DepthDisocclusionMask(
                logger,
                OnTemporalResourceAvailabilityChanged
            );
            _customBackend = new CustomTaaBackend(
                logger,
                OnTemporalResourceAvailabilityChanged,
                _performanceProfiler,
                _motionVectorSanitizer,
                reason => OnRuntimeFailure(BackendSelection.CustomTaa, "Custom TAA", reason)
            );
            _dlaaBackend = new NvidiaDlaaBackend(
                logger,
                reason => OnRuntimeFailure(BackendSelection.NvidiaDlaa, "DLAA", reason),
                _performanceProfiler,
                _motionVectorSanitizer,
                _depthDisocclusionMask
            );
            _fsr2Backend = new AmdFsr2Backend(
                logger,
                reason => OnRuntimeFailure(BackendSelection.AmdFsr2, "AMD FSR", reason),
                _performanceProfiler,
                _motionVectorSanitizer,
                _depthDisocclusionMask,
                OnTemporalResourceAvailabilityChanged
            );
            _dlssBackend = new NvidiaDlaaBackend(logger,
                reason => OnRuntimeFailure(BackendSelection.NvidiaDlss, "DLSS", reason),
                _performanceProfiler, _motionVectorSanitizer, _depthDisocclusionMask);
            _fsrUpscalingBackend = new AmdFsr2Backend(logger,
                reason => OnRuntimeFailure(BackendSelection.AmdFsrUpscaling, "FSR upscaling", reason),
                _performanceProfiler, _motionVectorSanitizer, _depthDisocclusionMask, OnTemporalResourceAvailabilityChanged);
            _dlssBackend.ConfigureReconstruction(_reconstructionQuality);
            _fsrUpscalingBackend.ConfigureReconstruction(_reconstructionQuality);
            // Keep persisted mode IDs stable. Slot 4 was the removed PPv2 TAA mode;
            // selection normalizes it to Custom TAA without duplicating ownership.
            _backends = new ITemporalBackend[]
            {
                _disabledBackend, _fxaaLowBackend, _fxaaHighBackend, _smaaBackend,
                null, _customBackend, _dlaaBackend, _fsr2Backend, _supersamplingBackend,
                _dlssBackend, _fsrUpscalingBackend
            };
            _activeBackend = _disabledBackend;
        }

        public bool Requested => _requestedBackend != BackendSelection.Off;
        public BackendSelection RequestedBackend => _requestedBackend;
        public bool Active => _activeBackend.Active;
        public string SelectedBackend => _activeBackend.Id;
        internal Camera ResolveCamera => _cameras?.ResolveCamera;
        internal IProjectionJitterSource AuxiliaryProjectionSource =>
            !_disposed && !_comparisonSuspended && Active
                ? _activeBackend as IProjectionJitterSource : null;

        // Called only by an explicit diagnostic capture; no traversal in the render loop.
        internal object[] CaptureBufferOwners() => new object[]
        {
            _activeBackend, _motionVectorSanitizer, _depthDisocclusionMask
        };
        public string ResolveCameraName =>
            _cameras == null || _cameras.ResolveCamera == null
                ? string.Empty
                : _cameras.ResolveCamera.name;
        public string SharedJitterCameraName =>
            _cameras == null || _cameras.SharedJitterCamera == null
                ? string.Empty
                : _cameras.SharedJitterCamera.name;
        public bool ProjectionJitterSupported =>
            _cameras != null && _cameras.ProjectionJitterSupported;
        public bool JitterTransparentRendering =>
            _cameras != null && _cameras.JitterTransparentRendering;
        public bool MapViewAaEnabled => _mapViewAaEnabled;
        public bool MapViewAaOverrideActive => !_mapViewAaEnabled &&
            _requestedBackend != BackendSelection.Off &&
            _cameras != null &&
            _cameras.SceneKind == TemporalSceneKind.Map;
        public HistoryResetReason LastResetReason => _lastResetReason;
        public CustomTaaConfig CustomConfig => _customConfig;
        public DlaaConfig DlaaConfig => _dlaaSettings.Current;
        public bool DlaaPresetIsMenuOnly => _dlaaSettings.IsMainMenu;
        public Fsr2Config Fsr2Config => _fsr2Config;
        public long CustomEstimatedMemoryBytes => _customBackend.EstimatedMemoryBytes;
        private NvidiaDlaaBackend NvidiaDiagnostics => _activeBackend == _dlssBackend ? _dlssBackend : _dlaaBackend;
        private AmdFsr2Backend AmdDiagnostics => _activeBackend == _fsrUpscalingBackend ? _fsrUpscalingBackend : _fsr2Backend;
        public bool DlaaManagedSurfaceAvailable =>
            NvidiaDiagnostics.ManagedSurfaceAvailable;
        public bool DlaaContextCreated => NvidiaDiagnostics.ContextCreated;
        public bool? DlaaContextUsesHdr => NvidiaDiagnostics.ContextCreated
            ? (bool?)NvidiaDiagnostics.ContextUsesHdr : null;
        public uint DlaaDeviceVersion => NvidiaDiagnostics.DeviceVersion;
        public long DlaaEstimatedMemoryBytes => NvidiaDiagnostics.EstimatedMemoryBytes +
            _motionVectorSanitizer.EstimatedMemoryBytes +
            _depthDisocclusionMask.EstimatedMemoryBytes;
        public int DlaaInputWidth => NvidiaDiagnostics.InputWidth;
        public int DlaaInputHeight => NvidiaDiagnostics.InputHeight;
        public int DlaaOutputWidth => NvidiaDiagnostics.OutputWidth;
        public int DlaaOutputHeight => NvidiaDiagnostics.OutputHeight;
        public string DlaaOutputGraphicsFormat => NvidiaDiagnostics.OutputGraphicsFormat;
        public bool DlaaOutputRandomWrite => NvidiaDiagnostics.OutputRandomWrite;
        public string DlaaLastFailure => NvidiaDiagnostics.LastFailure;
        public string DlaaExposureSource => NvidiaDiagnostics.ExposureSource;
        public float DlaaEffectivePreExposure =>
            NvidiaDiagnostics.EffectivePreExposure;
        public bool Fsr2ManagedSurfaceAvailable =>
            AmdDiagnostics.ManagedSurfaceAvailable;
        public bool Fsr2ContextCreated => AmdDiagnostics.ContextCreated;
        public bool? Fsr2ContextUsesHdr => AmdDiagnostics.ContextUsesHdr;
        public uint Fsr2DeviceVersion => AmdDiagnostics.DeviceVersion;
        public long Fsr2EstimatedMemoryBytes => AmdDiagnostics.EstimatedMemoryBytes +
            _motionVectorSanitizer.EstimatedMemoryBytes +
            _depthDisocclusionMask.EstimatedMemoryBytes;
        public int Fsr2InputWidth => AmdDiagnostics.InputWidth;
        public int Fsr2InputHeight => AmdDiagnostics.InputHeight;
        public int Fsr2OutputWidth => AmdDiagnostics.OutputWidth;
        public int Fsr2OutputHeight => AmdDiagnostics.OutputHeight;
        public string Fsr2OutputGraphicsFormat => AmdDiagnostics.OutputGraphicsFormat;
        public bool Fsr2OutputRandomWrite => AmdDiagnostics.OutputRandomWrite;
        public string Fsr2LastFailure => AmdDiagnostics.LastFailure;
        public string Fsr2ExposureSource => AmdDiagnostics.ExposureSource;
        public float Fsr2EffectivePreExposure =>
            AmdDiagnostics.EffectivePreExposure;
        public Vector2 Fsr2ProjectionJitterPixels =>
            AmdDiagnostics.ProjectionJitterPixels;
        public Vector2 Fsr2DispatchJitterPixels =>
            AmdDiagnostics.DispatchJitterPixels;
        public string MotionVectorSanitizerStatus => _motionVectorSanitizer.Status;
        public bool MotionVectorSanitizerEnabled =>
            _motionVectorSanitizer.Enabled;
        public long MotionVectorSanitizerEstimatedMemoryBytes =>
            _motionVectorSanitizer.EstimatedMemoryBytes;
        public Texture MotionVectorSanitizedTexture =>
            _motionVectorSanitizer.SanitizedTexture;
        public Texture MotionVectorCorruptionTexture =>
            _motionVectorSanitizer.CorruptionTexture;
        public Vector2 CurrentJitterNormalized
        {
            get
            {
                if (_activeBackend == _customBackend)
                {
                    return _customBackend.CurrentJitterNormalized;
                }
                if (_activeBackend == _dlaaBackend || _activeBackend == _dlssBackend)
                {
                    return ((NvidiaDlaaBackend)_activeBackend).CurrentJitterNormalized;
                }
                if (_activeBackend == _fsr2Backend || _activeBackend == _fsrUpscalingBackend)
                {
                    return ((AmdFsr2Backend)_activeBackend).CurrentJitterNormalized;
                }
                return Vector2.zero;
            }
        }
        public MotionVectorMatrixSnapshot MotionVectorMatrixSnapshot =>
            _motionVectorSanitizer.MatrixSnapshot;
        public string DepthDisocclusionMaskStatus => _depthDisocclusionMask.Status;
        public long DepthDisocclusionMaskEstimatedMemoryBytes =>
            _depthDisocclusionMask.EstimatedMemoryBytes;
        public string DlaaDetails
        {
            get
            {
                if (NvidiaDiagnostics.ContextCreated)
                {
                    return "Context active; API v" + NvidiaDiagnostics.DeviceVersion +
                        "; input " + NvidiaDiagnostics.InputWidth + "x" +
                        NvidiaDiagnostics.InputHeight + "; output " +
                        NvidiaDiagnostics.OutputWidth + "x" +
                        NvidiaDiagnostics.OutputHeight + " " +
                        NvidiaDiagnostics.OutputGraphicsFormat +
                        (NvidiaDiagnostics.OutputRandomWrite ? " UAV; " : " (not UAV); ") +
                        NvidiaDiagnostics.ExposureSource + " " +
                        NvidiaDiagnostics.EffectivePreExposure.ToString("0.000") + "; " +
                        _motionVectorSanitizer.Status + "; " +
                        _depthDisocclusionMask.Status;
                }
                if (!string.IsNullOrEmpty(NvidiaDiagnostics.LastFailure))
                {
                    return "Unavailable: " + NvidiaDiagnostics.LastFailure;
                }
                return NvidiaDiagnostics.ManagedSurfaceAvailable
                    ? "Managed Unity NVIDIA API found; context is created on first render."
                    : "Managed Unity NVIDIA API was not found.";
            }
        }
        public string Fsr2Details
        {
            get
            {
                if (AmdDiagnostics.ContextCreated)
                {
                    return AmdDiagnostics.Id + " context active; input " + AmdDiagnostics.InputWidth + "x" +
                        AmdDiagnostics.InputHeight + "; output " +
                        AmdDiagnostics.OutputWidth + "x" +
                        AmdDiagnostics.OutputHeight + " " +
                        AmdDiagnostics.OutputGraphicsFormat +
                        (AmdDiagnostics.OutputRandomWrite ? " UAV; " : " (not UAV); ") +
                        AmdDiagnostics.ExposureSource + " " +
                        AmdDiagnostics.EffectivePreExposure.ToString("0.000") + "; " +
                        _motionVectorSanitizer.Status;
                }
                if (!string.IsNullOrEmpty(AmdDiagnostics.LastFailure))
                {
                    return "Unavailable: " + AmdDiagnostics.LastFailure;
                }
                return AmdDiagnostics.ManagedSurfaceAvailable
                    ? "Modern AMD FSR runtime ready; context is created on first render."
                    : "Modern AMD FSR runtime is unavailable.";
            }
        }
        public string Status => _status;
        internal bool TraceFrameHitches { get; set; }

        public PerformanceProfileSnapshot GetPerformanceProfile(
            BackendSelection mode)
        {
            return _performanceProfiler.GetSnapshot(mode);
        }

        public void StartPerformanceProfile(BackendSelection mode)
        {
            if (!_comparisonSuspended) _performanceProfiler.Start(mode);
        }

        public void CancelPerformanceProfile()
        {
            _performanceProfiler.Cancel();
        }

        public void Initialize()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            _motionVectorSanitizer.Initialize();
            _depthDisocclusionMask.Initialize();
            _customBackend.Initialize();
            _dlaaBackend.Initialize();
            _fsr2Backend.Initialize();
            _dlssBackend.Initialize();
            _fsrUpscalingBackend.Initialize();
            ScheduleDiscovery();
            _pendingResetReasons =
                HistoryResetReason.FirstFrame | HistoryResetReason.BackendChanged;
            if (!Requested)
            {
                _status = "Off requested; discovering the final scene camera...";
            }
        }

        public void Tick()
        {
            ReduxBetterAA.Backends.Amd.AmdFsrNativeApi.CollectRetired();
            if (_disposed)
            {
                return;
            }

            if (_comparisonSuspended) return;
            if (_sceneOutput.Active && !string.IsNullOrEmpty(_sceneOutput.FailureReason))
            {
                if (_sceneOutput.ReacquisitionPending)
                    MarkDirty(HistoryResetReason.ResolutionChanged);
                else
                {
                    ActivateOffFallback("Upscaling", _sceneOutput.FailureReason);
                    return;
                }
            }
            if (TraceFrameHitches) TracePreviousFrameHitch();
            PollGameState();

            float now = Time.unscaledTime;
            if (_dirty && now >= _discoverAfter)
            {
                RefreshBackend(now);
            }
            _performanceProfiler.Tick(
                _requestedBackend,
                ActiveBackendSelection()
            );
            if (!_activeBackend.Active || _cameras == null)
            {
                return;
            }

            HistoryResetReason reasons = _pendingResetReasons;
            _pendingResetReasons = HistoryResetReason.None;
            reasons |= _resetTracker.Evaluate(
                _cameras.ResolveCamera,
                _cameras.RenderScalePercent,
                _originRebasedPending
            );
            _originRebasedPending = false;
            if (reasons != HistoryResetReason.None)
            {
                if ((reasons & (HistoryResetReason.SceneChanged |
                                HistoryResetReason.ResolutionChanged |
                                HistoryResetReason.RenderScaleChanged)) != 0)
                {
                    _performanceProfiler.InvalidateAll();
                }
                if ((reasons & HistoryResetReason.InvalidInput) != 0)
                {
                    MarkDirty(HistoryResetReason.None);
                    return;
                }
                ResetActiveHistory(reasons);
            }

            _activeBackend.Tick(_frameIndex++);
        }

        public void SetCustomConfig(CustomTaaConfig config)
        {
            if (_customConfig.ValuesEqual(in config))
            {
                return;
            }
            bool resetHistory = _customConfig.RequiresHistoryReset(in config);
            _customConfig = config;
            _performanceProfiler.Invalidate(BackendSelection.CustomTaa);
            _customBackend.ApplyConfig(in _customConfig);
            if (resetHistory &&
                _activeBackend == _customBackend && _activeBackend.Active)
            {
                ResetActiveHistory(HistoryResetReason.SettingsChanged);
            }
        }

        public void SetDlaaConfig(DlaaConfig config)
        {
            DlaaConfig previous = DlaaConfig;
            _dlaaSettings.SetCurrent(config);
            ApplyDlaaConfig(previous);
        }

        public void SetPersistentDlaaConfig(DlaaConfig config)
        {
            DlaaConfig previous = DlaaConfig;
            _dlaaSettings.SetSaved(config);
            ApplyDlaaConfig(previous);
        }

        private void ApplyDlaaConfig(DlaaConfig previous)
        {
            DlaaConfig config = DlaaConfig;
            if (previous.ValuesEqual(in config))
            {
                return;
            }
            bool recreate = previous.RequiresContextRecreation(in config);
            bool resetHistory = previous.RequiresHistoryReset(in config);
            _performanceProfiler.Invalidate(BackendSelection.NvidiaDlaa);
            _dlaaBackend.ApplyConfig(in config);
            _dlssBackend.ApplyConfig(in config);
            _performanceProfiler.Invalidate(BackendSelection.NvidiaDlss);
            if ((_activeBackend == _dlaaBackend || _activeBackend == _dlssBackend) && _activeBackend.Active)
            {
                if (recreate)
                {
                    DisableActiveBackend();
                    ScheduleDiscovery();
                }
                else if (resetHistory)
                {
                    ResetActiveHistory(HistoryResetReason.SettingsChanged);
                }
            }
            else if (_requestedBackend == BackendSelection.NvidiaDlaa || _requestedBackend == BackendSelection.NvidiaDlss)
            {
                ScheduleDiscovery();
            }
        }

        public void SetFsr2Config(Fsr2Config config)
        {
            if (_fsr2Config.ValuesEqual(in config))
            {
                return;
            }
            bool recreate = _fsr2Config.RequiresContextRecreation(in config);
            bool resetHistory = _fsr2Config.RequiresHistoryReset(in config);
            _fsr2Config = config;
            _performanceProfiler.Invalidate(BackendSelection.AmdFsr2);
            _fsr2Backend.ApplyConfig(in _fsr2Config);
            _fsrUpscalingBackend.ApplyConfig(in _fsr2Config);
            _performanceProfiler.Invalidate(BackendSelection.AmdFsrUpscaling);
            if ((_activeBackend == _fsr2Backend || _activeBackend == _fsrUpscalingBackend) && _activeBackend.Active)
            {
                if (recreate)
                {
                    DisableActiveBackend();
                    ScheduleDiscovery();
                }
                else if (resetHistory)
                {
                    ResetActiveHistory(HistoryResetReason.SettingsChanged);
                }
            }
            else if (_requestedBackend == BackendSelection.AmdFsr2 || _requestedBackend == BackendSelection.AmdFsrUpscaling)
            {
                ScheduleDiscovery();
            }
        }

        public void RequestHistoryReset()
        {
            if (_activeBackend.Active)
            {
                ResetActiveHistory(HistoryResetReason.Manual);
            }
        }

        public void NotifyMotionInputChanged()
        {
            if (_disposed)
            {
                return;
            }
            _performanceProfiler.InvalidateAll();
            if (_activeBackend.Active)
            {
                ResetActiveHistory(HistoryResetReason.SettingsChanged);
            }
            else
            {
                _pendingResetReasons |= HistoryResetReason.SettingsChanged;
            }
        }

        public void SetMotionVectorSanitizerEnabled(bool enabled)
        {
            if (!_motionVectorSanitizer.SetEnabled(enabled))
            {
                return;
            }
            NotifyMotionInputChanged();
        }

        public void SetMapViewAaEnabled(bool enabled)
        {
            if (_disposed || _mapViewAaEnabled == enabled)
            {
                return;
            }
            _mapViewAaEnabled = enabled;
            _performanceProfiler.InvalidateAll();
            if (_cameras == null ||
                _cameras.SceneKind != TemporalSceneKind.Map)
            {
                return;
            }

            DisableActiveBackend();
            _pendingResetReasons |= HistoryResetReason.SettingsChanged;
            ScheduleDiscovery();
            _status = enabled
                ? "Map-view AA enabled; restoring the selected mode..."
                : "Map-view AA disabled; switching map view to Off...";
        }

        public void NotifyOriginRebased()
        {
            ComparisonReset?.Invoke(HistoryResetReason.OriginRebased);
            if (_disposed || !_activeBackend.Active)
            {
                return;
            }

            // KSP moves the physics coordinate origin every 1000 metres (and at
            // high relative velocity). Keep the expensive camera graph and vendor
            // context intact, but tell the temporal backend not to reuse samples
            // described in the previous coordinate space. Independent effects
            // receive the same game message and own their own rebase handling.
            _pendingResetReasons |= HistoryResetReason.OriginRebased;
            _originRebasedPending = true;
            _lastOriginRebaseUnityFrame = Time.frameCount;
        }

        public void SetRequestedBackend(BackendSelection requested)
        {
            if (_disposed) return;
            bool reclaimed = _renderScale.Reclaim();
            requested = UserSettingsPolicy.NormalizeBackend(requested);
            if (requested < BackendSelection.Off || (int)requested >= _backends.Length)
                requested = BackendSelection.Off;
            if (_requestedBackend == requested)
            {
                if (reclaimed) MarkDirty(HistoryResetReason.BackendChanged);
                return;
            }

            DisableActiveBackend();
            _requestedBackend = requested;
            _pendingResetReasons |= HistoryResetReason.BackendChanged;
            _frameIndex = 0;
            if (requested == BackendSelection.CustomTaa)
                _customBackend.ClearRuntimeFailure();
            if (requested == BackendSelection.NvidiaDlaa)
            {
                _dlaaBackend.ClearRuntimeFailure();
            }
            if (requested == BackendSelection.AmdFsr2)
            {
                _fsr2Backend.ClearRuntimeFailure();
            }
            if (requested == BackendSelection.NvidiaDlss) _dlssBackend.ClearRuntimeFailure();
            if (requested == BackendSelection.AmdFsrUpscaling) _fsrUpscalingBackend.ClearRuntimeFailure();
            ScheduleDiscovery();
            _status = BackendName(requested) +
                " requested; discovering the final scene camera...";
        }

        public void MarkDirty(HistoryResetReason reason)
        {
            ComparisonReset?.Invoke(reason);
            if (_disposed)
            {
                return;
            }
            _pendingResetReasons |= reason;
            if ((reason & (HistoryResetReason.SceneChanged |
                           HistoryResetReason.ResolutionChanged |
                           HistoryResetReason.RenderScaleChanged)) != 0)
            {
                _performanceProfiler.InvalidateAll();
            }
            ScheduleDiscovery();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _performanceProfiler.Cancel();
            MapIconOverlay.Detach(ref _mapIcons);
            _gameEvents.Dispose();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            _sceneOutput.Dispose();
            foreach (ITemporalBackend backend in _backends)
                backend?.Dispose();
            _renderScale.Dispose();
            _motionVectorSanitizer.Dispose();
            _depthDisocclusionMask.Dispose();
            _activeBackend = _disabledBackend;
            _cameras = null;
            _resetTracker.Clear();
        }

        private void RefreshBackend(float now)
        {
            _dirty = false;
            DeactivateTemporalBackends();
            _activeBackend = _disabledBackend;
            _resetTracker.Clear();
            _cameras = TemporalCameraDiscovery.Discover();
            BackendSelection effectiveRequest = EffectiveBackendForScene(
                _requestedBackend,
                _cameras == null
                    ? TemporalSceneKind.Unsupported
                    : _cameras.SceneKind,
                _mapViewAaEnabled
            );

            string transportReason = string.Empty;
            if (IsUpscaling(effectiveRequest) &&
                (_cameras == null || (_cameras.SceneKind != TemporalSceneKind.Flight && _cameras.SceneKind != TemporalSceneKind.KerbalSpaceCenter)))
            {
                transportReason = "upscaling is not supported in this scene; native AA selected";
                effectiveRequest = effectiveRequest == BackendSelection.NvidiaDlss
                    ? BackendSelection.NvidiaDlaa : BackendSelection.AmdFsr2;
            }
            if (IsUpscaling(effectiveRequest) && !_sceneOutput.TryAcquire(_cameras.ResolveCamera, out transportReason))
            {
                ActivateOffFallback("Upscaling", transportReason);
                return;
            }
            int renderPercent = effectiveRequest == BackendSelection.Supersampling ? _supersamplingPercent :
                IsUpscaling(effectiveRequest) ? ReconstructionPolicy.RenderPercent(_reconstructionQuality) : 100;
            if (effectiveRequest == BackendSelection.NvidiaDlss &&
                !_dlssBackend.TryGetRenderPercent(Screen.width, Screen.height, _reconstructionQuality,
                    out renderPercent, out string sizingReason))
            {
                ActivateOffFallback("DLSS", sizingReason);
                return;
            }
            if (!_renderScale.Apply(renderPercent))
            {
                ActivateOffFallback("AA", "another render-scale owner changed the scene; reselect a mode to reclaim");
                return;
            }
            if (IsUpscaling(effectiveRequest) && !_sceneOutput.EnsureReady(out transportReason))
            {
                ActivateOffFallback("Upscaling", transportReason);
                return;
            }
            _cameras = TemporalCameraDiscovery.Discover();
            VegetationMotionCompatibility.Current?.SetEnabled(
                VegetationRepairRequested && effectiveRequest != BackendSelection.Off &&
                effectiveRequest != BackendSelection.Supersampling);

            _customBackend.ApplyConfig(in _customConfig);
            _dlaaSettings.SetScene(_cameras != null && _cameras.SceneKind == TemporalSceneKind.MainMenu);
            DlaaConfig dlaaConfig = DlaaConfig;
            _dlaaBackend.ApplyConfig(in dlaaConfig);
            _fsr2Backend.ApplyConfig(in _fsr2Config);
            _dlssBackend.ApplyConfig(in dlaaConfig);
            _fsrUpscalingBackend.ApplyConfig(in _fsr2Config);
            _dlssBackend.ConfigureReconstruction(_reconstructionQuality);
            _fsrUpscalingBackend.ConfigureReconstruction(_reconstructionQuality);
            ITemporalBackend requestedBackend = GetBackend(effectiveRequest);

            string failureReason;
            if (!TryConfigure(requestedBackend, _cameras, out failureReason))
            {
                if (effectiveRequest == BackendSelection.NvidiaDlaa || effectiveRequest == BackendSelection.NvidiaDlss)
                {
                    ActivateOffFallback("DLAA", failureReason);
                    return;
                }
                if (effectiveRequest == BackendSelection.AmdFsr2 || effectiveRequest == BackendSelection.AmdFsrUpscaling)
                {
                    ActivateOffFallback("AMD FSR", failureReason);
                    return;
                }

                _status = requestedBackend.Id + " unavailable: " + failureReason;
                _remainingDiscoveryRetries--;
                _dirty = _remainingDiscoveryRetries > 0;
                if (_dirty)
                {
                    _discoverAfter = now + DiscoveryRetrySeconds;
                    return;
                }
                if (effectiveRequest != BackendSelection.Off)
                {
                    ActivateOffFallback(
                        BackendName(effectiveRequest),
                        failureReason
                    );
                }
                return;
            }

            _activeBackend = requestedBackend;
            if (effectiveRequest != BackendSelection.Off && _cameras.SceneKind == TemporalSceneKind.Map &&
                _cameras.ResolveCamera != null && _cameras.ResolveCamera.name == "MapCamera")
                _mapIcons = MapIconOverlay.Attach(_cameras.ResolveCamera);
            if (effectiveRequest == BackendSelection.Off)
            {
                bool mapOverride = _requestedBackend != BackendSelection.Off &&
                    _cameras != null &&
                    _cameras.SceneKind == TemporalSceneKind.Map &&
                    !_mapViewAaEnabled;
                _status = mapOverride
                    ? "Map-view AA disabled; Off active on " +
                      (_cameras.ResolveCamera == null
                          ? "the map renderer"
                          : _cameras.ResolveCamera.name) +
                      "; selected flight mode remains " +
                      BackendName(_requestedBackend)
                    : _cameras != null && _cameras.ResolveCamera != null
                        ? "Off; PPv2 AA disabled on " +
                          _cameras.ResolveCamera.name
                        : "Off; no supported scene AA layer is active";
                _logger.LogInfo("[ReduxBetterAA/Backend] " + _status + ".");
                return;
            }

            _pendingResetReasons |=
                HistoryResetReason.FirstFrame | HistoryResetReason.BackendChanged;
            _status = requestedBackend.Id + " active on " +
                _cameras.ResolveCamera.name +
                (_cameras.SharedJitterCamera != null &&
                 _cameras.SharedJitterCamera != _cameras.ResolveCamera
                    ? " with synchronized " + _cameras.SharedJitterCamera.name
                    : string.Empty) + (string.IsNullOrEmpty(transportReason) ? string.Empty : "; " + transportReason);
            _logger.LogInfo("[ReduxBetterAA/Backend] " + _status + ".");
        }

        internal static BackendSelection EffectiveBackendForScene(
            BackendSelection requested,
            TemporalSceneKind sceneKind,
            bool mapViewAaEnabled)
        {
            return sceneKind == TemporalSceneKind.Map && !mapViewAaEnabled
                ? BackendSelection.Off
                : requested;
        }

        private void ActivateOffFallback(
            string requestedName,
            string failure)
        {
            DeactivateTemporalBackends();
            _activeBackend = _disabledBackend;
            _renderScale.Apply(100);
            VegetationMotionCompatibility.Current?.SetEnabled(false);

            string offFailure;
            if (_disabledBackend.Configure(_cameras, out offFailure))
            {
                _status = requestedName + " unavailable (" + failure +
                    "); Off fallback active" +
                    (_cameras != null && _cameras.ResolveCamera != null
                        ? " on " + _cameras.ResolveCamera.name
                        : string.Empty);
                _logger.LogWarning("[ReduxBetterAA/Backend] " + _status + ".");
                return;
            }

            _status = requestedName + " unavailable (" + failure +
                "); Off fallback could not claim scene AA: " + offFailure;
            _logger.LogWarning("[ReduxBetterAA/Backend] " + _status + ".");
        }

        private void DisableActiveBackend()
        {
            DeactivateTemporalBackends();
            _activeBackend = _disabledBackend;
            _cameras = null;
            _dirty = false;
            _resetTracker.Clear();
        }

        private void DeactivateTemporalBackends()
        {
            _sceneOutput.Dispose();
            MapIconOverlay.Detach(ref _mapIcons);
            foreach (ITemporalBackend backend in _backends)
                backend?.Deactivate();
            _motionVectorSanitizer.ReleaseResources();
            _depthDisocclusionMask.ReleaseResources();
        }

        private void ResetActiveHistory(HistoryResetReason reason)
        {
            _lastResetReason = reason;
            _lastResetUnityFrame = Time.frameCount;
            _activeBackend.ResetHistory(reason);
            _logger.LogInfo(
                "[ReduxBetterAA/History] Reset " +
                _activeBackend.Id + " history: " + reason + "."
            );
        }

        private void TracePreviousFrameHitch()
        {
            if (!_activeBackend.Active ||
                Time.unscaledDeltaTime < HitchThresholdSeconds)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextHitchLogTime)
            {
                return;
            }
            _nextHitchLogTime = now + HitchLogCooldownSeconds;

            int frame = Time.frameCount;
            int framesSinceReset = _lastResetUnityFrame < 0
                ? -1
                : frame - _lastResetUnityFrame;
            int framesSinceOriginRebase = _lastOriginRebaseUnityFrame < 0
                ? -1
                : frame - _lastOriginRebaseUnityFrame;
            _logger.LogInfo(
                "[ReduxBetterAA/FramePacing] " +
                (Time.unscaledDeltaTime * 1000.0f).ToString("F1") +
                " ms frame on " + _activeBackend.Id +
                "; framesSinceHistoryReset=" + framesSinceReset +
                "; framesSinceOriginRebase=" + framesSinceOriginRebase + "."
            );
        }

        private void PollGameState()
        {
            float now = Time.unscaledTime;
            if (now < _nextGameStatePollTime)
            {
                return;
            }
            _nextGameStatePollTime = now + GameStatePollSeconds;
            _gameEvents.Refresh();

            GameState state = TemporalCameraDiscovery.ReadCurrentGameState();
            if (_lastGameState != GameState.Invalid &&
                state != GameState.Invalid &&
                state != _lastGameState)
            {
                MarkDirty(HistoryResetReason.SceneChanged);
            }
            _lastGameState = state;
        }

        private void OnTemporalResourceAvailabilityChanged()
        {
            if (!_disposed &&
                (_requestedBackend == BackendSelection.CustomTaa ||
                 (_requestedBackend == BackendSelection.NvidiaDlaa &&
                  _activeBackend != _dlaaBackend) ||
                 (_requestedBackend == BackendSelection.AmdFsr2 &&
                  _activeBackend != _fsr2Backend) ||
                 (_requestedBackend == BackendSelection.NvidiaDlss && _activeBackend != _dlssBackend) ||
                 (_requestedBackend == BackendSelection.AmdFsrUpscaling && _activeBackend != _fsrUpscalingBackend)))
            {
                MarkDirty(HistoryResetReason.None);
            }
        }

        private void OnRuntimeFailure(BackendSelection mode, string label, string reason)
        {
            if (_disposed || (_requestedBackend != mode && ActiveBackendSelection() != mode))
                return;
            _status = label + " runtime failure (" + reason + "); switching to Off...";
            ScheduleDiscovery(0);
        }

        private void ScheduleDiscovery(int retries = MaximumDiscoveryRetries)
        {
            _dirty = true;
            _remainingDiscoveryRetries = retries;
            _discoverAfter = Time.unscaledTime;
        }

        internal ITemporalBackend GetBackend(BackendSelection selection) =>
            selection >= BackendSelection.Off && (int)selection < _backends.Length
                ? _backends[(int)UserSettingsPolicy.NormalizeBackend(selection)] : _disabledBackend;

        private string BackendName(BackendSelection selection) => GetBackend(selection).Id;

        private BackendSelection ActiveBackendSelection()
        {
            for (int index = 0; index < _backends.Length; index++)
                if (ReferenceEquals(_activeBackend, _backends[index]))
                    return (BackendSelection)index;
            return BackendSelection.Off;
        }

        internal static bool TryConfigure(ITemporalBackend backend, TemporalCameraSet cameras,
            out string failureReason)
        {
            try
            {
                if (backend.Configure(cameras, out failureReason))
                    return true;
            }
            catch (Exception exception)
            {
                failureReason = "configuration failed: " + exception.GetType().Name;
            }
            // Configure may have claimed camera state before an allocation or hook failed.
            backend.Deactivate();
            return false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            MarkDirty(HistoryResetReason.SceneChanged);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            MarkDirty(HistoryResetReason.SceneChanged);
        }

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            MarkDirty(HistoryResetReason.SceneChanged);
        }
    }
}
