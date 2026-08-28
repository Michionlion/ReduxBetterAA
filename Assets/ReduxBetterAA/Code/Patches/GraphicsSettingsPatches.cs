using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.Rendering;
using Redux.UI.Settings;
using Redux.UI.Settings.Component;
using Redux.UI.Settings.Submenus;
using UnityEngine;

namespace ReduxBetterAA.Patches
{
    /// <summary>
    /// Keeps KSP's stock MSAA selector from becoming a second, conflicting AA
    /// owner. The control remains visible as a disabled navigation hint so the
    /// graphics page explains where anti-aliasing is configured.
    /// </summary>
    [HarmonyPatch]
    internal static class StockAntialiasingControlPatch
    {
        private const string ComponentKey = "_antiAliasing";
        private const string ManagedLabel =
            "Anti-aliasing (managed by Redux Better AA)";
        private const string ManagedDescription =
            "Open Settings > Mods > Redux Better AA to select the AA mode, " +
            "sharpness, TAA stability, and DLAA preset.";

        private static readonly FieldInfo LabelKey = AccessTools.Field(
            typeof(BaseSettingsMenuComponent),
            "LabelKey"
        );
        private static readonly FieldInfo DescriptionKey = AccessTools.Field(
            typeof(BaseSettingsMenuComponent),
            "DescriptionKey"
        );

        private static MethodBase TargetMethod()
        {
            List<MethodInfo> methods = AccessTools.GetDeclaredMethods(
                typeof(SettingsSubMenuBuilder)
            );
            for (int index = 0; index < methods.Count; index++)
            {
                MethodInfo method = methods[index];
                if (method.Name == "BuildBindingToObject" &&
                    method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 2)
                {
                    return method.MakeGenericMethod(
                        typeof(UitkGraphicsSettingsManager)
                    );
                }
            }
            return null;
        }

        private static void Postfix(
            Dictionary<string, ISettingsMenuComponent> __result)
        {
            if (__result == null)
            {
                return;
            }

            ISettingsMenuComponent component;
            if (!__result.TryGetValue(ComponentKey, out component) ||
                component == null)
            {
                return;
            }

            component.SetEnabled(false);
            BaseSettingsMenuComponent baseComponent =
                component as BaseSettingsMenuComponent;
            if (baseComponent == null)
            {
                return;
            }

            LabelKey?.SetValue(baseComponent, ManagedLabel);
            DescriptionKey?.SetValue(baseComponent, ManagedDescription);
            baseComponent.OnRelocalize();
        }
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
