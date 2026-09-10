using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal static class DebugMenu
    {
        internal static readonly string[] Modes = {
            "Off", "FXAA Low", "FXAA High", "SMAA", "PPv2 TAA", "TAA", "NVIDIA DLAA", "FSR2 Native AA", "Supersampling"
        };

        // A small IMGUI dropdown: expands in the window so it also works near
        // screen edges, without creating a modal input layer over the game.
        internal static int Dropdown(string label, int selected, string[] options, ref bool open)
        {
            if (GUILayout.Button(label + ": " + options[selected] + (open ? "  ▲" : "  ▼"), GUILayout.Height(28)))
                open = !open;
            if (!open) return selected;
            int next = GUILayout.SelectionGrid(-1, options, 1);
            if (next < 0) return selected;
            open = false;
            return next;
        }
    }
}
