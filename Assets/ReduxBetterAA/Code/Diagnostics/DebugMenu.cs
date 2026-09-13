using System;
using ReduxBetterAA.Configuration;
using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal static class DebugMenu
    {
        internal static string[] Modes { get; private set; } = UserSettingsPolicy.BuildModeChoices(false, false);
        internal static BackendSelection[] Backends { get; private set; } = Array.ConvertAll(
            Modes, mode => UserSettingsPolicy.ParseBackend(mode, true, true));

        // The mod supplies the exact capability-filtered choices used by its
        // persistent settings. Never make optional vendor modes visible by default.
        internal static void ConfigureModes(string[] actualModeChoices)
        {
            Modes = actualModeChoices == null || actualModeChoices.Length == 0
                ? UserSettingsPolicy.BuildModeChoices(false, false)
                : (string[])actualModeChoices.Clone();
            Backends = Array.ConvertAll(Modes,
                mode => UserSettingsPolicy.ParseBackend(mode, true, true));
        }

        internal static string ModeName(BackendSelection backend) =>
            Modes[Mathf.Max(0, Array.IndexOf(Backends, backend))];

        internal static BackendSelection ModeDropdown(string label, BackendSelection selected, ref bool open)
        {
            int index = Mathf.Max(0, Array.IndexOf(Backends, selected));
            return Backends[Dropdown(label, index, Modes, ref open)];
        }

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
