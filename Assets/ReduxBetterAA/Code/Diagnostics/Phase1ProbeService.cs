using System;
using System.IO;
using System.Reflection;
using KSP.Game;
using Newtonsoft.Json;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using ReduxLib.Logging;
using SpaceWarp2.API.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Diagnostics
{
    [Flags]
    internal enum ProbeDirtyReason
    {
        None = 0,
        Initialized = 1 << 0,
        ModsInitialized = 1 << 1,
        SceneLoaded = 1 << 2,
        SceneUnloaded = 1 << 3,
        ActiveSceneChanged = 1 << 4,
        ResolutionChanged = 1 << 5,
        PresenterChanged = 1 << 6,
        ActiveCameraChanged = 1 << 7,
        GameStateChanged = 1 << 8,
        Manual = 1 << 9,
        MotionInputChanged = 1 << 10
    }

    internal sealed class Phase1ProbeService : IDisposable
    {
        private const float PollIntervalSeconds = 1.0f;
        private const float StabilizationSeconds = 1.5f;
        private const string ReportFolderName = "diagnostics";

        public static Phase1ProbeService Current;

        private readonly ReduxLogger _logger;
        private readonly SpaceWarpPluginDescriptor _metadata;
        private readonly bool _automaticReports;
        private readonly bool _hotkeys;
        private readonly bool _probeVendorRuntime;
        private readonly BufferVisualizer _visualizer;

        private bool _disposed;
        private bool _dirty;
        private ProbeDirtyReason _dirtyReasons;
        private float _captureAfter;
        private float _nextPoll;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private GameState _lastGameState = GameState.Invalid;
        private int _revision;
        private int _reportSequence;
        private int _screenshotSequence;
        private int _resumePanelAtFrame = -1;
        private CapabilityRecord _capabilities;

        public Phase1ProbeService(
            ReduxLogger logger,
            SpaceWarpPluginDescriptor metadata,
            bool automaticReports,
            bool hotkeys,
            bool probeVendorRuntime)
        {
            _logger = logger;
            _metadata = metadata;
            _automaticReports = automaticReports;
            _hotkeys = hotkeys;
            _probeVendorRuntime = probeVendorRuntime;
            _visualizer = new BufferVisualizer(logger);
        }

        public void Initialize()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            _visualizer.Initialize();
            MarkDirty(ProbeDirtyReason.Initialized);
        }

        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            if (_resumePanelAtFrame >= 0 && Time.frameCount >= _resumePanelAtFrame)
            {
                _resumePanelAtFrame = -1;
                _visualizer.ResumePanelAfterScreenshot();
            }

            if (_hotkeys)
            {
                if (Input.GetKeyDown(KeyCode.F10))
                {
                    if (ControlDown() && !ShiftDown() && !AltDown())
                    {
                        _visualizer.TogglePanel();
                    }
                    else if (!AnyModifierDown())
                    {
                        _visualizer.RequestScreenshot();
                    }
                }
                if (ModifiersDown() && Input.GetKeyDown(KeyCode.F8))
                {
                    CaptureNow(ProbeDirtyReason.Manual);
                }
            }

            _visualizer.TickMotionDiagnosticBurst(Time.unscaledTime);

            if (_visualizer.ConsumeReportRequest())
            {
                CaptureNow(ProbeDirtyReason.Manual);
            }
            if (_visualizer.ConsumeScreenshotRequest())
            {
                // Keep F10 and the panel button self-contained: every image now
                // receives a same-moment camera/backend report instead of relying
                // on the separate Ctrl+Alt+F8 diagnostic hotkey.
                CaptureNow(ProbeDirtyReason.Manual);
                CaptureScreenshot();
            }

            float now = Time.unscaledTime;
            if (_visualizer.ConsumeCameraRefreshRequest(now))
            {
                _visualizer.SetCandidates(CameraDiscovery.CaptureDebugCandidates());
            }
            if (now >= _nextPoll)
            {
                _nextPoll = now + PollIntervalSeconds;
                PollStableState();
            }

            if (_automaticReports && _dirty && now >= _captureAfter)
            {
                CaptureNow(_dirtyReasons);
            }
        }

        public void MarkDirty(ProbeDirtyReason reason)
        {
            if (_disposed)
            {
                return;
            }
            _dirty = true;
            _dirtyReasons |= reason;
            _captureAfter = Time.unscaledTime + StabilizationSeconds;
        }

        public void DrawGui()
        {
            if (!_disposed)
            {
                _visualizer.DrawGui();
            }
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
            Action resetHistory)
        {
            _visualizer.SetTemporalControls(
                status,
                requestedBackend,
                setRequestedBackend,
                ppv2Config,
                setPpv2Config,
                restorePpv2Preset,
                customConfig,
                setCustomConfig,
                restoreCustomPreset,
                customMemoryBytes,
                dlaaConfig,
                setDlaaConfig,
                restoreDlaaPreset,
                dlaaDetails,
                dlaaMemoryBytes,
                fsr2Config,
                setFsr2Config,
                restoreFsr2Preset,
                fsr2Details,
                fsr2MemoryBytes,
                performanceProfile,
                startPerformanceProfile,
                cancelPerformanceProfile,
                resetHistory
            );
        }

        public void SetMotionCadenceControls(
            Func<bool> interpolationEnabled,
            Action<bool> setInterpolationEnabled,
            Func<string> interpolationStatus,
            Action refreshInterpolation)
        {
            _visualizer.SetMotionCadenceControls(
                interpolationEnabled,
                setInterpolationEnabled,
                interpolationStatus,
                refreshInterpolation
            );
        }

        public void SetMotionSanitizerDiagnostics(
            Func<Texture> sanitizedMotion,
            Func<Texture> corruptionFlag,
            Func<Vector2> currentJitterNormalized)
        {
            _visualizer.SetMotionSanitizerDiagnostics(
                sanitizedMotion,
                corruptionFlag,
                currentJitterNormalized
            );
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
            _visualizer.Dispose();
        }

        private void PollStableState()
        {
            int width = Screen.width;
            int height = Screen.height;
            if (_lastScreenWidth >= 0 &&
                (width != _lastScreenWidth || height != _lastScreenHeight))
            {
                MarkDirty(ProbeDirtyReason.ResolutionChanged);
            }
            _lastScreenWidth = width;
            _lastScreenHeight = height;

            GameState gameState = ReadGameState();
            if (_lastGameState != GameState.Invalid && gameState != _lastGameState)
            {
                MarkDirty(ProbeDirtyReason.GameStateChanged);
            }
            _lastGameState = gameState;
        }

        private void CaptureNow(ProbeDirtyReason reasons)
        {
            try
            {
                _dirty = false;
                _dirtyReasons = ProbeDirtyReason.None;
                _revision++;

                CameraDiscoveryResult discovery = CameraDiscovery.Capture(_revision);
                _visualizer.SetCandidates(discovery.DebugCandidates);
                if (_capabilities == null)
                {
                    _capabilities = VendorCapabilityProbe.Capture(_probeVendorRuntime);
                }

                var report = new Phase1Report
                {
                    schemaVersion = 18,
                    capturedUtc = DateTime.UtcNow.ToString("O"),
                    captureReason = reasons.ToString(),
                    runtime = CaptureRuntime(),
                    capabilities = _capabilities,
                    cameraGraph = discovery.Graph,
                    evidence = BuildEvidence(discovery.Graph),
                    motionCadence = CaptureMotionCadence(),
                    temporal = CaptureTemporalBackend()
                };

                string reportPath = WriteReport(report);
                LogSummary(report, reportPath);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "[ReduxBetterAA/Probe] Capture failed safely: " +
                    exception.GetType().Name + ": " + exception.Message
                );
            }
        }

        private RuntimeRecord CaptureRuntime()
        {
            string reduxVersion = "Unavailable";
            var plugins = PluginList.AllEnabledAndActivePlugins;
            for (int index = 0; index < plugins.Count; index++)
            {
                SpaceWarpPluginDescriptor descriptor = plugins[index];
                if (descriptor == null || descriptor.SWInfo == null)
                {
                    continue;
                }
                if (string.Equals(
                        descriptor.Guid,
                        "Ksp2Redux",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        descriptor.Name,
                        "KSP2 Redux",
                        StringComparison.OrdinalIgnoreCase))
                {
                    reduxVersion = descriptor.SWInfo.Version;
                    break;
                }
            }

            Version assemblyVersion = typeof(Phase1ProbeService).Assembly.GetName().Version;
            return new RuntimeRecord
            {
                modVersion = _metadata?.SWInfo?.Version ?? assemblyVersion.ToString(),
                gameVersion = Application.version,
                reduxVersion = reduxVersion,
                unityVersion = Application.unityVersion,
                operatingSystem = SystemInfo.operatingSystem,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
                graphicsDeviceId = SystemInfo.graphicsDeviceID,
                graphicsDeviceVendorId = SystemInfo.graphicsDeviceVendorID,
                graphicsMemoryMb = SystemInfo.graphicsMemorySize,
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded
            };
        }

        private static TemporalBackendRecord CaptureTemporalBackend()
        {
            TemporalCoordinator coordinator = TemporalCoordinator.Current;
            if (coordinator == null)
            {
                return new TemporalBackendRecord
                {
                    requestedBackend = "Off",
                    selectedBackend = "Off",
                    active = false,
                    status = "Temporal coordinator unavailable",
                    fallbackReason = "Temporal coordinator unavailable",
                    lastResetReason = HistoryResetReason.None.ToString()
                };
            }

            TemporalBackendConfig ppv2 = coordinator.Ppv2Config;
            CustomTaaConfig custom = coordinator.CustomConfig;
            DlaaConfig dlaa = coordinator.DlaaConfig;
            Fsr2Config fsr2 = coordinator.Fsr2Config;
            MotionVectorMatrixSnapshot matrix =
                coordinator.MotionVectorMatrixSnapshot;
            return new TemporalBackendRecord
            {
                requestedBackend = coordinator.RequestedBackend.ToString(),
                selectedBackend = coordinator.SelectedBackend,
                active = coordinator.Active,
                resolveCamera = coordinator.ResolveCameraName,
                sharedJitterCamera = coordinator.SharedJitterCameraName,
                status = coordinator.Status,
                fallbackReason = coordinator.Requested && !coordinator.Active
                    ? coordinator.Status
                    : string.Empty,
                lastResetReason = coordinator.LastResetReason.ToString(),
                customEstimatedMemoryBytes = coordinator.CustomEstimatedMemoryBytes,
                dlaaEstimatedMemoryBytes = coordinator.DlaaEstimatedMemoryBytes,
                fsr2EstimatedMemoryBytes = coordinator.Fsr2EstimatedMemoryBytes,
                motionVectorSanitizerEstimatedMemoryBytes =
                    coordinator.MotionVectorSanitizerEstimatedMemoryBytes,
                depthDisocclusionMaskEstimatedMemoryBytes =
                    coordinator.DepthDisocclusionMaskEstimatedMemoryBytes,
                vendorMotionRejectionPixels =
                    MotionVectorSanitizer.MaximumMotionPixels,
                motionVectorSanitizerStatus =
                    coordinator.MotionVectorSanitizerStatus,
                motionMatrix = CaptureMotionMatrix(in matrix),
                depthDisocclusionMaskStatus =
                    coordinator.DepthDisocclusionMaskStatus,
                ppv2 = new Ppv2SettingsRecord
                {
                    jitterSpread = ppv2.JitterSpread,
                    sharpness = ppv2.Sharpness,
                    stationaryBlending = ppv2.StationaryBlending,
                    motionBlending = ppv2.MotionBlending
                },
                custom = new CustomTaaSettingsRecord
                {
                    jitterSpread = custom.JitterSpread,
                    sequenceLength = custom.SequenceLength,
                    stationaryHistory = custom.StationaryHistory,
                    movingHistory = custom.MovingHistory,
                    motionResponsePixels = custom.MotionResponsePixels,
                    maximumMotionPixels = custom.MaximumMotionPixels,
                    depthThreshold = custom.DepthThreshold,
                    depthEdgeStability = custom.DepthEdgeStability,
                    varianceGamma = custom.VarianceGamma,
                    reactiveScale = custom.ReactiveScale,
                    sharpening = custom.Sharpening,
                    noDepthHistory = custom.NoDepthHistory,
                    debugView = custom.DebugView.ToString()
                },
                dlaa = new DlaaSettingsRecord
                {
                    jitterSpread = dlaa.JitterSpread,
                    sequenceLength = dlaa.SequenceLength,
                    sharpness = dlaa.Sharpness,
                    preExposure = dlaa.PreExposure,
                    autoExposure = dlaa.AutoExposure,
                    preferPpv2Exposure = dlaa.PreferPpv2Exposure,
                    effectiveExposureSource = coordinator.DlaaExposureSource,
                    effectivePreExposure = coordinator.DlaaEffectivePreExposure,
                    invertMotionX = dlaa.InvertMotionX,
                    invertMotionY = dlaa.InvertMotionY,
                    preset = dlaa.Preset.ToString(),
                    allowSupersampling = dlaa.AllowSupersampling,
                    managedSurfaceAvailable =
                        coordinator.DlaaManagedSurfaceAvailable,
                    contextCreated = coordinator.DlaaContextCreated,
                    deviceVersion = coordinator.DlaaDeviceVersion,
                    inputWidth = coordinator.DlaaInputWidth,
                    inputHeight = coordinator.DlaaInputHeight,
                    outputWidth = coordinator.DlaaOutputWidth,
                    outputHeight = coordinator.DlaaOutputHeight,
                    outputGraphicsFormat = coordinator.DlaaOutputGraphicsFormat,
                    outputRandomWrite = coordinator.DlaaOutputRandomWrite,
                    nativeResolution = coordinator.DlaaInputWidth > 0 &&
                        coordinator.DlaaInputWidth == coordinator.DlaaOutputWidth &&
                        coordinator.DlaaInputHeight == coordinator.DlaaOutputHeight,
                    lastFailure = coordinator.DlaaLastFailure
                },
                fsr2 = new Fsr2SettingsRecord
                {
                    jitterSpread = fsr2.JitterSpread,
                    sequenceLength = fsr2.SequenceLength,
                    enableSharpening = fsr2.EnableSharpening,
                    sharpness = fsr2.Sharpness,
                    preExposure = fsr2.PreExposure,
                    autoExposure = fsr2.AutoExposure,
                    preferPpv2Exposure = fsr2.PreferPpv2Exposure,
                    effectiveExposureSource = coordinator.Fsr2ExposureSource,
                    effectivePreExposure = coordinator.Fsr2EffectivePreExposure,
                    projectionJitterPixels = new[]
                    {
                        coordinator.Fsr2ProjectionJitterPixels.x,
                        coordinator.Fsr2ProjectionJitterPixels.y
                    },
                    dispatchJitterPixels = new[]
                    {
                        coordinator.Fsr2DispatchJitterPixels.x,
                        coordinator.Fsr2DispatchJitterPixels.y
                    },
                    invertMotionX = fsr2.InvertMotionX,
                    invertMotionY = fsr2.InvertMotionY,
                    managedSurfaceAvailable =
                        coordinator.Fsr2ManagedSurfaceAvailable,
                    contextCreated = coordinator.Fsr2ContextCreated,
                    deviceVersion = coordinator.Fsr2DeviceVersion,
                    inputWidth = coordinator.Fsr2InputWidth,
                    inputHeight = coordinator.Fsr2InputHeight,
                    outputWidth = coordinator.Fsr2OutputWidth,
                    outputHeight = coordinator.Fsr2OutputHeight,
                    outputGraphicsFormat = coordinator.Fsr2OutputGraphicsFormat,
                    outputRandomWrite = coordinator.Fsr2OutputRandomWrite,
                    nativeResolution = coordinator.Fsr2InputWidth > 0 &&
                        coordinator.Fsr2InputWidth == coordinator.Fsr2OutputWidth &&
                        coordinator.Fsr2InputHeight == coordinator.Fsr2OutputHeight,
                    lastFailure = coordinator.Fsr2LastFailure
                },
                performance = new PerformanceProfilesRecord
                {
                    off = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.Off
                    ),
                    fxaaLow = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.FxaaLow
                    ),
                    smaa = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.Smaa
                    ),
                    fxaaHigh = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.FxaaHigh
                    ),
                    ppv2 = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.Ppv2Taa
                    ),
                    custom = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.CustomTaa
                    ),
                    dlaa = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.NvidiaDlaa
                    ),
                    fsr2 = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.AmdFsr2
                    )
                }
            };
        }

        private static MotionMatrixRecord CaptureMotionMatrix(
            in MotionVectorMatrixSnapshot snapshot)
        {
            return new MotionMatrixRecord
            {
                frame = snapshot.Frame,
                valid = snapshot.Valid,
                unityCurrentVsTrackedCurrentMaxAbs =
                    snapshot.UnityCurrentVsTrackedCurrentMaxAbs,
                unityPreviousVsTrackedPreviousMaxAbs =
                    snapshot.UnityPreviousVsTrackedPreviousMaxAbs,
                unityPreviousVsCurrentMaxAbs =
                    snapshot.UnityPreviousVsCurrentMaxAbs,
                trackedPreviousVsCurrentMaxAbs =
                    snapshot.TrackedPreviousVsCurrentMaxAbs,
                fieldOfView = snapshot.FieldOfView,
                nearClipPlane = snapshot.NearClipPlane,
                farClipPlane = snapshot.FarClipPlane,
                aspect = snapshot.Aspect,
                currentJitterPixels = new[]
                {
                    snapshot.CurrentJitterPixels.x,
                    snapshot.CurrentJitterPixels.y
                },
                currentJitterNormalized = new[]
                {
                    snapshot.CurrentJitterNormalized.x,
                    snapshot.CurrentJitterNormalized.y
                },
                cameraPosition = new[]
                {
                    snapshot.CameraPosition.x,
                    snapshot.CameraPosition.y,
                    snapshot.CameraPosition.z
                },
                cameraRotation = new[]
                {
                    snapshot.CameraRotation.x,
                    snapshot.CameraRotation.y,
                    snapshot.CameraRotation.z,
                    snapshot.CameraRotation.w
                },
                unityNonJitteredViewProjection = MatrixValues(
                    snapshot.UnityNonJitteredViewProjection
                ),
                unityPreviousViewProjection = MatrixValues(
                    snapshot.UnityPreviousViewProjection
                ),
                trackedCurrentViewProjection = MatrixValues(
                    snapshot.TrackedCurrentViewProjection
                ),
                trackedPreviousViewProjection = MatrixValues(
                    snapshot.TrackedPreviousViewProjection
                )
            };
        }

        private static float[] MatrixValues(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = matrix[index];
            }
            return values;
        }

        private static MotionCadenceRecord CaptureMotionCadence()
        {
            float fixedDeltaTime = Time.fixedDeltaTime;
            KspPhysicsRenderInterpolation interpolation =
                KspPhysicsRenderInterpolation.Current;
            return new MotionCadenceRecord
            {
                fixedDeltaTimeMilliseconds = fixedDeltaTime * 1000.0f,
                fixedUpdateHz = fixedDeltaTime > 0.0f
                    ? 1.0f / fixedDeltaTime
                    : 0.0f,
                experimentalRenderInterpolationEnabled =
                    interpolation != null && interpolation.Enabled,
                interpolatedKspPhysicsBodies = interpolation == null
                    ? 0
                    : interpolation.TrackedBodyCount,
                interpolationStatus = interpolation == null
                    ? "Unavailable"
                    : interpolation.Status
            };
        }

        private static PerformanceProfileRecord CapturePerformanceProfile(
            TemporalCoordinator coordinator,
            BackendSelection mode)
        {
            PerformanceProfileSnapshot snapshot =
                coordinator.GetPerformanceProfile(mode);
            return new PerformanceProfileRecord
            {
                state = snapshot.State.ToString(),
                samples = snapshot.Samples,
                targetSamples = snapshot.TargetSamples,
                averageCpuFrameMilliseconds =
                    snapshot.AverageCpuFrameMilliseconds,
                peakCpuFrameMilliseconds = snapshot.PeakCpuFrameMilliseconds,
                averageGpuFrameMilliseconds =
                    snapshot.AverageGpuFrameMilliseconds,
                peakGpuFrameMilliseconds = snapshot.PeakGpuFrameMilliseconds,
                gpuSamples = snapshot.GpuSamples,
                averageResolveCpuMilliseconds =
                    snapshot.AverageResolveCpuMilliseconds,
                peakResolveCpuMilliseconds =
                    snapshot.PeakResolveCpuMilliseconds,
                resolveSamples = snapshot.ResolveSamples
            };
        }

        private static EvidenceRecord BuildEvidence(CameraGraph graph)
        {
            bool presenterTargetPresent = false;
            bool presenterActive = false;
            ulong presentationCameraId = 0;
            float presentationDepth = float.MinValue;
            for (int index = 0; index < graph.presenters.Length; index++)
            {
                PresenterRecord presenter = graph.presenters[index];
                presenterTargetPresent |= presenter.renderTarget != null && presenter.renderTarget.present;
                presenterActive |= presenter.renderingEnabled;
                if (presenter.presentationCameraId != 0)
                {
                    presentationCameraId = presenter.presentationCameraId;
                }
            }

            bool uiAfterPresentation = false;
            bool motionRequested = false;
            bool sceneDepthAttached = false;
            for (int index = 0; index < graph.cameras.Length; index++)
            {
                CameraRecord camera = graph.cameras[index];
                if (camera.instanceId == presentationCameraId)
                {
                    presentationDepth = camera.depth;
                }
                if (camera.depthTextureMode.IndexOf("MotionVectors", StringComparison.Ordinal) >= 0 ||
                    camera.postProcessCameraFlags.IndexOf("MotionVectors", StringComparison.Ordinal) >= 0)
                {
                    motionRequested = true;
                }
                if ((camera.role.IndexOf("ScaledSpaceStack", StringComparison.Ordinal) >= 0 ||
                     camera.role.IndexOf("PhysicsSpaceStack", StringComparison.Ordinal) >= 0) &&
                    camera.targetTexture != null && camera.targetTexture.present &&
                    camera.targetTexture.depthBits > 0)
                {
                    sceneDepthAttached = true;
                }
            }

            if (presentationDepth > float.MinValue)
            {
                for (int index = 0; index < graph.cameras.Length; index++)
                {
                    CameraRecord camera = graph.cameras[index];
                    if (camera.role.IndexOf("UIOrOverlayCandidate", StringComparison.Ordinal) >= 0 &&
                        camera.enabled && camera.depth > presentationDepth)
                    {
                        uiAfterPresentation = true;
                        break;
                    }
                }
            }

            return new EvidenceRecord
            {
                finalSceneColorCandidate = presenterTargetPresent
                    ? "RenderScalePresenter shared color target; presented by its camera at AfterEverything"
                    : presenterActive
                        ? "RenderScalePresenter active but its shared target was unavailable during capture"
                        : "No active RenderScalePresenter target in this capture",
                uiCompositionCandidate = uiAfterPresentation
                    ? "At least one UI/overlay candidate renders after the presentation camera"
                    : "Not yet demonstrated by camera depth ordering",
                depthStatus = sceneDepthAttached
                    ? "A scene-stack target has a depth attachment; visual coverage still requires capture"
                    : "No shared scene-stack depth attachment demonstrated in this capture",
                motionVectorStatus = motionRequested
                    ? "At least one camera requests motion vectors; visual coverage still requires capture"
                    : "No camera requested motion vectors during this capture",
                resolvePlacementStatus =
                    "Decision 0001 selects one PPv2 resolve on the final scene camera before UI; near-launchpad motion discontinuities require a conservative experimental fallback"
            };
        }

        private string WriteReport(Phase1Report report)
        {
            string reportDirectory = GetReportDirectory();
            Directory.CreateDirectory(reportDirectory);

            _reportSequence++;
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string json = JsonConvert.SerializeObject(report, Formatting.Indented);
            string reportPath = Path.Combine(
                reportDirectory,
                "phase1-" + timestamp + "-" + _reportSequence.ToString("D3") + ".json"
            );
            File.WriteAllText(reportPath, json);
            File.WriteAllText(Path.Combine(reportDirectory, "phase1-latest.json"), json);
            return reportPath;
        }

        private void CaptureScreenshot()
        {
            try
            {
                string screenshotDirectory = Path.Combine(
                    GetReportDirectory(),
                    "screenshots"
                );
                Directory.CreateDirectory(screenshotDirectory);

                _screenshotSequence++;
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string view = MakeSafeFileNameComponent(_visualizer.CurrentViewName);
                string camera = MakeSafeFileNameComponent(_visualizer.SelectedCameraName);
                string fileName =
                    "phase1-" + timestamp + "-" +
                    _screenshotSequence.ToString("D3") + "-" +
                    view + "-" + camera + ".png";
                string path = Path.Combine(screenshotDirectory, fileName);
                string statisticsFileName =
                    Path.GetFileNameWithoutExtension(fileName) + "-motion-stats.json";
                string statisticsPath = Path.Combine(
                    screenshotDirectory,
                    statisticsFileName
                );

                bool statisticsExpected = _visualizer.MotionStatisticsEnabled;
                bool statisticsArmed = false;
                string statisticsUnavailableReason = string.Empty;
                if (statisticsExpected)
                {
                    statisticsArmed = _visualizer.TryArmMotionStatistics(
                        statisticsPath,
                        fileName,
                        out statisticsUnavailableReason
                    );
                }

                _visualizer.SuspendPanelForScreenshot();
                ScreenCapture.CaptureScreenshot(path);
                _resumePanelAtFrame = Time.frameCount + 2;
                _visualizer.SetScreenshotStatus(
                    statisticsArmed
                        ? "Screenshot + motion statistics queued: " + fileName
                        : statisticsExpected
                            ? "Screenshot queued; statistics unavailable: " +
                              statisticsUnavailableReason
                            : "Screenshot queued: " + fileName
                );
                _logger.LogInfo(
                    "[ReduxBetterAA/Capture] Screenshot queued at " + path +
                    (statisticsArmed
                        ? "; motion statistics will be written to " + statisticsPath
                        : string.Empty)
                );
            }
            catch (Exception exception)
            {
                _visualizer.ResumePanelAfterScreenshot();
                _resumePanelAtFrame = -1;
                _visualizer.SetScreenshotStatus(
                    "Screenshot failed: " + exception.GetType().Name
                );
                _logger.LogError(
                    "[ReduxBetterAA/Capture] Screenshot failed safely: " +
                    exception.GetType().Name + ": " + exception.Message
                );
            }
        }

        private static string GetReportDirectory()
        {
            string assemblyDirectory = Path.GetDirectoryName(
                typeof(Phase1ProbeService).Assembly.Location
            );
            string root = string.IsNullOrEmpty(assemblyDirectory)
                ? Application.persistentDataPath
                : assemblyDirectory;
            return Path.Combine(root, ReportFolderName);
        }

        private static string MakeSafeFileNameComponent(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "Unknown";
            }

            char[] characters = value.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int index = 0; index < characters.Length; index++)
            {
                char character = characters[index];
                if (char.IsWhiteSpace(character) ||
                    Array.IndexOf(invalid, character) >= 0)
                {
                    characters[index] = '_';
                }
            }
            return new string(characters);
        }

        private void LogSummary(Phase1Report report, string reportPath)
        {
            CameraGraph graph = report.cameraGraph;
            _logger.LogInfo(
                "[ReduxBetterAA/Probe] Report " + graph.revision + " (" +
                report.captureReason + "): scene=" + graph.activeScene +
                ", state=" + graph.gameState +
                ", group=" + graph.activeCameraGroup +
                ", cameras=" + graph.cameras.Length +
                ", stacks=" + graph.stacks.Length +
                ", presenters=" + graph.presenters.Length + "."
            );
            for (int index = 0; index < graph.cameras.Length; index++)
            {
                CameraRecord camera = graph.cameras[index];
                if (camera.role == "Other")
                {
                    continue;
                }
                _logger.LogInfo(
                    "[ReduxBetterAA/Camera] depth=" + camera.depth.ToString("R") +
                    " role=" + camera.role +
                    " name=" + camera.name +
                    " target=" + (camera.targetTexture.present
                        ? camera.targetTexture.name
                        : "CameraTarget") +
                    " depthMode=" + camera.depthTextureMode +
                    " PPAA=" + camera.postProcessAntialiasing + "."
                );
            }
            _logger.LogInfo("[ReduxBetterAA/Probe] JSON written to " + reportPath);
        }

        private static bool ModifiersDown()
        {
            return ControlDown() && AltDown();
        }

        private static bool ControlDown()
        {
            return Input.GetKey(KeyCode.LeftControl) ||
                   Input.GetKey(KeyCode.RightControl);
        }

        private static bool AltDown()
        {
            return Input.GetKey(KeyCode.LeftAlt) ||
                   Input.GetKey(KeyCode.RightAlt);
        }

        private static bool ShiftDown()
        {
            return Input.GetKey(KeyCode.LeftShift) ||
                   Input.GetKey(KeyCode.RightShift);
        }

        private static bool AnyModifierDown()
        {
            return ShiftDown() ||
                   ControlDown() ||
                   AltDown();
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

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            MarkDirty(ProbeDirtyReason.SceneLoaded);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            MarkDirty(ProbeDirtyReason.SceneUnloaded);
        }

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            MarkDirty(ProbeDirtyReason.ActiveSceneChanged);
        }
    }
}
