using UnityEngine;

namespace ReduxBetterAA.Configuration
{
    internal enum BackendSelection
    {
        Off = 0,
        // These three spatial modes mirror the post-processing AA choices
        // exposed by KSP's graphics settings.  Keeping them in the shared
        // selection enum lets the normal settings page, Ctrl+F10 panel, hotkey,
        // and performance profiler all use the same source of truth.
        FxaaLow = 1,
        FxaaHigh = 2,
        Smaa = 3,
        Ppv2Taa = 4,
        CustomTaa = 5,
        NvidiaDlaa = 6,
        AmdFsr2 = 7
    }

    internal readonly struct TemporalBackendConfig
    {
        public readonly float JitterSpread;
        public readonly float Sharpness;
        public readonly float StationaryBlending;
        public readonly float MotionBlending;

        public TemporalBackendConfig(
            float jitterSpread,
            float sharpness,
            float stationaryBlending,
            float motionBlending)
        {
            JitterSpread = Mathf.Clamp(jitterSpread, 0.1f, 1.0f);
            // Sharpness is one shared user setting across every reconstructing
            // backend. Keep PPv2 on that same 0-1 contract so the engineering
            // panel cannot advertise values that persistence silently truncates.
            Sharpness = Mathf.Clamp01(sharpness);
            StationaryBlending = Mathf.Clamp(stationaryBlending, 0.0f, 0.99f);
            MotionBlending = Mathf.Clamp(motionBlending, 0.0f, 0.99f);
        }

        public static TemporalBackendConfig ConservativePpv2 =>
            new TemporalBackendConfig(
                0.75f,
                0.15f,
                0.92f,
                0.05f
            );

        public bool ValuesEqual(in TemporalBackendConfig other)
        {
            return JitterSpread == other.JitterSpread &&
                   Sharpness == other.Sharpness &&
                   StationaryBlending == other.StationaryBlending &&
                   MotionBlending == other.MotionBlending;
        }
    }

    internal enum CustomTaaDebugView
    {
        FinalResolve = 0,
        CurrentColor = 1,
        HistoryColor = 2,
        ReprojectedHistory = 3,
        DepthRejection = 4,
        ReactiveMask = 5,
        HistoryWeight = 6,
        ClampExtent = 7,
        MotionVectors = 8,
        DepthEdges = 9
    }

    internal readonly struct CustomTaaConfig
    {
        public readonly float JitterSpread;
        public readonly int SequenceLength;
        public readonly float StationaryHistory;
        public readonly float MovingHistory;
        public readonly float MotionResponsePixels;
        public readonly float MaximumMotionPixels;
        public readonly float DepthThreshold;
        public readonly float DepthEdgeStability;
        public readonly float VarianceGamma;
        public readonly float ReactiveScale;
        public readonly float Sharpening;
        public readonly float NoDepthHistory;
        public readonly CustomTaaDebugView DebugView;

        public CustomTaaConfig(
            float jitterSpread,
            int sequenceLength,
            float stationaryHistory,
            float movingHistory,
            float motionResponsePixels,
            float maximumMotionPixels,
            float depthThreshold,
            float depthEdgeStability,
            float varianceGamma,
            float reactiveScale,
            float sharpening,
            float noDepthHistory,
            CustomTaaDebugView debugView)
        {
            JitterSpread = Mathf.Clamp(jitterSpread, 0.1f, 1.5f);
            SequenceLength = Mathf.Clamp(sequenceLength, 4, 32);
            StationaryHistory = Mathf.Clamp(stationaryHistory, 0.0f, 0.99f);
            MovingHistory = Mathf.Clamp(movingHistory, 0.0f, 0.99f);
            MotionResponsePixels = Mathf.Clamp(motionResponsePixels, 0.5f, 64.0f);
            MaximumMotionPixels = Mathf.Clamp(maximumMotionPixels, 8.0f, 512.0f);
            DepthThreshold = Mathf.Clamp(depthThreshold, 0.0001f, 0.1f);
            DepthEdgeStability = Mathf.Clamp01(depthEdgeStability);
            VarianceGamma = Mathf.Clamp(varianceGamma, 0.5f, 3.0f);
            ReactiveScale = Mathf.Clamp(reactiveScale, 0.0f, 10.0f);
            Sharpening = Mathf.Clamp(sharpening, 0.0f, 1.0f);
            NoDepthHistory = Mathf.Clamp(noDepthHistory, 0.0f, 0.99f);
            DebugView = debugView < CustomTaaDebugView.FinalResolve ||
                        debugView > CustomTaaDebugView.DepthEdges
                ? CustomTaaDebugView.FinalResolve
                : debugView;
        }

        public static CustomTaaConfig Conservative => new CustomTaaConfig(
            0.75f,
            8,
            0.93f,
            0.10f,
            8.0f,
            256.0f,
            0.01f,
            0.75f,
            1.25f,
            2.0f,
            0.15f,
            0.25f,
            CustomTaaDebugView.FinalResolve
        );

        public bool ValuesEqual(in CustomTaaConfig other)
        {
            return JitterSpread == other.JitterSpread &&
                   SequenceLength == other.SequenceLength &&
                   StationaryHistory == other.StationaryHistory &&
                   MovingHistory == other.MovingHistory &&
                   MotionResponsePixels == other.MotionResponsePixels &&
                   MaximumMotionPixels == other.MaximumMotionPixels &&
                   DepthThreshold == other.DepthThreshold &&
                   DepthEdgeStability == other.DepthEdgeStability &&
                   VarianceGamma == other.VarianceGamma &&
                   ReactiveScale == other.ReactiveScale &&
                   Sharpening == other.Sharpening &&
                   NoDepthHistory == other.NoDepthHistory &&
                   DebugView == other.DebugView;
        }

        public CustomTaaConfig WithUserSettings(
            float stationaryHistory,
            float sharpening)
        {
            return new CustomTaaConfig(
                JitterSpread,
                SequenceLength,
                stationaryHistory,
                MovingHistory,
                MotionResponsePixels,
                MaximumMotionPixels,
                DepthThreshold,
                DepthEdgeStability,
                VarianceGamma,
                ReactiveScale,
                sharpening,
                NoDepthHistory,
                DebugView
            );
        }

        public bool RequiresHistoryReset(in CustomTaaConfig other)
        {
            // Sharpening is applied after the unsharpened resolve has already
            // been written to history, and debug views only select presentation.
            return JitterSpread != other.JitterSpread ||
                   SequenceLength != other.SequenceLength ||
                   StationaryHistory != other.StationaryHistory ||
                   MovingHistory != other.MovingHistory ||
                   MotionResponsePixels != other.MotionResponsePixels ||
                   MaximumMotionPixels != other.MaximumMotionPixels ||
                   DepthThreshold != other.DepthThreshold ||
                   DepthEdgeStability != other.DepthEdgeStability ||
                   VarianceGamma != other.VarianceGamma ||
                   ReactiveScale != other.ReactiveScale ||
                   NoDepthHistory != other.NoDepthHistory;
        }
    }

