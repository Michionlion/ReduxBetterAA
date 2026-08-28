namespace ReduxBetterAA.Configuration
{
    /// <summary>
    /// Pure user-facing mode policy kept separate from runtime capability
    /// probing so migration and selection order remain testable.
    /// </summary>
    internal static class UserSettingsPolicy
    {
        public const string ModeOff = "Off";
        public const string ModeFxaaLow = "FXAA Low";
        public const string ModeFxaaHigh = "FXAA High";
        public const string ModeSmaa = "SMAA";
        public const string ModeTaa = "TAA";
        public const string ModeDlaa = "NVIDIA DLAA";
        public const string ModeFsr2 = "FSR 2 Native AA";
        public const string LegacyModePpv2 = "PPv2 TAA";
        public const string LegacyModeCustom = "Custom TAA";
        public const string LegacyModeFsr2 = "FSR 2";

        public static string[] BuildModeChoices(
            bool dlaaSelectable,
            bool fsr2Selectable)
        {
            string[] choices = new string[5 +
                (dlaaSelectable ? 1 : 0) +
                (fsr2Selectable ? 1 : 0)];
            int index = 0;
            choices[index++] = ModeOff;
            choices[index++] = ModeFxaaLow;
            choices[index++] = ModeFxaaHigh;
            choices[index++] = ModeSmaa;
            choices[index++] = ModeTaa;
            if (dlaaSelectable && fsr2Selectable)
            {
                choices[index++] = ModeDlaa;
                choices[index] = ModeFsr2;
                return choices;
            }
            if (dlaaSelectable)
            {
                choices[index] = ModeDlaa;
                return choices;
            }
            if (fsr2Selectable)
            {
                choices[index] = ModeFsr2;
                return choices;
            }
            return choices;
        }

        public static BackendSelection NextBackend(
            BackendSelection current,
            bool dlaaSelectable,
            bool fsr2Selectable)
        {
            switch (current)
            {
                case BackendSelection.Off:
                    return BackendSelection.FxaaLow;
                case BackendSelection.FxaaLow:
                    return BackendSelection.FxaaHigh;
                case BackendSelection.FxaaHigh:
                    return BackendSelection.Smaa;
                case BackendSelection.Smaa:
                    return BackendSelection.CustomTaa;
                case BackendSelection.CustomTaa:
                case BackendSelection.Ppv2Taa:
                    if (dlaaSelectable)
                    {
                        return BackendSelection.NvidiaDlaa;
                    }
                    return fsr2Selectable
                        ? BackendSelection.AmdFsr2
                        : BackendSelection.Off;
                case BackendSelection.NvidiaDlaa:
                    return fsr2Selectable
                        ? BackendSelection.AmdFsr2
                        : BackendSelection.Off;
                default:
                    return BackendSelection.Off;
            }
        }

        public static string NormalizeMode(
            string value,
            bool dlaaSelectable,
            bool fsr2Selectable)
        {
            if (value == LegacyModePpv2 || value == LegacyModeCustom)
            {
                return ModeTaa;
            }
            if (value == LegacyModeFsr2)
            {
                return fsr2Selectable ? ModeFsr2 : ModeOff;
            }
            if (value == ModeOff || value == ModeFxaaLow ||
                value == ModeFxaaHigh || value == ModeSmaa ||
                value == ModeTaa)
            {
                return value;
            }
            if (value == ModeDlaa)
            {
                return dlaaSelectable ? ModeDlaa : ModeOff;
            }
            if (value == ModeFsr2)
            {
                return fsr2Selectable ? ModeFsr2 : ModeOff;
            }
            return ModeOff;
        }

        public static BackendSelection ParseBackend(
            string value,
            bool dlaaSelectable,
            bool fsr2Selectable)
        {
            string normalized = NormalizeMode(
                value,
                dlaaSelectable,
                fsr2Selectable
            );
            if (normalized == ModeFxaaLow)
            {
                return BackendSelection.FxaaLow;
            }
            if (normalized == ModeFxaaHigh)
            {
                return BackendSelection.FxaaHigh;
            }
            if (normalized == ModeSmaa)
            {
                return BackendSelection.Smaa;
            }
            if (normalized == ModeTaa)
            {
                return BackendSelection.CustomTaa;
            }
            if (normalized == ModeDlaa)
            {
                return BackendSelection.NvidiaDlaa;
            }
            if (normalized == ModeFsr2)
            {
                return BackendSelection.AmdFsr2;
            }
            return BackendSelection.Off;
        }

        public static string NormalizeDlaaPreset(string value)
        {
            return value == "F" || value == "J" || value == "K" ||
                   value == "L" || value == "M"
                ? value
                : "M";
        }
    }
}
