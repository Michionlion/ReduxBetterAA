using System;
using System.Collections.Generic;

namespace ReduxBetterAA.Configuration
{
    /// <summary>
    /// Pure mode policy. Runtime probes supply capabilities and the active FSR
    /// provider; persistence records the mode without locking it to an old SDK.
    /// </summary>
    internal static class UserSettingsPolicy
    {
        public const string ModeOff = "Off";
        public const string ModeSupersampling = "Supersampling";
        public const string ModeFxaaLow = "FXAA Low";
        public const string ModeFxaaHigh = "FXAA High";
        public const string ModeSmaa = "SMAA";
        public const string ModeTaa = "TAA";
        public const string ModeDlaa = "NVIDIA DLAA";
        // The historic internal identifier remains compatible with backend ID 7.
        public const string ModeFsr2 = "FSR 3.1 Native AA";
        public const string LegacyModeFsr2Native = "FSR 2 Native AA";
        public const string LegacyModePpv2 = "PPv2 TAA";
        public const string LegacyModeCustom = "Custom TAA";
        public const string LegacyModeFsr2 = "FSR 2";

        private static readonly BackendSelection[] CycleOrder =
        {
            BackendSelection.Off, BackendSelection.Supersampling,
            BackendSelection.FxaaLow, BackendSelection.FxaaHigh, BackendSelection.Smaa,
            BackendSelection.CustomTaa, BackendSelection.NvidiaDlaa, BackendSelection.AmdFsr2
        };

        public static string[] BuildModeChoices(bool dlaaSelectable, bool fsr2Selectable,
            string providerName = "FSR 3.1")
        {
            fsr2Selectable &= NormalizeFsrProvider(providerName) != null;
            var choices = new List<string>(10)
            {
                ModeOff, ModeFxaaLow, ModeFxaaHigh, ModeSmaa, ModeTaa, ModeSupersampling
            };
            if (dlaaSelectable) choices.Add(ModeDlaa);
            if (fsr2Selectable) choices.Add(FsrModeName(providerName));
            return choices.ToArray();
        }

        public static BackendSelection NormalizeBackend(BackendSelection backend) =>
            (int)backend == 4 ? BackendSelection.CustomTaa : backend;

        public static BackendSelection NextBackend(BackendSelection current,
            bool dlaaSelectable, bool fsr2Selectable)
        {
            int currentIndex = Array.IndexOf(CycleOrder, NormalizeBackend(current));
            if (currentIndex < 0) return BackendSelection.Off;
            for (int offset = 1; offset <= CycleOrder.Length; offset++)
            {
                BackendSelection next = CycleOrder[(currentIndex + offset) % CycleOrder.Length];
                if (IsSelectable(next, dlaaSelectable, fsr2Selectable)) return next;
            }
            return BackendSelection.Off;
        }

        public static string NormalizeMode(string value, bool dlaaSelectable, bool fsr2Selectable,
            string providerName = "FSR 3.1")
        {
            BackendSelection backend = ParseBackend(value, dlaaSelectable, fsr2Selectable,
                providerName);
            TryGetMode(backend, dlaaSelectable, fsr2Selectable, out string mode,
                providerName);
            return mode;
        }

        public static BackendSelection ParseBackend(string value,
            bool dlaaSelectable, bool fsr2Selectable, string providerName = "FSR 3.1")
        {
            fsr2Selectable &= NormalizeFsrProvider(providerName) != null;
            BackendSelection backend;
            switch (value)
            {
                case ModeSupersampling: backend = BackendSelection.Supersampling; break;
                case ModeFxaaLow: backend = BackendSelection.FxaaLow; break;
                case ModeFxaaHigh: backend = BackendSelection.FxaaHigh; break;
                case ModeSmaa: backend = BackendSelection.Smaa; break;
                case ModeTaa:
                case LegacyModePpv2:
                case LegacyModeCustom: backend = BackendSelection.CustomTaa; break;
                case ModeDlaa: backend = BackendSelection.NvidiaDlaa; break;
                default:
                    if (TryReadFsrMode(value)) backend = BackendSelection.AmdFsr2;
                    else backend = BackendSelection.Off;
                    break;
            }
            return IsSelectable(backend, dlaaSelectable, fsr2Selectable) ? backend : BackendSelection.Off;
        }

        public static string NormalizeDlaaPreset(string value)
        {
            return value == "F" || value == "J" || value == "K" || value == "L" || value == "M"
                ? value : "K";
        }

        public static bool TryGetMode(BackendSelection backend,
            bool dlaaSelectable, bool fsr2Selectable, out string mode,
            string providerName = "FSR 3.1")
        {
            fsr2Selectable &= NormalizeFsrProvider(providerName) != null;
            backend = NormalizeBackend(backend);
            switch (backend)
            {
                case BackendSelection.Off: mode = ModeOff; break;
                case BackendSelection.Supersampling: mode = ModeSupersampling; break;
                case BackendSelection.FxaaLow: mode = ModeFxaaLow; break;
                case BackendSelection.FxaaHigh: mode = ModeFxaaHigh; break;
                case BackendSelection.Smaa: mode = ModeSmaa; break;
                case BackendSelection.CustomTaa: mode = ModeTaa; break;
                case BackendSelection.NvidiaDlaa: mode = ModeDlaa; break;
                case BackendSelection.AmdFsr2: mode = FsrModeName(providerName); break;
                default: mode = string.Empty; return false;
            }
            return IsSelectable(backend, dlaaSelectable, fsr2Selectable);
        }

        public static bool TryGetModeForBackend(BackendSelection backend,
            bool dlaaSelectable, bool fsr2Selectable, out string mode,
            string providerName = "FSR 3.1") =>
            TryGetMode(backend, dlaaSelectable, fsr2Selectable, out mode,
                providerName);

        private static bool IsSelectable(BackendSelection backend,
            bool dlaaSelectable, bool fsr2Selectable)
        {
            switch (backend)
            {
                case BackendSelection.NvidiaDlaa: return dlaaSelectable;
                case BackendSelection.AmdFsr2: return fsr2Selectable;
                default: return backend >= BackendSelection.Off && backend <= BackendSelection.Supersampling;
            }
        }

        private static string FsrModeName(string providerName)
        {
            string provider = NormalizeFsrProvider(providerName);
            return provider == null ? string.Empty : provider + " Native AA";
        }

        private static string NormalizeFsrProvider(string providerName)
        {
            if (string.IsNullOrWhiteSpace(providerName)) return null;
            string provider = providerName.Trim();
            if (provider.StartsWith("AMD ", StringComparison.Ordinal)) provider = provider.Substring(4);
            switch (provider)
            {
                case "FSR3.1": case "FSR 3.1": return "FSR 3.1";
                case "FSR4": case "FSR 4": return "FSR 4";
                case "FSR4.1": case "FSR 4.1": return "FSR 4.1";
                default: return null;
            }
        }

        private static bool TryReadFsrMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string provider = value.Trim();
            if (provider.StartsWith("AMD ", StringComparison.Ordinal)) provider = provider.Substring(4);
            if (provider.EndsWith(" Native AA", StringComparison.Ordinal))
                provider = provider.Substring(0, provider.Length - " Native AA".Length);
            switch (provider)
            {
                case "FSR":
                case "FSR 2": case "FSR2":
                case "FSR 3.1": case "FSR3.1":
                case "FSR 4": case "FSR4":
                case "FSR 4.1": case "FSR4.1": return true;
                default: return false;
            }
        }
    }
}
