using HarmonyLib;
using KSP.Map;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Patches
{
    [HarmonyPatch(typeof(Map3DFocusItemIcon), nameof(Map3DFocusItemIcon.Configure))]
    internal static class MapIconRegistrationPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Map3DFocusItemIcon __instance)
        {
            MapIconOverlay.Current?.Register(__instance.IconImage);
        }
    }
}
