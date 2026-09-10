using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.Rendering;
using Redux.UI.Settings;
using Redux.UI.Settings.Component;
using Redux.UI.Settings.Submenus;
using UnityEngine;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Patches
{
    /// <summary>
    /// Keeps KSP's stock MSAA selector from becoming a second, conflicting AA
    /// owner. The control remains visible as a disabled navigation hint so the
    /// graphics page explains where anti-aliasing is configured.
    /// </summary>
    [HarmonyPatch(typeof(UitkSettingsMenuManager), nameof(UitkSettingsMenuManager.ShowSubMenu))]
    internal static class StockAntialiasingControlPatch
    {
        private const string ManagedLabel =
            "Anti-aliasing (managed by Redux Better AA)";
        private const string ManagedDescription =
            "Open Settings > Mods > Redux Better AA to select the AA mode, " +
            "supersampling, sharpness, TAA stability, and DLAA preset.";
        private struct Claim
        {
            internal BaseSettingsMenuComponent Component;
            internal string Label, Description, AppliedLabel;
            internal bool Enabled;
        }
        private static readonly List<Claim> Claims = new List<Claim>();

        internal static void Restore()
        {
            foreach (var claim in Claims)
            {
                if ((string)LabelKey?.GetValue(claim.Component) != claim.AppliedLabel) continue;
                LabelKey?.SetValue(claim.Component, claim.Label);
                if ((string)DescriptionKey?.GetValue(claim.Component) == ManagedDescription)
                    DescriptionKey?.SetValue(claim.Component, claim.Description);
                if (!claim.Component.enabledSelf) claim.Component.SetEnabled(claim.Enabled);
                claim.Component.OnRelocalize();
            }
            Claims.Clear();
        }

        private static readonly FieldInfo LabelKey = AccessTools.Field(
            typeof(BaseSettingsMenuComponent),
            "LabelKey"
        );
        private static readonly FieldInfo DescriptionKey = AccessTools.Field(
            typeof(BaseSettingsMenuComponent),
            "DescriptionKey"
        );

        private static readonly FieldInfo[] MenuCollections = {
            AccessTools.Field(typeof(UitkSettingsMenuManager), "_mainMenuSubMenus"),
            AccessTools.Field(typeof(UitkSettingsMenuManager), "_pauseMenuSubMenus")
        };

        // Hook the non-generic page boundary, including pages built before mod init.
        private static void Postfix(UitkSettingsMenuManager __instance, UitkSettingsSubMenu __0)
        {
            if (!(__0 is UitkGraphicsSettingsManager)) return;
            foreach (var field in MenuCollections)
            {
                if (!(field?.GetValue(__instance) is IEnumerable menus)) continue;
                foreach (object entry in menus)
                {
                    var type = entry.GetType();
                    if (!ReferenceEquals(type.GetField("Item1")?.GetValue(entry), __0)) continue;
                    object info = type.GetField("Item2")?.GetValue(entry);
                    if (!(info?.GetType().GetProperty("Components")?.GetValue(info) is IEnumerable components)) continue;
                    foreach (object component in components)
                    {
                        if (!(component is BaseSettingsMenuComponent control)) continue;
                        string key = (string)LabelKey?.GetValue(control);
                        if (key == "Menu/Settings/Anti-aliasing") Redirect(control, ManagedLabel);
                        if (key == "Menu/Settings/Render Scale") Redirect(control, "Supersampling (managed by Redux Better AA)");
                    }
                }
            }
        }

        private static void Redirect(BaseSettingsMenuComponent baseComponent, string label)
        {
            foreach (var claim in Claims) if (ReferenceEquals(claim.Component, baseComponent)) return;
            Claims.Add(new Claim { Component = baseComponent, Label = (string)LabelKey?.GetValue(baseComponent),
                Description = (string)DescriptionKey?.GetValue(baseComponent), Enabled = baseComponent.enabledSelf,
                AppliedLabel = label });
            baseComponent.SetEnabled(false);
            LabelKey?.SetValue(baseComponent, label);
            DescriptionKey?.SetValue(baseComponent, ManagedDescription);
            baseComponent.OnRelocalize();
        }
    }

    [HarmonyPatch(typeof(UitkGraphicsSettingsManager), "OnRenderScaleChanged")]
    internal static class StockSupersamplingChangePatch
    {
        private static bool Prefix() => TemporalCoordinator.Current == null;
    }

    [HarmonyPatch(
        typeof(UitkGraphicsSettingsManager),
        "OnAntiAliasingChanged"
    )]
    internal static class StockUitkAntialiasingChangePatch
    {
        private static bool Prefix()
        {
            QualitySettings.antiAliasing = 0;
            return false;
        }
    }

    [HarmonyPatch(
        typeof(KSP.UI.GraphicsSettingsMenuManager),
        "OnAntiAliasingChanged"
    )]
    internal static class StockLegacyAntialiasingChangePatch
    {
        private static bool Prefix()
        {
            QualitySettings.antiAliasing = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(GraphicsSettings), "SetAntiAliasing")]
    internal static class StockGraphicsAntialiasingApplyPatch
    {
        private static void Prefix(ref int __0)
        {
            __0 = 0;
            QualitySettings.antiAliasing = 0;
        }
    }
}
