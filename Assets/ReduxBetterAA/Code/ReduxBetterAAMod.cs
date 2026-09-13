using System;
using HarmonyLib;
using ReduxBetterAA.Backends.Amd;
using ReduxBetterAA.Backends.Nvidia;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;
using SpaceWarp2.API.Mods;
using UnityEngine;

namespace ReduxBetterAA
{
    /// <summary>
    /// Redux loader entry point for renderer diagnostics and the mutually exclusive
    /// Scene AA selection, native-resolution temporal backends, and diagnostics.
    ///
    /// New installs select custom TAA. While Off is selected, Better AA
    /// explicitly owns a zero-AA baseline; all captured renderer state is
    /// restored when ownership moves or the mod unloads.
    /// </summary>
    public sealed class ReduxBetterAAMod : MonoBehaviourMod
    {
        private static readonly string[] DlaaPresetChoices =
            { "F", "J", "K", "L", "M" };

        private IConfigEntry _modeEntry;
        private IConfigEntry _supersamplingEntry;
        private IConfigEntry _upscalingQualityEntry;
        private IConfigEntry _sharpnessEntry;
        private IConfigEntry _taaStabilityEntry;
        private IConfigEntry _dlaaPresetEntry;
        private IConfigEntry _foliageMotionRepairEntry;
        private IConfigEntry _mapViewAaEntry;
        private IConfigEntry _hotkeysEntry;
        private IConfigEntry _cycleKeyEntry;
        private IConfigEntry _issueReportEntry;
        private KeyCode _cycleKey = KeyCode.None;
        private bool _dlaaSelectable;
        private bool _fsr2Selectable;
        private string _fsrProviderName = string.Empty;
        private bool _upscalingSelectable;
        private bool _syncingConfiguration;
        private bool _pendingPersistentSettings;
        private int _originalMsaaSamples;
        private bool _ownsMsaa;
        private Phase1ProbeService _probeService;
        private TemporalCoordinator _temporalCoordinator;
        private VegetationMotionCompatibility _vegetationMotionCompatibility;
        private Harmony _harmony;

