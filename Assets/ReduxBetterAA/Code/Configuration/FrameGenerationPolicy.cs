using System;
using System.Collections.Generic;
using ReduxBetterAA.Rendering;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Configuration
{
    // One persisted value controls provider and total displayed-frame multiplier.
    // These identifiers are independent of anti-aliasing/reconstruction IDs.
    internal enum FrameGenerationMode { Off, Auto, Dlss2x, Dlss3x, Dlss4x, Fsr4_2x, Fsr31_2x }
    internal enum FrameGenerationProviderKind { None, NvidiaDlss, AmdFsr4, AmdFsr31 }

    internal readonly struct FrameGenerationSupport
    {
        internal readonly FrameGenerationProviderKind Kind;
        internal readonly FrameGenerationCapabilities Capabilities;
        internal readonly bool PresentationReady;
        internal readonly string UnavailableReason;

        // SDK context creation is deliberately separate from a usable presenter.
        internal FrameGenerationSupport(FrameGenerationProviderKind kind,
            in FrameGenerationCapabilities capabilities, bool presentationReady, string unavailableReason = "")
        {
            Kind = kind; Capabilities = capabilities; PresentationReady = presentationReady;
            UnavailableReason = unavailableReason ?? string.Empty;
        }

        internal bool Supports(GraphicsDeviceType api, int multiplier) =>
            Kind != FrameGenerationProviderKind.None && PresentationReady && Capabilities.Available &&
            !string.IsNullOrWhiteSpace(Capabilities.Provider) &&
            !string.IsNullOrWhiteSpace(Capabilities.RuntimeVersion) &&
            Capabilities.ColorDomain >= FrameGenerationColorDomain.SceneLinearHdr &&
            Capabilities.ColorDomain <= FrameGenerationColorDomain.DisplaySrgb &&
            Capabilities.MotionUnits >= FrameGenerationMotionUnits.NormalizedUv &&
            Capabilities.MotionUnits <= FrameGenerationMotionUnits.DisplayPixels &&
            (api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
             api == GraphicsDeviceType.Vulkan) && Capabilities.GraphicsApi == api &&
            Capabilities.SupportsMultiplier(multiplier);
    }

    internal readonly struct FrameGenerationSelection
    {
        internal readonly FrameGenerationMode Requested, Selected;
        internal readonly string Reason;
        internal FrameGenerationSelection(FrameGenerationMode requested, FrameGenerationMode selected, string reason)
        { Requested = requested; Selected = selected; Reason = reason; }
        internal bool FellBack => Requested != FrameGenerationMode.Auto && Requested != Selected;
    }

    internal static class FrameGenerationPolicy
    {
        internal const string SettingName = "Frame generation";
        private static readonly FrameGenerationMode[] ExplicitModes = {
            FrameGenerationMode.Dlss2x, FrameGenerationMode.Dlss3x, FrameGenerationMode.Dlss4x,
            FrameGenerationMode.Fsr4_2x, FrameGenerationMode.Fsr31_2x
        };

        internal static string Label(FrameGenerationMode mode)
        {
            switch (mode)
            {
                case FrameGenerationMode.Auto: return "Auto";
                case FrameGenerationMode.Dlss2x: return "DLSS 2x";
                case FrameGenerationMode.Dlss3x: return "DLSS 3x";
                case FrameGenerationMode.Dlss4x: return "DLSS 4x";
                case FrameGenerationMode.Fsr4_2x: return "FSR 4 2x";
                case FrameGenerationMode.Fsr31_2x: return "FSR 3.1 2x";
                default: return "Off";
            }
        }

        internal static FrameGenerationMode Parse(string value)
        {
            if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase)) return FrameGenerationMode.Auto;
            for (int i = 0; i < ExplicitModes.Length; i++)
                if (string.Equals(value, Label(ExplicitModes[i]), StringComparison.OrdinalIgnoreCase))
                    return ExplicitModes[i];
            return FrameGenerationMode.Off;
        }

        internal static int Multiplier(FrameGenerationMode mode)
        {
            switch (mode)
            {
                case FrameGenerationMode.Dlss2x:
                case FrameGenerationMode.Fsr4_2x:
                case FrameGenerationMode.Fsr31_2x: return 2;
                case FrameGenerationMode.Dlss3x: return 3;
                case FrameGenerationMode.Dlss4x: return 4;
                default: return 1;
            }
        }

        internal static FrameGenerationProviderKind Provider(FrameGenerationMode mode)
        {
            switch (mode)
            {
                case FrameGenerationMode.Dlss2x:
                case FrameGenerationMode.Dlss3x:
                case FrameGenerationMode.Dlss4x: return FrameGenerationProviderKind.NvidiaDlss;
                case FrameGenerationMode.Fsr4_2x: return FrameGenerationProviderKind.AmdFsr4;
                case FrameGenerationMode.Fsr31_2x: return FrameGenerationProviderKind.AmdFsr31;
                default: return FrameGenerationProviderKind.None;
            }
        }

        internal static string[] BuildChoices(FrameGenerationSupport[] support, GraphicsDeviceType api)
        {
            var values = new List<string>(7) { Label(FrameGenerationMode.Off) };
            for (int i = 0; i < ExplicitModes.Length; i++)
            {
                if (!Supported(ExplicitModes[i], support, api)) continue;
                if (values.Count == 1) values.Add(Label(FrameGenerationMode.Auto));
                values.Add(Label(ExplicitModes[i]));
            }
            return values.ToArray();
        }

        internal static bool Supported(FrameGenerationMode mode, FrameGenerationSupport[] support, GraphicsDeviceType api)
        {
            if (mode == FrameGenerationMode.Off) return true;
            var kind = Provider(mode);
            if (kind == FrameGenerationProviderKind.None || support == null) return false;
            for (int i = 0; i < support.Length; i++)
                if (support[i].Kind == kind && support[i].Supports(api, Multiplier(mode))) return true;
            return false;
        }

        internal static FrameGenerationSelection Resolve(FrameGenerationMode requested,
            FrameGenerationSupport[] support, GraphicsDeviceType api)
        {
            if (requested < FrameGenerationMode.Off || requested > FrameGenerationMode.Fsr31_2x)
                requested = FrameGenerationMode.Off;
            if (requested == FrameGenerationMode.Off)
                return new FrameGenerationSelection(requested, requested, string.Empty);
            FrameGenerationMode selected = FrameGenerationMode.Off;
            if (requested == FrameGenerationMode.Auto || Provider(requested) == FrameGenerationProviderKind.NvidiaDlss)
            {
                int maximum = requested == FrameGenerationMode.Auto ? 4 : Multiplier(requested);
                for (int multiplier = maximum; multiplier >= 2; multiplier--)
                {
                    var candidate = (FrameGenerationMode)((int)FrameGenerationMode.Dlss2x + multiplier - 2);
                    if (!Supported(candidate, support, api)) continue;
                    selected = candidate;
                    break;
                }
            }
            // NVIDIA FG unavailable (including pre-RTX-40 hardware): use the
            // actual supported AMD FG provider, never an SR capability guess.
            if (selected == FrameGenerationMode.Off && requested != FrameGenerationMode.Fsr31_2x &&
                Supported(FrameGenerationMode.Fsr4_2x, support, api)) selected = FrameGenerationMode.Fsr4_2x;
            if (selected == FrameGenerationMode.Off && Supported(FrameGenerationMode.Fsr31_2x, support, api))
                selected = FrameGenerationMode.Fsr31_2x;
            string reason = selected == FrameGenerationMode.Off
                ? "No compatible frame-generation provider with a ready presenter; AA/upscaling remains selected."
                : selected == requested || requested == FrameGenerationMode.Auto ? string.Empty
                : Label(requested) + " unavailable; using " + Label(selected) + ".";
            return new FrameGenerationSelection(requested, selected, reason);
        }
    }
}
