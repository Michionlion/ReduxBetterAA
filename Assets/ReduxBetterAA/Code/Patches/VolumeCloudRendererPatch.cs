using HarmonyLib;
using KSP.VolumeCloud;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Patches
{
    /// <summary>
    /// Reports the built-in KSP cloud temporal-transition state to Better AA.
    /// Harmony resolves the private fields once when installing the patch; the
    /// steady-state callback only compares primitive values and allocates nothing.
    /// </summary>
    [HarmonyPatch(typeof(VolumeCloudRenderer), "RenderClouds")]
    internal static class VolumeCloudRendererResizePatch
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            var type = typeof(VolumeCloudRenderer);
            bool supported = AccessTools.DeclaredMethod(type, "RenderClouds") != null &&
                AccessTools.Field(type, "_renderWidthCurrent")?.FieldType == typeof(int) &&
                AccessTools.Field(type, "_renderHeightCurrent")?.FieldType == typeof(int);
            if (!supported)
                UnityEngine.Debug.LogWarning("[ReduxBetterAA/Cloud] Transition observer unavailable on this renderer; cloud state will not be modified.");
            return supported;
        }

        [HarmonyPostfix]
        private static void Postfix(
            VolumeCloudRenderer __instance,
            int ____renderWidthCurrent,
            int ____renderHeightCurrent)
        {
            TemporalCoordinator.Current?.NotifyCloudRenderResolution(
                __instance,
                ____renderWidthCurrent,
                ____renderHeightCurrent,
                __instance.EnableTUS
            );
        }
    }
}
