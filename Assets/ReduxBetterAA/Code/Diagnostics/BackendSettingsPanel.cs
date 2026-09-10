using System;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    // AA controls only: no camera hooks, texture ownership, capture or input suppression.
    internal sealed class BackendSettingsPanel
    {
        internal Action ClosePanel;
        internal Func<string> TemporalStatus;
        internal Func<BackendSelection> RequestedBackend;
        internal Action<BackendSelection> SetRequestedBackend;
        internal Func<TemporalBackendConfig> Ppv2Config;
        internal Action<TemporalBackendConfig> SetPpv2Config;
        internal Action RestorePpv2Preset;
        internal Func<CustomTaaConfig> CustomConfig;
        internal Action<CustomTaaConfig> SetCustomConfig;
        internal Action RestoreCustomPreset;
        internal Func<long> CustomMemoryBytes;
        internal Func<DlaaConfig> DlaaConfig;
        internal Action<DlaaConfig> SetDlaaConfig;
        internal Action RestoreDlaaPreset;
        internal Func<string> DlaaDetails;
        internal Func<long> DlaaMemoryBytes;
        internal Func<Fsr2Config> Fsr2Config;
        internal Action<Fsr2Config> SetFsr2Config;
        internal Action RestoreFsr2Preset;
        internal Func<string> Fsr2Details;
        internal Func<long> Fsr2MemoryBytes;
        internal Func<BackendSelection, PerformanceProfileSnapshot>
            PerformanceProfile;
        internal Action<BackendSelection> StartPerformanceProfile;
        internal Action CancelPerformanceProfile;
        internal Action ResetTemporalHistory;
        internal Func<bool> MapViewAaEnabled;
        internal Action<bool> SetMapViewAaEnabled;
        private Vector2 _customScroll;
        private Vector2 _dlaaScroll;
        private Vector2 _fsr2Scroll;
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
        internal void DrawMapViewAaControl()
        {
            bool enabled = MapViewAaEnabled == null || MapViewAaEnabled();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = SetMapViewAaEnabled != null;
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
                SetMapViewAaEnabled(!enabled);
            }
            GUI.backgroundColor = previousColor;
            GUI.enabled = previousEnabled;
            GUILayout.Label(
                "This independently forces AA Off only while map view is active."
            );
        }

        internal static void DrawOffTab()
        {
            GUILayout.Label(
                "Redux forces PPv2 anti-aliasing off while this mode is selected, " +
                "providing a true unfiltered baseline. The renderer's prior AA " +
                "state is restored when Redux releases the scene. Choose FXAA " +
                "Low, FXAA High, SMAA, PPv2, Custom, DLAA, or FSR2 AA above to " +
                "enable that mode and open its settings."
            );
        }

        internal static void DrawSpatialAaTab(BackendSelection mode)
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

        internal void DrawPpv2Tab()
        {
            GUILayout.Label("Phase 2 / PPv2 TAA parameters");

            if (Ppv2Config == null || SetPpv2Config == null)
            {
                GUILayout.Label("PPv2 parameter controls are unavailable.");
                return;
            }

            TemporalBackendConfig config = Ppv2Config();
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
                SetPpv2Config(updated);
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
                RestorePpv2Preset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && ResetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                ResetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        internal void DrawCustomTab()
        {
            GUILayout.Label("Phase 3 / project-owned custom TAA");
            if (CustomConfig == null || SetCustomConfig == null)
            {
                GUILayout.Label("Custom TAA parameter controls are unavailable.");
                return;
            }

            _customScroll = GUILayout.BeginScrollView(
                _customScroll,
                GUILayout.Height(370f)
            );
            CustomTaaConfig config = CustomConfig();
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
                SetCustomConfig(updated);
            }
            GUILayout.EndScrollView();

            long bytes = CustomMemoryBytes == null ? 0 : CustomMemoryBytes();
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
                RestoreCustomPreset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && ResetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                ResetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        private bool IsTemporalBackendActive()
        {
            return RequestedBackend != null &&
                   RequestedBackend() != BackendSelection.Off;
        }

        internal void DrawDlaaTab()
        {
            GUILayout.Label("Phase 4 / managed Unity NVIDIA DLAA");
            GUILayout.Label(
                DlaaDetails == null
                    ? "DLAA runtime details are unavailable."
                    : DlaaDetails()
            );
            if (DlaaConfig == null || SetDlaaConfig == null)
            {
                GUILayout.Label("DLAA parameter controls are unavailable.");
                return;
            }

            _dlaaScroll = GUILayout.BeginScrollView(
                _dlaaScroll,
                GUILayout.Height(330f)
            );
            DlaaConfig config = DlaaConfig();
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
                SetDlaaConfig(updated);
            }
            GUILayout.EndScrollView();

            long bytes = DlaaMemoryBytes == null ? 0 : DlaaMemoryBytes();
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
                RestoreDlaaPreset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && ResetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                ResetTemporalHistory();
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

        internal void DrawFsr2Tab()
        {
            GUILayout.Label("Phase 5 experiment / Unity AMD FSR2 Native AA");
            GUILayout.Label(
                Fsr2Details == null
                    ? "FSR2 runtime details are unavailable."
                    : Fsr2Details()
            );
            if (Fsr2Config == null || SetFsr2Config == null)
            {
                GUILayout.Label("FSR2 parameter controls are unavailable.");
                return;
            }

            _fsr2Scroll = GUILayout.BeginScrollView(
                _fsr2Scroll,
                GUILayout.Height(330f)
            );
            Fsr2Config config = Fsr2Config();
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
                SetFsr2Config(updated);
            }
            GUILayout.EndScrollView();

            long bytes = Fsr2MemoryBytes == null ? 0 : Fsr2MemoryBytes();
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
                RestoreFsr2Preset?.Invoke();
            }
            bool previousHistoryEnabled = GUI.enabled;
            GUI.enabled = IsTemporalBackendActive() && ResetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f)))
            {
                ResetTemporalHistory();
            }
            GUI.enabled = previousHistoryEnabled;
            GUILayout.EndHorizontal();
        }

        internal void DrawPerformanceProfile(BackendSelection mode)
        {
            GUILayout.Space(10f);
            GUILayout.Label("Performance profile (30 warm-up + 240 measured frames)");
            if (PerformanceProfile == null || StartPerformanceProfile == null)
            {
                GUILayout.Label("Performance profiling is unavailable.");
                return;
            }

            PerformanceProfileSnapshot profile = PerformanceProfile(mode);
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
                    CancelPerformanceProfile?.Invoke();
                }
            }
            else if (GUILayout.Button(
                         "Profile 240 frames (panel closes)",
                         GUILayout.Height(28f)))
            {
                StartPerformanceProfile(mode);
                ClosePanel?.Invoke();
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

            PerformanceProfileSnapshot baseline = PerformanceProfile(
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

        internal static float DrawParameter(
            string label,
            float value,
            float minimum,
            float maximum)
        {
            GUILayout.Label(label + ": " + value.ToString("0.000"));
            return GUILayout.HorizontalSlider(value, minimum, maximum);
        }

    }
}