        public override void OnPreInitialized()
        {
            string dlaaReason;
            string fsr2Reason;
            _dlaaSelectable = ProbeDlaaAvailability(out dlaaReason);
            _fsr2Selectable = ProbeFsrAvailability(out _fsrProviderName, out fsr2Reason);
            _upscalingSelectable = ReduxSceneOutput.CompatibleAssembly;
            string[] modeChoices = UserSettingsPolicy.BuildModeChoices(
                _dlaaSelectable,
                _fsr2Selectable, _fsrProviderName, _dlaaSelectable && _upscalingSelectable, _fsr2Selectable && _upscalingSelectable
            );
            DebugMenu.ConfigureModes(modeChoices);

            _modeEntry = SWConfiguration.Bind(
                "Anti-Aliasing",
                "Mode",
                UserSettingsPolicy.ModeTaa,
                "Select the scene anti-aliasing method. FXAA Low and FXAA High " +
                "are KSP's stock spatial modes; SMAA is the highest-quality PPv2 " +
                "spatial option. TAA is the portable temporal option. NVIDIA DLAA " +
                "is offered only on supported NVIDIA hardware. AMD FSR chooses the best " +
                "available runtime and reports its actual provider. Upscaling renders " +
                "the scene at reduced resolution while keeping the UI native.",
                new ListConstraint<string>(modeChoices)
            );
            _sharpnessEntry = BindFloat(
                "Anti-Aliasing", "Sharpness", 0.15f, 0.0f, 1.0f, 100,
                "Shared post-reconstruction sharpness for supported temporal AA and " +
                "upscaling modes. " +
                "Zero disables sharpening."
            );
            _supersamplingEntry = SWConfiguration.Bind(
                "Anti-Aliasing", "Supersampling scale", 150,
                "Scene resolution per dimension when Supersampling is selected. UI stays native. " +
                "200% renders four times as many pixels; other AA modes use 100%.",
                new ListConstraint<int>(new[] { 125, 150, 175, 200 }));
            _upscalingQualityEntry = SWConfiguration.Bind(
                "Anti-Aliasing", "Upscaling quality", "Quality",
                "Used by DLSS and AMD FSR Upscaling. Quality keeps more scene detail; " +
                "Performance renders fewer pixels. Unsupported scenes use native AA.",
                new ListConstraint<string>(new[] { "Quality", "Balanced", "Performance" }));
            _taaStabilityEntry = BindFloat(
                "Anti-Aliasing", "TAA stability", 0.93f, 0.0f, 0.99f, 100,
                "Controls stationary TAA history retention. Higher values reduce " +
                "shimmer, while lower values respond faster and reduce ghosting."
            );
            _dlaaPresetEntry = SWConfiguration.Bind(
                "Anti-Aliasing",
                "DLAA preset",
                "K",
                "Select the NVIDIA DLAA model. F is a deprecated legacy model " +
                "retained for comparison. J can reduce ghosting but may flicker " +
                "more. K prioritizes image quality and fine detail. L is sharper " +
                "and more stable but costs more. M is a newer balanced quality and " +
                "stability alternative.",
                new ListConstraint<string>(DlaaPresetChoices)
            );
            _foliageMotionRepairEntry = SWConfiguration.Bind(
                "Anti-Aliasing",
                "Foliage motion repair",
                true,
                "Repairs invalid foliage motion that can destabilize temporal " +
                "anti-aliasing near the KSC. Enabling it may also improve " +
                "foliage rendering performance on compatible Redux builds."
            );
            _mapViewAaEntry = SWConfiguration.Bind(
                "Anti-Aliasing",
                "Enable AA in map view",
                true,
                "Apply the selected anti-aliasing mode in map view. Disable " +
                "this independently when map-view AA is unnecessary or looks " +
                "worse, without changing the normal flight AA selection."
            );

            _hotkeysEntry = SWConfiguration.Bind("Diagnostics", "Enable diagnostic hotkeys", true,
                "F10 opens the AA menu; Issue ZIP captures a report; Shift+F10 takes a screenshot.");
            _cycleKeyEntry = SWConfiguration.Bind("Diagnostics", "Cycle AA mode key", "None",
                "Optional mode-cycle key. Disabled by default to avoid Steam's F12 screenshot shortcut.",
                new ListConstraint<string>(new[] { "None", "F6", "F7", "F9", "F11", "F12" }));
            _issueReportEntry = SWConfiguration.Bind("Diagnostics", "Generate issue report ZIP", false,
                "Turn on to capture the current scene, available buffers and settings. Resets immediately. " +
                "Capture may pause the game briefly. The resulting ZIP stays local; review its images before sending it.");
            Persist(_issueReportEntry, false);
            _issueReportEntry.RegisterCallback((previous, current) =>
            {
                if (!(bool)current) return;
                Persist(_issueReportEntry, false);
                _probeService?.RequestIssueReport();
            });

            MigrateUserFacingSettings();

            RegisterSettingsCallbacks();

            SWLogger.LogInfo(
                "[ReduxBetterAA/Config] User-facing anti-aliasing settings " +
                "loaded; DLAA selectable=" + _dlaaSelectable + " (" +
                dlaaReason + "); AMD FSR selectable=" + _fsr2Selectable + " (" +
                fsr2Reason + ")."
            );
        }

