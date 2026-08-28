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
        private readonly Ppv2TaaBackend _ppv2Backend = new Ppv2TaaBackend();
        private readonly MotionVectorSanitizer _motionVectorSanitizer;
        private readonly DepthDisocclusionMask _depthDisocclusionMask;
        private readonly CustomTaaBackend _customBackend;
        private readonly NvidiaDlaaBackend _dlaaBackend;
        private readonly AmdFsr2Backend _fsr2Backend;
        private readonly DisabledBackend _disabledBackend = new DisabledBackend();
        private readonly HistoryResetTracker _resetTracker = new HistoryResetTracker();

        private ITemporalBackend _activeBackend;
        private TemporalCameraSet _cameras;
        private BackendSelection _requestedBackend;
        private HistoryResetReason _pendingResetReasons;
        private bool _dirty;
        private bool _disposed;
        private bool _dlaaRuntimeFailed;
        private bool _fsr2RuntimeFailed;
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
        private TemporalBackendConfig _ppv2Config =
            TemporalBackendConfig.ConservativePpv2;
        private CustomTaaConfig _customConfig = CustomTaaConfig.Conservative;
        private DlaaConfig _dlaaConfig = DlaaConfig.Conservative;
        private Fsr2Config _fsr2Config = Fsr2Config.Conservative;
        private string _status = "Off";

        public TemporalCoordinator(ReduxLogger logger, bool requestPpv2Taa)
        {
            _logger = logger;
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
                _motionVectorSanitizer
            );
            _dlaaBackend = new NvidiaDlaaBackend(
                logger,
                OnDlaaRuntimeFailure,
                _performanceProfiler,
                _motionVectorSanitizer,
                _depthDisocclusionMask
            );
            _fsr2Backend = new AmdFsr2Backend(
                logger,
                OnFsr2RuntimeFailure,
                _performanceProfiler,
                _motionVectorSanitizer,
                _depthDisocclusionMask
            );
            _activeBackend = _disabledBackend;
            _requestedBackend = requestPpv2Taa
                ? BackendSelection.Ppv2Taa
                : BackendSelection.Off;
        }

        public bool Requested => _requestedBackend != BackendSelection.Off;
        public BackendSelection RequestedBackend => _requestedBackend;
        public bool Active => _activeBackend.Active;
        public string SelectedBackend => _activeBackend.Id;
        public string ResolveCameraName =>
            _cameras == null || _cameras.ResolveCamera == null
                ? string.Empty
                : _cameras.ResolveCamera.name;
        public string SharedJitterCameraName =>
            _cameras == null || _cameras.SharedJitterCamera == null
                ? string.Empty
                : _cameras.SharedJitterCamera.name;
        public HistoryResetReason LastResetReason => _lastResetReason;
        public TemporalBackendConfig Ppv2Config => _ppv2Config;
        public CustomTaaConfig CustomConfig => _customConfig;
        public DlaaConfig DlaaConfig => _dlaaConfig;
        public Fsr2Config Fsr2Config => _fsr2Config;
        public long CustomEstimatedMemoryBytes => _customBackend.EstimatedMemoryBytes;
        public bool DlaaManagedSurfaceAvailable =>
            _dlaaBackend.ManagedSurfaceAvailable;
        public bool DlaaContextCreated => _dlaaBackend.ContextCreated;
        public uint DlaaDeviceVersion => _dlaaBackend.DeviceVersion;
        public long DlaaEstimatedMemoryBytes => _dlaaBackend.EstimatedMemoryBytes +
            _motionVectorSanitizer.EstimatedMemoryBytes +
            _depthDisocclusionMask.EstimatedMemoryBytes;
        public int DlaaInputWidth => _dlaaBackend.InputWidth;
        public int DlaaInputHeight => _dlaaBackend.InputHeight;
        public int DlaaOutputWidth => _dlaaBackend.OutputWidth;
        public int DlaaOutputHeight => _dlaaBackend.OutputHeight;
        public string DlaaOutputGraphicsFormat => _dlaaBackend.OutputGraphicsFormat;
        public bool DlaaOutputRandomWrite => _dlaaBackend.OutputRandomWrite;
        public string DlaaLastFailure => _dlaaBackend.LastFailure;
        public string DlaaExposureSource => _dlaaBackend.ExposureSource;
        public float DlaaEffectivePreExposure =>
            _dlaaBackend.EffectivePreExposure;
        public bool Fsr2ManagedSurfaceAvailable =>
            _fsr2Backend.ManagedSurfaceAvailable;
        public bool Fsr2ContextCreated => _fsr2Backend.ContextCreated;
        public uint Fsr2DeviceVersion => _fsr2Backend.DeviceVersion;
        public long Fsr2EstimatedMemoryBytes => _fsr2Backend.EstimatedMemoryBytes +
            _motionVectorSanitizer.EstimatedMemoryBytes +
            _depthDisocclusionMask.EstimatedMemoryBytes;
        public int Fsr2InputWidth => _fsr2Backend.InputWidth;
        public int Fsr2InputHeight => _fsr2Backend.InputHeight;
        public int Fsr2OutputWidth => _fsr2Backend.OutputWidth;
        public int Fsr2OutputHeight => _fsr2Backend.OutputHeight;
        public string Fsr2OutputGraphicsFormat => _fsr2Backend.OutputGraphicsFormat;
        public bool Fsr2OutputRandomWrite => _fsr2Backend.OutputRandomWrite;
        public string Fsr2LastFailure => _fsr2Backend.LastFailure;
        public string Fsr2ExposureSource => _fsr2Backend.ExposureSource;
        public float Fsr2EffectivePreExposure =>
            _fsr2Backend.EffectivePreExposure;
        public Vector2 Fsr2ProjectionJitterPixels =>
            _fsr2Backend.ProjectionJitterPixels;
        public Vector2 Fsr2DispatchJitterPixels =>
            _fsr2Backend.DispatchJitterPixels;
        public string MotionVectorSanitizerStatus => _motionVectorSanitizer.Status;
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
                if (_activeBackend == _dlaaBackend)
                {
                    return _dlaaBackend.CurrentJitterNormalized;
                }
                if (_activeBackend == _fsr2Backend)
                {
                    return _fsr2Backend.CurrentJitterNormalized;
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
                if (_dlaaBackend.ContextCreated)
                {
                    return "Context active; API v" + _dlaaBackend.DeviceVersion +
                        "; input " + _dlaaBackend.InputWidth + "x" +
                        _dlaaBackend.InputHeight + "; output " +
                        _dlaaBackend.OutputWidth + "x" +
                        _dlaaBackend.OutputHeight + " " +
                        _dlaaBackend.OutputGraphicsFormat +
                        (_dlaaBackend.OutputRandomWrite ? " UAV; " : " (not UAV); ") +
                        _dlaaBackend.ExposureSource + " " +
                        _dlaaBackend.EffectivePreExposure.ToString("0.000") + "; " +
                        _motionVectorSanitizer.Status + "; " +
                        _depthDisocclusionMask.Status;
                }
                if (!string.IsNullOrEmpty(_dlaaBackend.LastFailure))
                {
                    return "Unavailable: " + _dlaaBackend.LastFailure;
                }
                return _dlaaBackend.ManagedSurfaceAvailable
                    ? "Managed Unity NVIDIA API found; context is created on first render."
                    : "Managed Unity NVIDIA API was not found.";
            }
        }
        public string Fsr2Details
        {
            get
            {
                if (_fsr2Backend.ContextCreated)
                {
                    return "Context active; API v" + _fsr2Backend.DeviceVersion +
                        "; input " + _fsr2Backend.InputWidth + "x" +
                        _fsr2Backend.InputHeight + "; output " +
                        _fsr2Backend.OutputWidth + "x" +
                        _fsr2Backend.OutputHeight + " " +
                        _fsr2Backend.OutputGraphicsFormat +
                        (_fsr2Backend.OutputRandomWrite ? " UAV; " : " (not UAV); ") +
                        _fsr2Backend.ExposureSource + " " +
                        _fsr2Backend.EffectivePreExposure.ToString("0.000") + "; " +
                        _motionVectorSanitizer.Status + "; " +
                        _depthDisocclusionMask.Status;
                }
                if (!string.IsNullOrEmpty(_fsr2Backend.LastFailure))
                {
                    return "Unavailable: " + _fsr2Backend.LastFailure;
                }
                return _fsr2Backend.ManagedSurfaceAvailable
                    ? "Managed Unity AMD FSR2 API found; context is created on first render."
                    : "Managed Unity AMD FSR2 API was not found.";
            }
        }
        public string Status => _status;

        public PerformanceProfileSnapshot GetPerformanceProfile(
            BackendSelection mode)
        {
            return _performanceProfiler.GetSnapshot(mode);
        }

        public void StartPerformanceProfile(BackendSelection mode)
        {
            _performanceProfiler.Start(mode);
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
            _dirty = true;
            _remainingDiscoveryRetries = MaximumDiscoveryRetries;
            _discoverAfter = Time.unscaledTime;
            _pendingResetReasons =
                HistoryResetReason.FirstFrame | HistoryResetReason.BackendChanged;
            if (!Requested)
            {
                _status = "Off requested; discovering the final scene camera...";
            }
        }

        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            TracePreviousFrameHitch();
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
                _lastResetReason = reasons;
                _lastResetUnityFrame = Time.frameCount;
                _activeBackend.ResetHistory(reasons);
                _logger.LogInfo(
                    "[ReduxBetterAA/History] Reset " +
                    _activeBackend.Id + " history: " + reasons + "."
                );
            }

            _activeBackend.Tick(_frameIndex++);
        }

        public void CycleRequestedBackend()
        {
            BackendSelection next;
            switch (_requestedBackend)
            {
                case BackendSelection.Off:
                    next = BackendSelection.FxaaLow;
                    break;
                case BackendSelection.FxaaLow:
                    next = BackendSelection.FxaaHigh;
                    break;
                case BackendSelection.FxaaHigh:
                    next = BackendSelection.Smaa;
                    break;
                case BackendSelection.Smaa:
                    next = BackendSelection.Ppv2Taa;
                    break;
                case BackendSelection.Ppv2Taa:
                    next = BackendSelection.CustomTaa;
                    break;
                case BackendSelection.CustomTaa:
                    next = BackendSelection.NvidiaDlaa;
                    break;
                case BackendSelection.NvidiaDlaa:
                    next = BackendSelection.AmdFsr2;
                    break;
                default:
                    next = BackendSelection.Off;
                    break;
            }
            SetRequestedBackend(next);
        }

        public void SetPpv2Config(TemporalBackendConfig config)
        {
            if (_ppv2Config.ValuesEqual(in config))
            {
                return;
            }
            _ppv2Config = config;
            _performanceProfiler.Invalidate(BackendSelection.Ppv2Taa);
            _ppv2Backend.ApplyConfig(in _ppv2Config);
            if (_activeBackend == _ppv2Backend && _activeBackend.Active)
            {
                ResetActiveHistory(HistoryResetReason.SettingsChanged);
            }
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
            if (_dlaaConfig.ValuesEqual(in config))
            {
                return;
            }
            bool recreate = _dlaaConfig.RequiresContextRecreation(in config);
            bool resetHistory = _dlaaConfig.RequiresHistoryReset(in config);
            _dlaaConfig = config;
            _performanceProfiler.Invalidate(BackendSelection.NvidiaDlaa);
            _dlaaBackend.ApplyConfig(in _dlaaConfig);
            if (_activeBackend == _dlaaBackend && _activeBackend.Active)
            {
                if (recreate)
                {
                    DisableActiveBackend();
                    _dirty = true;
                    _remainingDiscoveryRetries = MaximumDiscoveryRetries;
                    _discoverAfter = Time.unscaledTime;
                }
                else if (resetHistory)
                {
                    ResetActiveHistory(HistoryResetReason.SettingsChanged);
                }
            }
            else if (_requestedBackend == BackendSelection.NvidiaDlaa)
            {
                _dirty = true;
                _remainingDiscoveryRetries = MaximumDiscoveryRetries;
                _discoverAfter = Time.unscaledTime;
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
            if (_activeBackend == _fsr2Backend && _activeBackend.Active)
            {
                if (recreate)
                {
                    DisableActiveBackend();
                    _dirty = true;
                    _remainingDiscoveryRetries = MaximumDiscoveryRetries;
                    _discoverAfter = Time.unscaledTime;
                }
                else if (resetHistory)
                {
                    ResetActiveHistory(HistoryResetReason.SettingsChanged);
                }
            }
            else if (_requestedBackend == BackendSelection.AmdFsr2)
            {
                _dirty = true;
                _remainingDiscoveryRetries = MaximumDiscoveryRetries;
                _discoverAfter = Time.unscaledTime;
            }
        }

        public void RestoreConservativePpv2Preset()
        {
            SetPpv2Config(TemporalBackendConfig.ConservativePpv2);
        }

        public void RestoreConservativeCustomPreset()
        {
            SetCustomConfig(CustomTaaConfig.Conservative);
        }

        public void RestoreConservativeDlaaPreset()
        {
            SetDlaaConfig(DlaaConfig.Conservative);
        }

        public void RestoreConservativeFsr2Preset()
        {
            SetFsr2Config(Fsr2Config.Conservative);
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

        public void NotifyOriginRebased()
        {
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
            if (requested < BackendSelection.Off ||
                requested > BackendSelection.AmdFsr2)
            {
                requested = BackendSelection.Off;
            }
            if (_requestedBackend == requested)
            {
                return;
            }

            DisableActiveBackend();
            _requestedBackend = requested;
            _pendingResetReasons |= HistoryResetReason.BackendChanged;
            _frameIndex = 0;
            _dlaaRuntimeFailed = false;
            _fsr2RuntimeFailed = false;
            if (requested == BackendSelection.NvidiaDlaa)
            {
                _dlaaBackend.ClearRuntimeFailure();
            }
            if (requested == BackendSelection.AmdFsr2)
            {
                _fsr2Backend.ClearRuntimeFailure();
            }
            _dirty = true;
            _remainingDiscoveryRetries = MaximumDiscoveryRetries;
            _discoverAfter = Time.unscaledTime;
            _status = BackendName(requested) +
                " requested; discovering the final scene camera...";
        }

        public void MarkDirty(HistoryResetReason reason)
        {
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
            _dirty = true;
            _remainingDiscoveryRetries = MaximumDiscoveryRetries;
            _discoverAfter = Time.unscaledTime;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            _ppv2Backend.Dispose();
            _customBackend.Dispose();
            _dlaaBackend.Dispose();
            _fsr2Backend.Dispose();
            _fxaaLowBackend.Dispose();
            _smaaBackend.Dispose();
            _fxaaHighBackend.Dispose();
            _motionVectorSanitizer.Dispose();
            _depthDisocclusionMask.Dispose();
            _disabledBackend.Dispose();
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

            ITemporalBackend requestedBackend;
            if (_requestedBackend == BackendSelection.Off)
            {
                requestedBackend = _disabledBackend;
            }
            else if (_requestedBackend == BackendSelection.FxaaLow)
            {
                requestedBackend = _fxaaLowBackend;
            }
            else if (_requestedBackend == BackendSelection.Smaa)
            {
                requestedBackend = _smaaBackend;
            }
            else if (_requestedBackend == BackendSelection.FxaaHigh)
            {
                requestedBackend = _fxaaHighBackend;
            }
            else if (_requestedBackend == BackendSelection.Ppv2Taa)
            {
                _ppv2Backend.ApplyConfig(in _ppv2Config);
                requestedBackend = _ppv2Backend;
            }
            else if (_requestedBackend == BackendSelection.CustomTaa)
            {
                _customBackend.ApplyConfig(in _customConfig);
                requestedBackend = _customBackend;
            }
            else if (_requestedBackend == BackendSelection.NvidiaDlaa)
            {
                _dlaaBackend.ApplyConfig(in _dlaaConfig);
                requestedBackend = _dlaaBackend;
            }
            else if (_requestedBackend == BackendSelection.AmdFsr2)
            {
                _fsr2Backend.ApplyConfig(in _fsr2Config);
                requestedBackend = _fsr2Backend;
            }
            else
            {
                requestedBackend = _disabledBackend;
            }

            string failureReason;
            if (!requestedBackend.Configure(_cameras, out failureReason))
            {
                if (_requestedBackend == BackendSelection.NvidiaDlaa)
                {
                    ActivateOffFallback("DLAA", failureReason);
                    return;
                }
                if (_requestedBackend == BackendSelection.AmdFsr2)
                {
                    ActivateOffFallback("FSR2", failureReason);
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
                if (_requestedBackend != BackendSelection.Off)
                {
                    ActivateOffFallback(
                        BackendName(_requestedBackend),
                        failureReason
                    );
                }
                return;
            }

            _activeBackend = requestedBackend;
            if (_requestedBackend == BackendSelection.Off)
            {
                _status = _cameras != null && _cameras.ResolveCamera != null
                    ? "Off; PPv2 AA disabled on " + _cameras.ResolveCamera.name
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
                    : string.Empty);
            _logger.LogInfo("[ReduxBetterAA/Backend] " + _status + ".");
        }

        private void ActivateOffFallback(
            string requestedName,
            string failure)
        {
            DeactivateTemporalBackends();
            _activeBackend = _disabledBackend;

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
            _disabledBackend.Deactivate();
            _ppv2Backend.Deactivate();
            _customBackend.Deactivate();
            _dlaaBackend.Deactivate();
            _fsr2Backend.Deactivate();
            _fxaaLowBackend.Deactivate();
            _smaaBackend.Deactivate();
            _fxaaHighBackend.Deactivate();
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

            GameState state = ReadGameState();
            if (_lastGameState != GameState.Invalid &&
                state != GameState.Invalid &&
                state != _lastGameState)
            {
                MarkDirty(HistoryResetReason.SceneChanged);
            }
            _lastGameState = state;
        }

        private static GameState ReadGameState()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null || manager.Game == null ||
                manager.Game.GlobalGameState == null)
            {
                return GameState.Invalid;
            }
            return manager.Game.GlobalGameState.GetGameState().GameState;
        }

        private void OnTemporalResourceAvailabilityChanged()
        {
            if (!_disposed &&
                (_requestedBackend == BackendSelection.CustomTaa ||
                 (_requestedBackend == BackendSelection.NvidiaDlaa &&
                  _activeBackend != _dlaaBackend) ||
                 (_requestedBackend == BackendSelection.AmdFsr2 &&
                  _activeBackend != _fsr2Backend)))
            {
                MarkDirty(HistoryResetReason.None);
            }
        }

        private void OnDlaaRuntimeFailure(string reason)
        {
            if (_disposed || _requestedBackend != BackendSelection.NvidiaDlaa ||
                _dlaaRuntimeFailed)
            {
                return;
            }
            _dlaaRuntimeFailed = true;
            _status = "DLAA runtime failure (" + reason +
                "); switching to the Off fallback...";
            _dirty = true;
            _remainingDiscoveryRetries = 0;
            _discoverAfter = Time.unscaledTime;
        }

        private void OnFsr2RuntimeFailure(string reason)
        {
            if (_disposed || _requestedBackend != BackendSelection.AmdFsr2 ||
                _fsr2RuntimeFailed)
            {
                return;
            }
            _fsr2RuntimeFailed = true;
            _status = "FSR2 runtime failure (" + reason +
                "); switching to the Off fallback...";
            _dirty = true;
            _remainingDiscoveryRetries = 0;
            _discoverAfter = Time.unscaledTime;
        }

        private static string BackendName(BackendSelection selection)
        {
            switch (selection)
            {
                case BackendSelection.FxaaLow:
                    return "FXAA Low";
                case BackendSelection.Smaa:
                    return "SMAA";
                case BackendSelection.FxaaHigh:
                    return "FXAA High";
                case BackendSelection.Ppv2Taa:
                    return "PPv2 TAA";
                case BackendSelection.CustomTaa:
                    return "Custom TAA";
                case BackendSelection.NvidiaDlaa:
                    return "NVIDIA DLAA";
                case BackendSelection.AmdFsr2:
                    return "FSR2 Native AA";
                default:
                    return "Off";
            }
        }

        private BackendSelection ActiveBackendSelection()
        {
            if (_activeBackend == _fxaaLowBackend)
            {
                return BackendSelection.FxaaLow;
            }
            if (_activeBackend == _smaaBackend)
            {
                return BackendSelection.Smaa;
            }
            if (_activeBackend == _fxaaHighBackend)
            {
                return BackendSelection.FxaaHigh;
            }
            if (_activeBackend == _ppv2Backend)
            {
                return BackendSelection.Ppv2Taa;
            }
            if (_activeBackend == _customBackend)
            {
                return BackendSelection.CustomTaa;
            }
            if (_activeBackend == _dlaaBackend)
            {
                return BackendSelection.NvidiaDlaa;
            }
            if (_activeBackend == _fsr2Backend)
            {
                return BackendSelection.AmdFsr2;
            }
            return BackendSelection.Off;
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
