using HarmonyLib;
using KSP.Rendering;
using KSP.Sim;
using KSP.Sim.impl;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Patches
{
    [HarmonyPatch(typeof(RenderScalePresenter), nameof(RenderScalePresenter.Configure))]
    internal static class PresenterConfiguredPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Phase1ProbeService.Current?.MarkDirty(ProbeDirtyReason.PresenterChanged);
            TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.RenderScaleChanged);
        }
    }

    [HarmonyPatch(typeof(RenderScalePresenter), nameof(RenderScalePresenter.SetRenderScalePercent))]
    internal static class PresenterScaleChangedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Phase1ProbeService.Current?.MarkDirty(ProbeDirtyReason.PresenterChanged);
            TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.RenderScaleChanged);
        }
    }

    [HarmonyPatch(typeof(RenderScalePresenter), nameof(RenderScalePresenter.SetRenderingEnabled))]
    internal static class PresenterEnabledPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Phase1ProbeService.Current?.MarkDirty(ProbeDirtyReason.PresenterChanged);
            TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.RenderScaleChanged);
        }
    }

    [HarmonyPatch(typeof(UniverseCameraManager), nameof(UniverseCameraManager.SetPrimaryScreenCamera))]
    internal static class PrimaryCameraChangedPatch
    {
        private static bool _hasCamera;
        private static CameraID _lastCamera;

        [HarmonyPostfix]
        private static void Postfix(CameraID camera, bool force)
        {
            if (_hasCamera && _lastCamera.Equals(camera))
            {
                return;
            }
            _hasCamera = true;
            _lastCamera = camera;
            Phase1ProbeService.Current?.MarkDirty(ProbeDirtyReason.ActiveCameraChanged);
            TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.CameraCut);
        }
    }

    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.SetCurrentUnityCamera))]
    internal static class CurrentUnityCameraChangedPatch
    {
        private static Camera _lastCamera;

        [HarmonyPostfix]
        private static void Postfix(Camera camera)
        {
            if (object.ReferenceEquals(_lastCamera, camera))
            {
                return;
            }
            _lastCamera = camera;
            Phase1ProbeService.Current?.MarkDirty(ProbeDirtyReason.ActiveCameraChanged);
            TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.CameraCut);
        }
    }

    [HarmonyPatch(typeof(UniverseCameraManager), "OnFloatingOriginSnapped")]
    internal static class FloatingOriginSnappedPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            TemporalCoordinator.Current?.NotifyOriginRebased();
        }
    }

    [HarmonyPatch(typeof(RigidbodyBehavior), nameof(RigidbodyBehavior.StartPhysX))]
    internal static class KspPhysicsBodyStartedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Rigidbody __result)
        {
            KspPhysicsRenderInterpolation.Current?.Apply(__result);
        }
    }

    [HarmonyPatch(
        typeof(RigidbodyBehavior),
        nameof(RigidbodyBehavior.StopPhysX),
        new[] { typeof(Transform), typeof(Vector3?) })]
    internal static class KspPhysicsBodyStoppingPatch
    {
        [HarmonyPrefix]
        private static void Prefix(RigidbodyBehavior __instance)
        {
            KspPhysicsRenderInterpolation.Current?.Restore(
                __instance.activeRigidBody
            );
        }
    }
}
