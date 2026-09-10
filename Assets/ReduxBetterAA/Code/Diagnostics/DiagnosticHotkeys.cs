using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal static class DiagnosticHotkeys
    {
        internal static bool ControlDown() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        internal static bool AltDown() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        internal static bool ShiftDown() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        internal static bool AnyModifierDown() => ControlDown() || AltDown() || ShiftDown();
    }
}
