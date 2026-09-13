using HarmonyLib;
using KSP.Rendering;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Patches
{
    [HarmonyPatch(typeof(RenderScalePresenter), "ShouldUseRenderScale")]
    internal static class SceneOutputScalePatch
    {
        [HarmonyPrepare] private static bool Prepare() => ReduxSceneOutput.CompatibleAssembly;
        [HarmonyPrefix]
        private static bool Prefix(RenderScalePresenter __instance, ref bool __result)
        {
            if (!ReduxSceneOutput.TryOverrideRenderScale(__instance, out bool enabled)) return true;
            __result = enabled;
            return false;
        }
    }

    [HarmonyPatch(typeof(RenderScalePresenter), "RecreateRenderTarget")]
    internal static class SceneOutputTargetPatch
    {
        [HarmonyPrepare] private static bool Prepare() => ReduxSceneOutput.CompatibleAssembly;
        [HarmonyPrefix] private static void Prefix(RenderScalePresenter __instance) => ReduxSceneOutput.PresenterChanging(__instance);
    }

    [HarmonyPatch(typeof(RenderScalePresenter), nameof(RenderScalePresenter.Configure))]
    internal static class SceneOutputGraphPatch
    {
        [HarmonyPrepare] private static bool Prepare() => ReduxSceneOutput.CompatibleAssembly;
        [HarmonyPrefix] private static void Prefix(RenderScalePresenter __instance) => ReduxSceneOutput.PresenterChanging(__instance);
    }

    [HarmonyPatch(typeof(RenderScalePresenter), nameof(RenderScalePresenter.SetRenderingEnabled))]
    internal static class SceneOutputEnabledPatch
    {
        [HarmonyPrepare] private static bool Prepare() => ReduxSceneOutput.CompatibleAssembly;
        [HarmonyPrefix] private static void Prefix(RenderScalePresenter __instance) => ReduxSceneOutput.PresenterChanging(__instance);
    }

    [HarmonyPatch(typeof(RenderScalePresenter), "RebuildPresentBuffer")]
    internal static class SceneOutputBufferPatch
    {
        [HarmonyPrepare] private static bool Prepare() => ReduxSceneOutput.CompatibleAssembly;
        [HarmonyPostfix] private static void Postfix(RenderScalePresenter __instance) => ReduxSceneOutput.PresenterBufferRebuilt(__instance);
    }
}
