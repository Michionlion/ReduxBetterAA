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
    /// Phase 2 PPv2, Phase 3 custom TAA, Phase 4 managed DLAA, and experimental
    /// native-resolution FSR2 prototypes.
    ///
    /// The AA backend remains off by default. While Off is selected, Redux
    /// explicitly owns a zero-AA baseline; all captured renderer state is
    /// restored when ownership moves or the mod unloads.
    /// </summary>
    public sealed class ReduxBetterAAMod : MonoBehaviourMod
    {
        private const string ModeOff = UserSettingsPolicy.ModeOff;
        private const string ModeFxaaLow = UserSettingsPolicy.ModeFxaaLow;
        private const string ModeSmaa = UserSettingsPolicy.ModeSmaa;
        private const string ModeFxaaHigh = UserSettingsPolicy.ModeFxaaHigh;
        private const string ModeCustom = UserSettingsPolicy.ModeTaa;
        private const string ModeDlaa = UserSettingsPolicy.ModeDlaa;
        private const string ModeFsr2 = UserSettingsPolicy.ModeFsr2;

        private static readonly string[] DlaaPresetChoices =
            { "F", "J", "K", "L", "M" };

        private IConfigEntry _modeEntry;
        private IConfigEntry _sharpnessEntry;
        private IConfigEntry _taaStabilityEntry;
        private IConfigEntry _dlaaPresetEntry;
        private bool _dlaaSelectable;
        private bool _fsr2Selectable;
        private bool _syncingConfiguration;
        private int _originalMsaaSamples;
        private bool _ownsMsaa;
        private const bool HotkeysEnabled = true;
        private Phase1ProbeService _probeService;
        private TemporalCoordinator _temporalCoordinator;
        private KspPhysicsRenderInterpolation _physicsRenderInterpolation;
        private Harmony _harmony;

        public override void OnPreInitialized()
        {
            string dlaaReason;
            string fsr2Reason;
            _dlaaSelectable = ProbeDlaaAvailability(out dlaaReason);
            _fsr2Selectable = ProbeFsr2Availability(out fsr2Reason);
            string[] modeChoices = UserSettingsPolicy.BuildModeChoices(
                _dlaaSelectable,
                _fsr2Selectable
            );

            _modeEntry = SWConfiguration.Bind(
                "Anti-Aliasing",
                "Mode",
                ModeOff,
                "Select the scene anti-aliasing method. FXAA Low and FXAA High " +
                "are KSP's stock spatial modes; SMAA is the highest-quality PPv2 " +
                "spatial option. TAA is the portable temporal option. NVIDIA DLAA " +
                "is offered only on supported NVIDIA hardware. FSR 2 Native AA " +
                "uses equal input and output resolution and is offered when its " +
                "Unity runtime is available.",
                new ListConstraint<string>(modeChoices)
            );
            _sharpnessEntry = BindFloat(
                "Anti-Aliasing", "Sharpness", 0.15f, 0.0f, 1.0f, 100,
                "Shared post-reconstruction sharpness for TAA, DLAA, and FSR 2 " +
                "Native AA. " +
                "Zero disables sharpening."
            );
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
                "more. K is NVIDIA's recommended DLAA default and prioritizes " +
                "image quality. L is sharper and more stable but costs more. " +
                "M targets similar quality improvements with performance closer " +
                "to J and K.",
                new ListConstraint<string>(DlaaPresetChoices)
            );

            MigrateUserFacingSettings();

            RegisterSettingsCallbacks();

            SWLogger.LogInfo(
                "[ReduxBetterAA/Config] Four user-facing anti-aliasing settings " +
                "loaded; DLAA selectable=" + _dlaaSelectable + " (" +
                dlaaReason + "); FSR2 selectable=" + _fsr2Selectable + " (" +
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
                HotkeysEnabled,
                true
            );
            Phase1ProbeService.Current = _probeService;
            _probeService.Initialize();

            _temporalCoordinator = new TemporalCoordinator(
                SWLogger,
                false
            );
            TemporalCoordinator.Current = _temporalCoordinator;
            _temporalCoordinator.Initialize();

            ApplyPersistentSettings();

            _physicsRenderInterpolation = new KspPhysicsRenderInterpolation(
                SWLogger,
                OnMotionInputChanged
            );
            KspPhysicsRenderInterpolation.Current = _physicsRenderInterpolation;
            _physicsRenderInterpolation.Initialize();
            _probeService.SetTemporalControls(
                () => _temporalCoordinator.Status,
                () => _temporalCoordinator.RequestedBackend,
                SetRequestedBackendAndPersist,
                () => _temporalCoordinator.Ppv2Config,
                SetPpv2ConfigAndPersist,
                RestoreConservativePpv2PresetAndPersist,
                () => _temporalCoordinator.CustomConfig,
                SetCustomConfigAndPersist,
                RestoreConservativeCustomPresetAndPersist,
                () => _temporalCoordinator.CustomEstimatedMemoryBytes,
                () => _temporalCoordinator.DlaaConfig,
                SetDlaaConfigAndPersist,
                RestoreConservativeDlaaPresetAndPersist,
                () => _temporalCoordinator.DlaaDetails,
                () => _temporalCoordinator.DlaaEstimatedMemoryBytes,
                () => _temporalCoordinator.Fsr2Config,
                SetFsr2ConfigAndPersist,
                RestoreConservativeFsr2PresetAndPersist,
                () => _temporalCoordinator.Fsr2Details,
                () => _temporalCoordinator.Fsr2EstimatedMemoryBytes,
                _temporalCoordinator.GetPerformanceProfile,
                _temporalCoordinator.StartPerformanceProfile,
                _temporalCoordinator.CancelPerformanceProfile,
                _temporalCoordinator.RequestHistoryReset
            );
            _probeService.SetMotionCadenceControls(
                () => _physicsRenderInterpolation.Enabled,
                SetPhysicsRenderInterpolation,
                () => _physicsRenderInterpolation.Status,
                RefreshPhysicsRenderInterpolation
            );
            _probeService.SetMotionSanitizerDiagnostics(
                () => _temporalCoordinator.MotionVectorSanitizedTexture,
                () => _temporalCoordinator.MotionVectorCorruptionTexture,
                () => _temporalCoordinator.CurrentJitterNormalized
            );
            _harmony = CreateHarmonyAndPatchAll();

            SWLogger.LogInfo(
                "[ReduxBetterAA/Backend] Spatial AA, PPv2, custom TAA, DLAA, and FSR2 Native AA backends installed; requested mode is " +
                _temporalCoordinator.RequestedBackend + "."
            );
        }

        public override void OnPostInitialized()
        {
            _probeService.MarkDirty(ProbeDirtyReason.ModsInitialized);
            SWLogger.LogInfo(
                "[ReduxBetterAA/Probe] Controls: Ctrl+F10 panel; F10 report+screenshot; F12 cycles AA mode; Ctrl+Alt+F8 report."
            );
        }

        private void Update()
        {
            if (HotkeysEnabled && Input.GetKeyDown(KeyCode.F12))
            {
                CycleRequestedBackendAndPersist();
            }
            _physicsRenderInterpolation?.Tick();
            _temporalCoordinator?.Tick();
            _probeService?.Tick();
        }

        private void OnGUI()
        {
            _probeService?.DrawGui();
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(
                    KspPhysicsRenderInterpolation.Current,
                    _physicsRenderInterpolation))
            {
                KspPhysicsRenderInterpolation.Current = null;
            }
            _physicsRenderInterpolation?.Dispose();
            _physicsRenderInterpolation = null;

            if (ReferenceEquals(TemporalCoordinator.Current, _temporalCoordinator))
            {
                TemporalCoordinator.Current = null;
            }
            _temporalCoordinator?.Dispose();
            _temporalCoordinator = null;

            if (ReferenceEquals(Phase1ProbeService.Current, _probeService))
            {
                Phase1ProbeService.Current = null;
            }

            _probeService?.Dispose();
            _probeService = null;

            if (_harmony != null)
            {
                _harmony.UnpatchAll(_harmony.Id);
            }
            _harmony = null;
            if (_ownsMsaa)
            {
                QualitySettings.antiAliasing = _originalMsaaSamples;
                _ownsMsaa = false;
            }
        }

        private void OnMotionInputChanged()
        {
            _temporalCoordinator?.NotifyMotionInputChanged();
            _probeService?.MarkDirty(ProbeDirtyReason.MotionInputChanged);
        }

        private void RefreshPhysicsRenderInterpolation()
        {
            _physicsRenderInterpolation?.RefreshNow();
        }

        private void SetPhysicsRenderInterpolation(bool enabled)
        {
            _physicsRenderInterpolation?.SetEnabled(enabled);
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
                new RangeConstraint<float>(minimum, maximum, steps, string.Empty)
            );
        }

        private void RegisterSettingsCallbacks()
        {
            _modeEntry.RegisterCallback(OnPersistentSettingChanged);
            _sharpnessEntry.RegisterCallback(OnPersistentSettingChanged);
            _taaStabilityEntry.RegisterCallback(OnPersistentSettingChanged);
            _dlaaPresetEntry.RegisterCallback(OnPersistentSettingChanged);
        }

        private void OnPersistentSettingChanged(object previous, object current)
        {
            if (!_syncingConfiguration)
            {
                ApplyPersistentSettings();
            }
        }

        private void ApplyPersistentSettings()
        {
            if (_temporalCoordinator == null)
            {
                return;
            }

            float sharpness = (float)_sharpnessEntry.Value;
            TemporalBackendConfig ppv2 = _temporalCoordinator.Ppv2Config;
            _temporalCoordinator.SetPpv2Config(new TemporalBackendConfig(
                ppv2.JitterSpread,
                sharpness,
                ppv2.StationaryBlending,
                ppv2.MotionBlending
            ));

            CustomTaaConfig custom = _temporalCoordinator.CustomConfig;
            _temporalCoordinator.SetCustomConfig(custom.WithUserSettings(
                (float)_taaStabilityEntry.Value,
                sharpness
            ));

            DlaaConfig dlaa = _temporalCoordinator.DlaaConfig;
            _temporalCoordinator.SetDlaaConfig(dlaa.WithUserSettings(
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
            _temporalCoordinator.SetRequestedBackend(
                ParseBackend((string)_modeEntry.Value)
            );
        }

        private void SetRequestedBackendAndPersist(BackendSelection backend)
        {
            _temporalCoordinator?.SetRequestedBackend(backend);
            string label;
            if (TryGetUserBackendLabel(backend, out label))
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
                _fsr2Selectable
            );
            SetRequestedBackendAndPersist(next);
        }

        private void SetPpv2ConfigAndPersist(TemporalBackendConfig config)
        {
            float sharpness = Mathf.Clamp01(config.Sharpness);
            if (_temporalCoordinator != null)
            {
                _temporalCoordinator.SetPpv2Config(new TemporalBackendConfig(
                    config.JitterSpread,
                    sharpness,
                    config.StationaryBlending,
                    config.MotionBlending
                ));
            }
            SetSharedSharpnessAndPersist(sharpness);
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

        private void RestoreConservativePpv2PresetAndPersist()
        {
            SetPpv2ConfigAndPersist(TemporalBackendConfig.ConservativePpv2);
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

            TemporalBackendConfig ppv2 = _temporalCoordinator.Ppv2Config;
            _temporalCoordinator.SetPpv2Config(new TemporalBackendConfig(
                ppv2.JitterSpread,
                sharpness,
                ppv2.StationaryBlending,
                ppv2.MotionBlending
            ));

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
                    _fsr2Selectable
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

        private static bool ProbeFsr2Availability(out string reason)
        {
            var api = new AmdFsr2Api();
            return api.TryInitialize(out reason);
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
                _fsr2Selectable
            );
        }

        private bool TryGetUserBackendLabel(
            BackendSelection backend,
            out string label)
        {
            switch (backend)
            {
                case BackendSelection.FxaaLow:
                    label = ModeFxaaLow;
                    return true;
                case BackendSelection.Smaa:
                    label = ModeSmaa;
                    return true;
                case BackendSelection.FxaaHigh:
                    label = ModeFxaaHigh;
                    return true;
                case BackendSelection.CustomTaa:
                    label = ModeCustom;
                    return true;
                case BackendSelection.NvidiaDlaa:
                    label = ModeDlaa;
                    return _dlaaSelectable;
                case BackendSelection.AmdFsr2:
                    label = ModeFsr2;
                    return _fsr2Selectable;
                case BackendSelection.Off:
                    label = ModeOff;
                    return true;
                default:
                    label = string.Empty;
                    return false;
            }
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
