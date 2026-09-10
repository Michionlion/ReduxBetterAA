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
        internal bool HotkeysEnabled { get; set; }
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
        private IssueReportCapture _issueReports;
        private float _reportNoticeUntil;

        internal bool IssueReportBusy => _issueReports != null && _issueReports.Busy;
        internal string LastIssueReport => _issueReports?.LastZipPath;
        internal int TemporalInputCaptureCount => _issueReports?.TemporalInputCaptureCount ?? 0;
        internal void SetPanelVisible(bool visible) => _visualizer.SetPanelVisible(visible);
        internal bool RequestIssueReport() => !_visualizer.CaptureBusy && _resumePanelAtFrame < 0 &&
            _issueReports != null && _issueReports.Request();

        internal void InitializeIssueReports(MonoBehaviour host)
        {
            _issueReports = new IssueReportCapture(host, BuildReport, status =>
            {
                _visualizer.SetScreenshotStatus(status);
                _reportNoticeUntil = Time.unscaledTime + 30f;
                _logger.LogInfo("[ReduxBetterAA/Report] " + status);
            }, suspended =>
            {
                if (suspended) _visualizer.SuspendPanelForScreenshot();
                else _visualizer.ResumePanelAfterScreenshot();
            });
            _visualizer.CreateIssueReport = RequestIssueReport;
            _visualizer.IssueReportBusy = () => IssueReportBusy;
        }

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
            HotkeysEnabled = hotkeys;
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
            _issueReports?.Tick();

            if (_resumePanelAtFrame >= 0 && Time.frameCount >= _resumePanelAtFrame)
            {
                _resumePanelAtFrame = -1;
                _visualizer.ResumePanelAfterScreenshot();
            }

            if (HotkeysEnabled)
            {
                if (Input.GetKeyDown(KeyCode.F10))
                {
                    if (DiagnosticHotkeys.ControlDown() && !DiagnosticHotkeys.ShiftDown() && !DiagnosticHotkeys.AltDown())
                    {
                        _visualizer.TogglePanel();
                    }
                    else if (DiagnosticHotkeys.ShiftDown() && !DiagnosticHotkeys.ControlDown() && !DiagnosticHotkeys.AltDown())
                    {
                        _visualizer.RequestScreenshot();
                    }
                    else if (!DiagnosticHotkeys.AnyModifierDown())
                    {
                        RequestIssueReport();
                    }
                }
                if (DiagnosticHotkeys.ControlDown() && DiagnosticHotkeys.AltDown() && !DiagnosticHotkeys.ShiftDown() && Input.GetKeyDown(KeyCode.F8))
                {
                    CaptureNow(ProbeDirtyReason.Manual);
                }
            }

            if (IssueReportBusy)
                return;
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
                if (_issueReports != null && !IssueReportBusy && Time.unscaledTime < _reportNoticeUntil)
                {
                    GUILayout.BeginArea(new Rect(12f, Screen.height - 125f,
                        Mathf.Min(640f, Screen.width - 24f), 112f), GUI.skin.box);
                    GUILayout.Label(_issueReports.Status);
                    if (GUILayout.Button("Open reports folder"))
                        Application.OpenURL(new Uri(Path.Combine(GetReportDirectory(), "reports")).AbsoluteUri);
                    GUILayout.EndArea();
                }
            }
        }

        public void SetTemporalControls(BackendSettingsPanel panel)
        {
            _visualizer.SetTemporalControls(panel);
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

        public void SetMotionInputControls(
            Func<bool> vegetationRepairEnabled,
            Action<bool> setVegetationRepairEnabled,
            Func<string> vegetationRepairStatus,
            Func<long> vegetationRepairReroutedCalls,
            Func<bool> sanitizerEnabled,
            Action<bool> setSanitizerEnabled,
            Func<string> sanitizerStatus)
        {
            _visualizer.SetMotionInputControls(
                vegetationRepairEnabled,
                setVegetationRepairEnabled,
                vegetationRepairStatus,
                vegetationRepairReroutedCalls,
                sanitizerEnabled,
                setSanitizerEnabled,
                sanitizerStatus
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
            _issueReports?.Dispose();
            _visualizer.Dispose();
        }

        internal void RestoreMotionVectorPassProbe()
        {
            _visualizer.RestoreMotionVectorPassProbe();
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
                _visualizer.SetCandidates(CameraDiscovery.CaptureDebugCandidates());
                Phase1Report report = BuildReport(_visualizer.SelectedCameraForDiagnostics);
                report.captureReason = reasons.ToString();
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

        private Phase1Report BuildReport(Camera camera)
        {
            CameraDiscoveryResult discovery = CameraDiscovery.Capture(++_revision);
            if (_capabilities == null)
                _capabilities = VendorCapabilityProbe.Capture(_probeVendorRuntime);
            return new Phase1Report
            {
                schemaVersion = 23,
                frame = Time.frameCount,
                capturedUtc = DateTime.UtcNow.ToString("O"),
                captureReason = "IssueReport",
                runtime = CapabilityReportBuilder.CaptureRuntime(_metadata),
                capabilities = _capabilities,
                cameraGraph = discovery.Graph,
                evidence = CapabilityReportBuilder.BuildEvidence(discovery.Graph),
                motionCadence = CapabilityReportBuilder.CaptureMotionCadence(),
                motionSignDiagnostic = _visualizer.CaptureMotionSignDiagnostic(),
                cloud = CloudDiagnosticCapture.CaptureRecord(camera),
                temporal = CapabilityReportBuilder.CaptureTemporalBackend()
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
                string captureBaseName = Path.GetFileNameWithoutExtension(fileName);
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
                string cloudCaptureStatus;
                int cloudCaptureCount = CloudDiagnosticCapture.CaptureTextures(
                    _visualizer.SelectedCameraForDiagnostics,
                    screenshotDirectory,
                    captureBaseName,
                    out cloudCaptureStatus
                );
                _resumePanelAtFrame = Time.frameCount + 2;
                string cloudStatusSuffix = cloudCaptureCount > 0
                    ? "; cloud source images: " + cloudCaptureCount
                    : "; cloud source images unavailable: " + cloudCaptureStatus;
                _visualizer.SetScreenshotStatus(
                    statisticsArmed
                        ? "Screenshot + motion statistics queued: " + fileName +
                          cloudStatusSuffix
                        : statisticsExpected
                            ? "Screenshot queued; statistics unavailable: " +
                              statisticsUnavailableReason + cloudStatusSuffix
                            : "Screenshot queued: " + fileName + cloudStatusSuffix
                );
                _logger.LogInfo(
                    "[ReduxBetterAA/Capture] Screenshot queued at " + path +
                    (statisticsArmed
                        ? "; motion statistics will be written to " + statisticsPath
                        : string.Empty) +
                    "; cloud diagnostics: " + cloudCaptureStatus
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

        internal static string GetReportDirectory()
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