    internal enum DlaaPreset
    {
        Default = 0,
        F = 1,
        J = 2,
        K = 4,
        L = 8,
        M = 16
    }

    internal readonly struct DlaaConfig
    {
        public readonly float JitterSpread;
        public readonly int SequenceLength;
        public readonly float Sharpness;
        public readonly float PreExposure;
        public readonly bool AutoExposure;
        public readonly bool PreferPpv2Exposure;
        public readonly bool InvertMotionX;
        public readonly bool InvertMotionY;
        public readonly DlaaPreset Preset;
        public readonly bool AllowSupersampling;

        public DlaaConfig(
            float jitterSpread,
            int sequenceLength,
            float sharpness,
            float preExposure,
            bool autoExposure,
            bool invertMotionX,
            bool invertMotionY,
            DlaaPreset preset,
            bool allowSupersampling = false,
            bool preferPpv2Exposure = true)
        {
            JitterSpread = Mathf.Clamp(jitterSpread, 0.1f, 1.5f);
            SequenceLength = Mathf.Clamp(sequenceLength, 4, 32);
            Sharpness = Mathf.Clamp(sharpness, 0.0f, 1.0f);
            PreExposure = Mathf.Clamp(preExposure, 0.01f, 16.0f);
            AutoExposure = autoExposure;
            PreferPpv2Exposure = preferPpv2Exposure;
            InvertMotionX = invertMotionX;
            InvertMotionY = invertMotionY;
            Preset = IsValidPreset(preset) ? preset : DlaaPreset.M;
            AllowSupersampling = allowSupersampling;
        }

        public static DlaaConfig Conservative => new DlaaConfig(
            0.75f,
            8,
            0.15f,
            1.0f,
            true,
            true,
            true,
            DlaaPreset.M,
            false,
            true
        );

        public bool ValuesEqual(in DlaaConfig other)
        {
            return JitterSpread == other.JitterSpread &&
                   SequenceLength == other.SequenceLength &&
                   Sharpness == other.Sharpness &&
                   PreExposure == other.PreExposure &&
                   AutoExposure == other.AutoExposure &&
                   PreferPpv2Exposure == other.PreferPpv2Exposure &&
                   InvertMotionX == other.InvertMotionX &&
                   InvertMotionY == other.InvertMotionY &&
                   Preset == other.Preset &&
                   AllowSupersampling == other.AllowSupersampling;
        }

        public bool RequiresContextRecreation(in DlaaConfig other)
        {
            return AutoExposure != other.AutoExposure ||
                   PreferPpv2Exposure != other.PreferPpv2Exposure ||
                   Preset != other.Preset ||
                   AllowSupersampling != other.AllowSupersampling;
        }

        public bool RequiresHistoryReset(in DlaaConfig other)
        {
            // DLAA sharpening is an output treatment. Preset/exposure-mode and
            // supersampling changes are handled by context recreation instead.
            return JitterSpread != other.JitterSpread ||
                   SequenceLength != other.SequenceLength ||
                   PreExposure != other.PreExposure ||
                   InvertMotionX != other.InvertMotionX ||
                   InvertMotionY != other.InvertMotionY;
        }

        public DlaaConfig WithUserSettings(
            float sharpness,
            float preExposure,
            bool autoExposure,
            DlaaPreset preset,
            bool allowSupersampling)
        {
            return new DlaaConfig(
                JitterSpread,
                SequenceLength,
                sharpness,
                preExposure,
                autoExposure,
                InvertMotionX,
                InvertMotionY,
                preset,
                allowSupersampling,
                PreferPpv2Exposure
            );
        }

        public DlaaConfig WithExposurePreference(bool preferPpv2Exposure)
        {
            return new DlaaConfig(
                JitterSpread,
                SequenceLength,
                Sharpness,
                PreExposure,
                AutoExposure,
                InvertMotionX,
                InvertMotionY,
                Preset,
                AllowSupersampling,
                preferPpv2Exposure
            );
        }

        private static bool IsValidPreset(DlaaPreset preset)
        {
            return preset == DlaaPreset.F ||
                   preset == DlaaPreset.J ||
                   preset == DlaaPreset.K ||
                   preset == DlaaPreset.L ||
                   preset == DlaaPreset.M;
        }
    }