        public override void OnInitialized()
        {
            // Redux Better AA owns scene anti-aliasing while loaded. The stock
            // graphics selector is disabled by a Harmony patch and MSAA is kept
            // off to avoid an undocumented second filter.
            _originalMsaaSamples = QualitySettings.antiAliasing;
            _ownsMsaa = true;
            QualitySettings.antiAliasing = 0;

            _probeService = new Phase1ProbeService(
                SWLogger,
                SWMetadata,
                false,
                (bool)_hotkeysEntry.Value,
                true
            );
            Phase1ProbeService.Current = _probeService;
            _probeService.Initialize();
            _probeService.InitializeIssueReports(this);

            _temporalCoordinator = new TemporalCoordinator(SWLogger);
            TemporalCoordinator.Current = _temporalCoordinator;
            _temporalCoordinator.Initialize();

            _vegetationMotionCompatibility =
                new VegetationMotionCompatibility(
                    SWLogger,
                    OnMotionInputChanged
                );
            VegetationMotionCompatibility.Current =
                _vegetationMotionCompatibility;
            _vegetationMotionCompatibility.Initialize();

            ApplyPersistentSettings();

            _probeService.SetTemporalControls(new BackendSettingsPanel
            {
                TemporalStatus = () => _temporalCoordinator.Status,
                RequestedBackend = () => _temporalCoordinator.RequestedBackend,
                SetRequestedBackend = SetRequestedBackendAndPersist,
                CustomConfig = () => _temporalCoordinator.CustomConfig,
                SetCustomConfig = SetCustomConfigAndPersist,
                RestoreCustomPreset = RestoreConservativeCustomPresetAndPersist,
                CustomMemoryBytes = () => _temporalCoordinator.CustomEstimatedMemoryBytes,
                DlaaConfig = () => _temporalCoordinator.DlaaConfig,
                DlaaPresetIsMenuOnly = () => _temporalCoordinator.DlaaPresetIsMenuOnly,
                SetDlaaConfig = SetDlaaConfigAndPersist,
                RestoreDlaaPreset = RestoreConservativeDlaaPresetAndPersist,
                DlaaDetails = () => _temporalCoordinator.DlaaDetails,
                DlaaMemoryBytes = () => _temporalCoordinator.DlaaEstimatedMemoryBytes,
                Fsr2Config = () => _temporalCoordinator.Fsr2Config,
                SetFsr2Config = SetFsr2ConfigAndPersist,
                RestoreFsr2Preset = RestoreConservativeFsr2PresetAndPersist,
                Fsr2Details = () => _temporalCoordinator.Fsr2Details,
                Fsr2MemoryBytes = () => _temporalCoordinator.Fsr2EstimatedMemoryBytes,
                PerformanceProfile = _temporalCoordinator.GetPerformanceProfile,
                StartPerformanceProfile = _temporalCoordinator.StartPerformanceProfile,
                CancelPerformanceProfile = _temporalCoordinator.CancelPerformanceProfile,
                ResetTemporalHistory = _temporalCoordinator.RequestHistoryReset,
                MapViewAaEnabled = () => _temporalCoordinator.MapViewAaEnabled,
                SetMapViewAaEnabled = SetMapViewAaEnabled,
                Sharpness = () => (float)_sharpnessEntry.Value,
                SetSharpness = value => _sharpnessEntry.Value = value,
                SetStability = value => _taaStabilityEntry.Value = value,
                SetDlaaPreset = value => SetDlaaConfigAndPersist(
                    _temporalCoordinator.DlaaConfig.WithPreset(ParseDlaaPreset(value))),
                SupersamplingPercent = () => (int)_supersamplingEntry.Value,
                SetSupersamplingPercent = value => _supersamplingEntry.Value = value,
                UpscalingQuality = () => _temporalCoordinator.UpscalingQuality,
                SetUpscalingQuality = value => _upscalingQualityEntry.Value = value.ToString(),
                DlssDetails = () => _temporalCoordinator.DlaaDetails,
                DlssMemoryBytes = () => _temporalCoordinator.DlaaEstimatedMemoryBytes,
                FsrUpscalingDetails = () => _temporalCoordinator.Fsr2Details,
                FsrUpscalingMemoryBytes = () => _temporalCoordinator.Fsr2EstimatedMemoryBytes,
            });
            _probeService.SetMotionSanitizerDiagnostics(
                () => _temporalCoordinator.MotionVectorSanitizedTexture,
                () => _temporalCoordinator.MotionVectorCorruptionTexture,
                () => _temporalCoordinator.CurrentJitterNormalized
            );
            _probeService.SetMotionInputControls(
                () => _vegetationMotionCompatibility.Enabled,
                SetVegetationMotionRepairEnabled,
                () => _vegetationMotionCompatibility.Status,
                () => _vegetationMotionCompatibility.ReroutedCalls,
                () => _temporalCoordinator.MotionVectorSanitizerEnabled,
                SetMotionSanitizerEnabled,
                () => _temporalCoordinator.MotionVectorSanitizerStatus
            );
            _harmony = CreateHarmonyAndPatchAll();

            SWLogger.LogInfo(
                "[ReduxBetterAA/Backend] Spatial AA, custom TAA, NVIDIA and AMD reconstruction backends installed; requested mode is " +
                _temporalCoordinator.RequestedBackend + "."
            );
        }

        public override void OnPostInitialized()
        {
            _probeService.MarkDirty(ProbeDirtyReason.ModsInitialized);
            SWLogger.LogInfo(
                "[ReduxBetterAA/Probe] Controls: F10 AA menu; Issue ZIP button; Shift+F10 screenshot; optional cycle key in settings; Ctrl+Alt+F8 report."
            );
        }

