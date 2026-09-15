using System;
using HarmonyLib;
using Redux.UI.Settings;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;

namespace ReduxBetterAA.Patches
{
    // Installed individually during pre-initialization. SpaceWarp invokes its
    // settings InitHooks before the mod's OnInitialized/PatchAll callback.
    // No HarmonyPatch attribute: the later renderer PatchAll must not add this
    // prefix a second time under another owner.
    internal static class FrameGenerationSettingsPatch
    {
        internal const string Owner = "ReduxBetterAA.FrameGenerationSettings";

        internal static Harmony Install()
        {
            var harmony = new Harmony(Owner);
            var target = AccessTools.Method(typeof(UitkPropertyDrawers), nameof(UitkPropertyDrawers.BuildDropdownFor));
            var prefix = AccessTools.Method(typeof(FrameGenerationSettingsPatch), nameof(Prefix));
            var patches = Harmony.GetPatchInfo(target);
            if (patches != null)
                foreach (var patch in patches.Prefixes)
                    if (patch.owner == Owner && patch.PatchMethod == prefix) return harmony;
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            return harmony;
        }

        private static bool Prefix(IConfigEntry entry,SettingsSubMenuBuilder subMenuBuilder)
        {
            if (!ReduxSceneOutput.CompatibleAssembly ||
                !ReferenceEquals(entry,FrameGenerationSettingsBinding.Entry)) return true;
            subMenuBuilder.AddComponent(new FrameGenerationSettingsDropdown(entry));
            return false;
        }
    }
}