    internal readonly struct Fsr2Config
    {
        public readonly float JitterSpread;
        public readonly int SequenceLength;
        public readonly bool EnableSharpening;
        public readonly float Sharpness;
        public readonly float PreExposure;
        public readonly bool AutoExposure;
        public readonly bool PreferPpv2Exposure;
        public readonly bool InvertMotionX;
        public readonly bool InvertMotionY;

        public Fsr2Config(
            float jitterSpread,
            int sequenceLength,
            float sharpness,
            float preExposure,
            bool autoExposure,
            bool invertMotionX,
            bool invertMotionY,
            bool preferPpv2Exposure = true)
        {
            JitterSpread = Mathf.Clamp(jitterSpread, 0.1f, 1.5f);
            SequenceLength = Mathf.Clamp(sequenceLength, 4, 32);
            Sharpness = Mathf.Clamp01(sharpness);
            EnableSharpening = Sharpness > 0.0f;
            PreExposure = Mathf.Clamp(preExposure, 0.01f, 16.0f);
            AutoExposure = autoExposure;
            PreferPpv2Exposure = preferPpv2Exposure;
            InvertMotionX = invertMotionX;
            InvertMotionY = invertMotionY;
        }

        public static Fsr2Config Conservative => new Fsr2Config(
            0.75f,
            8,
            0.15f,
            1.0f,
            true,
            true,
            true,
            true
        );

        public bool ValuesEqual(in Fsr2Config other)
        {
            return JitterSpread == other.JitterSpread &&
                   SequenceLength == other.SequenceLength &&
                   EnableSharpening == other.EnableSharpening &&
                   Sharpness == other.Sharpness &&
                   PreExposure == other.PreExposure &&
                   AutoExposure == other.AutoExposure &&
                   PreferPpv2Exposure == other.PreferPpv2Exposure &&
                   InvertMotionX == other.InvertMotionX &&
                   InvertMotionY == other.InvertMotionY;
        }

        public bool RequiresContextRecreation(in Fsr2Config other)
        {
            return AutoExposure != other.AutoExposure ||
                   PreferPpv2Exposure != other.PreferPpv2Exposure;
        }

        public bool RequiresHistoryReset(in Fsr2Config other)
        {
            // RCAS is an output-only pass. Exposure-mode changes recreate the
            // context; these remaining temporal-input changes invalidate it.
            return JitterSpread != other.JitterSpread ||
                   SequenceLength != other.SequenceLength ||
                   PreExposure != other.PreExposure ||
                   InvertMotionX != other.InvertMotionX ||
                   InvertMotionY != other.InvertMotionY;
        }

        public Fsr2Config WithUserSettings(
            float sharpness,
            float preExposure,
            bool autoExposure)
        {
            return new Fsr2Config(
                JitterSpread,
                SequenceLength,
                sharpness,
                preExposure,
                autoExposure,
                InvertMotionX,
                InvertMotionY,
                PreferPpv2Exposure
            );
        }

        public Fsr2Config WithExposurePreference(bool preferPpv2Exposure)
        {
            return new Fsr2Config(
                JitterSpread,
                SequenceLength,
                Sharpness,
                PreExposure,
                AutoExposure,
                InvertMotionX,
                InvertMotionY,
                preferPpv2Exposure
            );
        }
    }

}