        private void Update()
        {
            ApplyPendingPersistentSettings();
            if (_cycleKey != KeyCode.None && Input.GetKeyDown(_cycleKey) && !DiagnosticHotkeys.AnyModifierDown())
            {
                CycleRequestedBackendAndPersist();
            }
            _temporalCoordinator?.Tick();
            _probeService?.Tick();
        }

        private void OnGUI()
        {
            _probeService?.DrawGui();
        }

        private void OnDestroy()
        {
            // Comparison claims the same cameras after the normal coordinator.
            // Unwind diagnostics first, then normal rendering, then global state.
            if (ReferenceEquals(Phase1ProbeService.Current, _probeService)) Phase1ProbeService.Current = null;
            _probeService?.Dispose();
            _probeService = null;

            if (ReferenceEquals(
                    VegetationMotionCompatibility.Current,
                    _vegetationMotionCompatibility))
            {
                VegetationMotionCompatibility.Current = null;
            }
            _vegetationMotionCompatibility?.Dispose();
            _vegetationMotionCompatibility = null;

            if (ReferenceEquals(TemporalCoordinator.Current, _temporalCoordinator))
            {
                TemporalCoordinator.Current = null;
            }
            _temporalCoordinator?.Dispose();
            _temporalCoordinator = null;

            Patches.StockAntialiasingControlPatch.Restore();

            if (_harmony != null)
            {
                _harmony.UnpatchAll(_harmony.Id);
            }
            _harmony = null;
            if (_ownsMsaa && QualitySettings.antiAliasing == 0)
            {
                QualitySettings.antiAliasing = _originalMsaaSamples;
            }
            _ownsMsaa = false;
        }

        private void OnMotionInputChanged()
        {
            _temporalCoordinator?.NotifyMotionInputChanged();
            _probeService?.MarkDirty(ProbeDirtyReason.MotionInputChanged);
        }

        private void SetVegetationMotionRepairEnabled(bool enabled)
        {
            if (_temporalCoordinator != null) _temporalCoordinator.VegetationRepairRequested = enabled;
            bool changed = _vegetationMotionCompatibility != null &&
                _vegetationMotionCompatibility.SetEnabled(enabled && _temporalCoordinator != null &&
                    _temporalCoordinator.Active && _temporalCoordinator.SelectedBackend != "Supersampling");
            Persist(_foliageMotionRepairEntry, enabled);
            if (changed)
            {
                OnMotionInputChanged();
            }
        }

        private void SetMapViewAaEnabled(bool enabled)
        {
            _temporalCoordinator?.SetMapViewAaEnabled(enabled);
            Persist(_mapViewAaEntry, enabled);
        }

        private void SetMotionSanitizerEnabled(bool enabled)
        {
            _temporalCoordinator?.SetMotionVectorSanitizerEnabled(enabled);
        }

        private IConfigEntry BindFloat(
            string section,
            string key,
            float defaultValue,
            float minimum,
            float maximum,
            int steps,
            string description)
        {
            return SWConfiguration.Bind(
                section,
                key,
                defaultValue,
                description,
                new RangeConstraint<float>(minimum, maximum, steps, "{0:F2}")
            );
        }

        private void RegisterSettingsCallbacks()
        {
            _supersamplingEntry.RegisterCallback(OnPersistentSettingChanged);
            _upscalingQualityEntry.RegisterCallback(OnPersistentSettingChanged);
            _modeEntry.RegisterCallback(OnPersistentSettingChanged);
            _sharpnessEntry.RegisterCallback(OnPersistentSettingChanged);
            _taaStabilityEntry.RegisterCallback(OnPersistentSettingChanged);
            _dlaaPresetEntry.RegisterCallback(OnPersistentSettingChanged);
            _foliageMotionRepairEntry.RegisterCallback(OnPersistentSettingChanged);
            _mapViewAaEntry.RegisterCallback(OnPersistentSettingChanged);
            _hotkeysEntry.RegisterCallback(OnPersistentSettingChanged);
            _cycleKeyEntry.RegisterCallback(OnPersistentSettingChanged);
        }

        private void OnPersistentSettingChanged(object previous, object current)
        {
            if (!_syncingConfiguration)
            {
                // Redux's UI mirrors callback arguments back into the entry.
                // Correcting a value here would let a later UI callback replay
                // the outer setter's stale value and recurse indefinitely.
                _pendingPersistentSettings = true;
            }
        }

