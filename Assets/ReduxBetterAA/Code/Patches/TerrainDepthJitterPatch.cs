using System;
using HarmonyLib;
using KSP.Rendering.Planets;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Patches
{
    // Redux draws PQS depth immediately, before camera jitter is applied by the
    // normal render callback. Its later terrain blend must see matching depth.
    [HarmonyPatch(typeof(PQSRenderer), "DrawPqsDepthNow")]
    internal static class TerrainDepthJitterPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Camera targetCamera, out AuxiliaryProjectionScope __state)
        {
            __state = default;
            TemporalCoordinator coordinator = TemporalCoordinator.Current;
            if (coordinator != null)
                __state.Apply(targetCamera, coordinator.AuxiliaryProjectionSource);
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, ref AuxiliaryProjectionScope __state)
        {
            __state.Restore();
            return __exception;
        }
    }
}
