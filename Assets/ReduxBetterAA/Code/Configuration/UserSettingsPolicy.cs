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
        public const string ModeSmaa = "SMAA";
        public const string ModeFxaaHigh = "FXAA High";
        public const string ModeTaa = "TAA";
        public const string ModeDlaa = "NVIDIA DLAA";
        public const string ModeFsr2 = "FSR 2";

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
            choices[index++] = ModeSmaa;
            choices[index++] = ModeFxaaHigh;
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
                    return BackendSelection.Smaa;
                case BackendSelection.Smaa:
                    return BackendSelection.FxaaHigh;
                case BackendSelection.FxaaHigh:
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
    }
}