        internal void ApplyPendingPersistentSettings()
        {
            if (!_pendingPersistentSettings) return;
            _pendingPersistentSettings = false;
            // ReduxLib reset can restore a provider label from another GPU.
            // Normalize after the original setter and UI callbacks have returned.
            MigrateUserFacingSettings();
            ApplyPersistentSettings();
        }

        private void ApplyPersistentSettings()
        {
            if (_probeService != null)
                _probeService.HotkeysEnabled = (bool)_hotkeysEntry.Value;
            if (!Enum.TryParse((string)_cycleKeyEntry.Value, out _cycleKey))
                _cycleKey = KeyCode.None;
            if (_temporalCoordinator == null)
            {
                return;
            }

            float sharpness = (float)_sharpnessEntry.Value;
            _temporalCoordinator.SetSupersamplingPercent((int)_supersamplingEntry.Value);
            _temporalCoordinator.SetReconstructionQuality(ReconstructionPolicy.Parse((string)_upscalingQualityEntry.Value));
            CustomTaaConfig custom = _temporalCoordinator.CustomConfig;
            _temporalCoordinator.SetCustomConfig(custom.WithUserSettings(
                (float)_taaStabilityEntry.Value,
                sharpness
            ));

            DlaaConfig dlaa = _temporalCoordinator.DlaaConfig;
            _temporalCoordinator.SetPersistentDlaaConfig(dlaa.WithUserSettings(
                sharpness,
                dlaa.PreExposure,
                dlaa.AutoExposure,
                ParseDlaaPreset((string)_dlaaPresetEntry.Value),
                dlaa.AllowSupersampling
            ));

            Fsr2Config fsr2 = _temporalCoordinator.Fsr2Config;
            _temporalCoordinator.SetFsr2Config(fsr2.WithUserSettings(
                sharpness,
                fsr2.PreExposure,
                fsr2.AutoExposure
            ));
            SetVegetationMotionRepairEnabled(
                (bool)_foliageMotionRepairEntry.Value
            );
            _temporalCoordinator.SetMapViewAaEnabled(
                (bool)_mapViewAaEntry.Value
            );
            _temporalCoordinator.SetRequestedBackend(
                ParseBackend((string)_modeEntry.Value)
            );
        }

        private void SetRequestedBackendAndPersist(BackendSelection backend)
        {
            _temporalCoordinator?.SetRequestedBackend(backend);
            string label;
            if (UserSettingsPolicy.TryGetMode(backend, _dlaaSelectable, _fsr2Selectable, out label, _fsrProviderName, _dlaaSelectable && _upscalingSelectable, _fsr2Selectable && _upscalingSelectable))
            {
                Persist(_modeEntry, label);
            }
        }

        private void CycleRequestedBackendAndPersist()
        {
            if (_temporalCoordinator == null)
            {
                return;
            }
            BackendSelection next = UserSettingsPolicy.NextBackend(
                _temporalCoordinator.RequestedBackend,
                _dlaaSelectable,
                _fsr2Selectable, _dlaaSelectable && _upscalingSelectable, _fsr2Selectable && _upscalingSelectable
            );
            SetRequestedBackendAndPersist(next);
        }

        private void SetCustomConfigAndPersist(CustomTaaConfig config)
        {
            _temporalCoordinator?.SetCustomConfig(config);
            Persist(_taaStabilityEntry, config.StationaryHistory);
            SetSharedSharpnessAndPersist(config.Sharpening);
        }

        private void SetDlaaConfigAndPersist(DlaaConfig config)
        {
            if (config.Preset == DlaaPreset.Default)
            {
                config = config.WithUserSettings(
                    config.Sharpness,
                    config.PreExposure,
                    config.AutoExposure,
                    DlaaPreset.K,
                    config.AllowSupersampling
                );
            }
            _temporalCoordinator?.SetDlaaConfig(config);
            if (_temporalCoordinator != null)
            {
                Fsr2Config fsr2 = _temporalCoordinator.Fsr2Config;
                _temporalCoordinator.SetFsr2Config(
                    fsr2.WithUserSettings(
                        fsr2.Sharpness,
                        config.PreExposure,
                        config.AutoExposure
                    ).WithExposurePreference(config.PreferPpv2Exposure)
                );
            }
            if (_temporalCoordinator == null || !_temporalCoordinator.DlaaPresetIsMenuOnly)
                Persist(_dlaaPresetEntry, config.Preset.ToString());
            SetSharedSharpnessAndPersist(config.Sharpness);
        }

