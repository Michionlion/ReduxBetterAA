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
        internal Func<CustomTaaConfig> CustomConfig;
        internal Action<CustomTaaConfig> SetCustomConfig;
        internal Action RestoreCustomPreset;
        internal Func<long> CustomMemoryBytes;
        internal Func<DlaaConfig> DlaaConfig;
        internal Func<bool> DlaaPresetIsMenuOnly;
        internal Action<DlaaConfig> SetDlaaConfig;
        internal Action RestoreDlaaPreset;
        internal Func<string> DlaaDetails;
        internal Func<long> DlaaMemoryBytes;
        internal Func<string> DlssDetails;
        internal Func<long> DlssMemoryBytes;
        internal Func<Fsr2Config> Fsr2Config;
        internal Action<Fsr2Config> SetFsr2Config;
        internal Action RestoreFsr2Preset;
        internal Func<string> Fsr2Details;
        internal Func<long> Fsr2MemoryBytes;
        internal Func<string> FsrUpscalingDetails;
        internal Func<long> FsrUpscalingMemoryBytes;
        internal Func<BackendSelection, PerformanceProfileSnapshot>
            PerformanceProfile;
        internal Action<BackendSelection> StartPerformanceProfile;
        internal Action CancelPerformanceProfile;
        internal Action ResetTemporalHistory;
        internal Func<bool> MapViewAaEnabled;
        internal Action<bool> SetMapViewAaEnabled;
        internal Func<float> Sharpness;
        internal Action<float> SetSharpness;
        internal Action<float> SetStability;
        internal Action<string> SetDlaaPreset;
        internal Func<int> SupersamplingPercent;
        internal Action<int> SetSupersamplingPercent;
        internal Func<ReconstructionQuality> UpscalingQuality;
        internal Action<ReconstructionQuality> SetUpscalingQuality;
        private bool _scaleOpen;
        private static readonly string[] ScaleLabels = { "125%", "150%", "175%", "200%" };
        private bool _qualityOpen;
        private static readonly string[] QualityLabels = { "Quality", "Balanced", "Performance" };
        private bool _presetOpen;

        internal void DrawBasic(BackendSelection mode)
        {
            if ((mode == BackendSelection.NvidiaDlss || mode == BackendSelection.AmdFsrUpscaling) &&
                UpscalingQuality != null && SetUpscalingQuality != null)
            {
                int index = Mathf.Clamp((int)UpscalingQuality() - 1, 0, QualityLabels.Length - 1);
                int next = DebugMenu.Dropdown("Upscaling quality", index, QualityLabels, ref _qualityOpen);
                if (next != index) SetUpscalingQuality((ReconstructionQuality)(next + 1));
            }
            if ((mode >= BackendSelection.CustomTaa && mode <= BackendSelection.AmdFsr2 ||
                mode == BackendSelection.NvidiaDlss || mode == BackendSelection.AmdFsrUpscaling) && Sharpness != null) {
                float value = Sharpness();
                float next = DrawParameter("Sharpness", value, 0, 1);
                if (next != value) SetSharpness(next);
            }
            if (mode == BackendSelection.CustomTaa && CustomConfig != null) {
                float value = CustomConfig().StationaryHistory;
                float next = DrawParameter("Stability", value, 0, .99f);
                if (next != value) SetStability(next);
            }
            if (mode == BackendSelection.NvidiaDlaa && DlaaConfig != null) {
                int index = Array.IndexOf(DlaaPresetLabels, DlaaConfig().Preset.ToString());
                int next = DebugMenu.Dropdown("Model", Mathf.Max(0, index), DlaaPresetLabels, ref _presetOpen);
                if (next != index) SetDlaaPreset(DlaaPresetLabels[next]);
                if (DlaaPresetIsMenuOnly != null && DlaaPresetIsMenuOnly())
                    GUILayout.Label("Main-menu model only; gameplay selection is preserved.");
            }
            bool map = MapViewAaEnabled == null || MapViewAaEnabled();
            if (mode == BackendSelection.Supersampling && SupersamplingPercent != null)
            {
                int index = Mathf.Clamp((SupersamplingPercent() - 125) / 25, 0, 3);
                int next = DebugMenu.Dropdown("Scene scale", index, ScaleLabels, ref _scaleOpen);
                if (next != index) SetSupersamplingPercent?.Invoke(125 + next * 25);
                GUILayout.Label("UI stays native. 200% renders four times the pixels.");
            }
            bool nextMap = GUILayout.Toggle(map, "Enable AA in map view");
            if (nextMap != map) SetMapViewAaEnabled?.Invoke(nextMap);
            if (mode == BackendSelection.Off) GUILayout.Label("Scene AA is disabled.");
            if (mode >= BackendSelection.FxaaLow && mode <= BackendSelection.Smaa) DrawSpatialAaTab(mode);
        }
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

        internal void DrawCustomTab()
        {
            GUILayout.Label("Custom TAA");
            if (CustomConfig == null || SetCustomConfig == null)
            {
                GUILayout.Label("Custom TAA parameter controls are unavailable.");
                return;
            }

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

            long bytes = CustomMemoryBytes == null ? 0 : CustomMemoryBytes();
            GUILayout.Label(
                bytes > 0
                    ? "Allocated custom history: " +
                      (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
                    : "Custom history is allocated when the backend first renders."
            );
            GUILayout.Label(
                "Motion above the configured limit is rejected."
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
            if (DlaaPresetIsMenuOnly != null && DlaaPresetIsMenuOnly())
                GUILayout.Label("Main-menu model only; gameplay selection is preserved.");
            GUILayout.Label("NVIDIA DLAA");
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
                false,
                preferPpv2Exposure
            );
            if (!config.ValuesEqual(in updated))
            {
                SetDlaaConfig(updated);
            }

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
                "<=256 px camera fallback. Select the separate Supersampling mode " +
                "to render above native resolution."
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
            GUILayout.Label(DebugMenu.ModeName(BackendSelection.AmdFsr2));
            GUILayout.Label(
                Fsr2Details == null
                    ? "FSR runtime details are unavailable."
                    : Fsr2Details()
            );
            if (Fsr2Config == null || SetFsr2Config == null)
            {
                GUILayout.Label("FSR parameter controls are unavailable.");
                return;
            }

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
                config.PreExposure,
                config.AutoExposure,
                invertMotionX,
                invertMotionY,
                config.PreferPpv2Exposure
            );
            if (!config.ValuesEqual(in updated))
            {
                SetFsr2Config(updated);
            }

            long bytes = Fsr2MemoryBytes == null ? 0 : Fsr2MemoryBytes();
            GUILayout.Label(
                bytes > 0
                    ? "Project-owned FSR output: " +
                      (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
                    : "The FSR output is allocated when its context first renders."
            );
            GUILayout.Label(
                "Native-resolution AA keeps the scene render scale at 100%. " +
                "The active FSR provider is shown above. Screen-wide corruption is " +
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

        internal void DrawUpscalingTab(BackendSelection mode)
        {
            bool dlss = mode == BackendSelection.NvidiaDlss;
            GUILayout.Label(DebugMenu.ModeName(mode));
            Func<string> details = dlss ? DlssDetails : FsrUpscalingDetails;
            GUILayout.Label(details == null ? "Upscaling runtime details are unavailable." : details());
            Func<long> memoryBytes = dlss ? DlssMemoryBytes : FsrUpscalingMemoryBytes;
            long bytes = memoryBytes == null ? 0 : memoryBytes();
            GUILayout.Label(bytes > 0
                ? "Project-owned reconstruction buffers: " + (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
                : "Reconstruction buffers are allocated when this mode first renders.");
            GUILayout.Label("Reconstructs the scene to display resolution while the UI stays native. " +
                "Quality renders more scene detail; Performance renders fewer pixels.");
            bool previousEnabled = GUI.enabled;
            GUI.enabled = RequestedBackend != null && RequestedBackend() == mode && ResetTemporalHistory != null;
            if (GUILayout.Button("Reset history", GUILayout.Height(28f))) ResetTemporalHistory();
            GUI.enabled = previousEnabled;
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
                "CPU frame interval (includes waits): " +
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
                    : "Whole frame GPU unavailable: " + profile.GpuUnavailableReason
            );
            if (profile.GpuSamples > 0)
                GUILayout.Label("GPU timing source: " + profile.GpuSource +
                    "; repeated Unity records skipped: " + profile.DuplicateTimingRecords + ".");
            if (profile.CpuFallbackSamples > 0)
                GUILayout.Label("CPU frame interval used Unity delta time for " +
                    profile.CpuFallbackSamples + " frames without fresh CPU timing records.");

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
            if (profile.GpuSource != baseline.GpuSource) return ", GPU timing sources differ";
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