        private void SetFsr2ConfigAndPersist(Fsr2Config config)
        {
            _temporalCoordinator?.SetFsr2Config(config);
            if (_temporalCoordinator != null)
            {
                DlaaConfig dlaa = _temporalCoordinator.DlaaConfig;
                _temporalCoordinator.SetDlaaConfig(
                    dlaa.WithUserSettings(
                        dlaa.Sharpness,
                        config.PreExposure,
                        config.AutoExposure,
                        dlaa.Preset,
                        dlaa.AllowSupersampling
                    ).WithExposurePreference(config.PreferPpv2Exposure)
                );
            }
            SetSharedSharpnessAndPersist(
                config.Sharpness
            );
        }

        private void RestoreConservativeCustomPresetAndPersist()
        {
            SetCustomConfigAndPersist(CustomTaaConfig.Conservative);
        }

        private void RestoreConservativeDlaaPresetAndPersist()
        {
            SetDlaaConfigAndPersist(DlaaConfig.Conservative);
        }

        private void RestoreConservativeFsr2PresetAndPersist()
        {
            SetFsr2ConfigAndPersist(Fsr2Config.Conservative);
        }

        private void SetSharedSharpnessAndPersist(float value)
        {
            float sharpness = Mathf.Clamp01(value);
            Persist(_sharpnessEntry, sharpness);
            if (_temporalCoordinator == null)
            {
                return;
            }

            CustomTaaConfig custom = _temporalCoordinator.CustomConfig;
            _temporalCoordinator.SetCustomConfig(custom.WithUserSettings(
                custom.StationaryHistory,
                sharpness
            ));

            DlaaConfig dlaa = _temporalCoordinator.DlaaConfig;
            _temporalCoordinator.SetDlaaConfig(dlaa.WithUserSettings(
                sharpness,
                dlaa.PreExposure,
                dlaa.AutoExposure,
                dlaa.Preset,
                dlaa.AllowSupersampling
            ));

            Fsr2Config fsr2 = _temporalCoordinator.Fsr2Config;
            _temporalCoordinator.SetFsr2Config(fsr2.WithUserSettings(
                sharpness,
                fsr2.PreExposure,
                fsr2.AutoExposure
            ));
        }

        private void MigrateUserFacingSettings()
        {
            string mode = _modeEntry.Value as string;
            Persist(
                _modeEntry,
                UserSettingsPolicy.NormalizeMode(
                    mode,
                    _dlaaSelectable,
                    _fsr2Selectable, _fsrProviderName, _dlaaSelectable && _upscalingSelectable, _fsr2Selectable && _upscalingSelectable
                )
            );

            string preset = _dlaaPresetEntry.Value as string;
            Persist(
                _dlaaPresetEntry,
                UserSettingsPolicy.NormalizeDlaaPreset(preset)
            );
        }

        private static bool ProbeDlaaAvailability(out string reason)
        {
            bool nvidia = SystemInfo.graphicsDeviceVendorID == 0x10DE ||
                SystemInfo.graphicsDeviceVendor.IndexOf(
                    "NVIDIA",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0;
            if (!nvidia)
            {
                reason = "active GPU is not NVIDIA";
                return false;
            }

            var api = new NvidiaDlaaApi();
            return api.TryInitialize(out reason);
        }

        private static bool ProbeFsrAvailability(out string provider, out string reason)
        {
            return AmdFsrNativeApi.Probe(out provider, out reason);
        }

        private void Persist<T>(IConfigEntry entry, T value)
        {
            if (entry == null || Equals(entry.Value, value))
            {
                return;
            }
            _syncingConfiguration = true;
            try
            {
                entry.Value = value;
            }
            finally
            {
                _syncingConfiguration = false;
            }
        }

        private BackendSelection ParseBackend(string value)
        {
            return UserSettingsPolicy.ParseBackend(
                value,
                _dlaaSelectable,
                _fsr2Selectable, _fsrProviderName, _dlaaSelectable && _upscalingSelectable, _fsr2Selectable && _upscalingSelectable
            );
        }

        private static DlaaPreset ParseDlaaPreset(string value)
        {
            switch (value)
            {
                case "F":
                    return DlaaPreset.F;
                case "J":
                    return DlaaPreset.J;
                case "K":
                    return DlaaPreset.K;
                case "L":
                    return DlaaPreset.L;
                case "M":
                    return DlaaPreset.M;
                default:
                    return DlaaPreset.K;
            }
        }

    }
}
