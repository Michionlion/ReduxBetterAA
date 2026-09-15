using System;
using System.Reflection;
using HarmonyLib;
using KSP.Rendering.Planets;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Patches
{
    // Installed explicitly by its service; not an unconditional PatchAll target.
    internal static class TerrainMotionCompatibilityPatch
    {
        private static readonly Guid AuditedMvid = new Guid("7d0cb6df-eda7-43ac-8386-a1c00da65d32");
        private const BindingFlags DeclaredInstance = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic;

        internal static bool SupportsModule(Guid mvid) => mvid == AuditedMvid;

        internal static bool TryResolveTargets(out MethodInfo generation, out MethodInfo depth, out MethodInfo color)
        {
            generation = depth = color = null;
            Type owner = typeof(PQSRenderer);
            if (!SupportsModule(owner.Module.ModuleVersionId)) return false;
            generation = owner.GetMethod("RenderPQS", DeclaredInstance);
            depth = owner.GetMethod("DrawPqsDepthNow", DeclaredInstance, null, new[] { typeof(Material), typeof(Camera) }, null);
            color = owner.GetMethod("DrawPQSQuads", DeclaredInstance, null,
                new[] { typeof(Material), typeof(int), typeof(Camera), typeof(MaterialPropertyBlock) }, null);
            // Tokens are meaningful only together with this exact module MVID.
            return Matches(generation, 100719022, 7) && Matches(depth, 100719041, 2) && Matches(color, 100719046, 4) &&
                owner.GetField("DepthBuffer", BindingFlags.Public | BindingFlags.Instance)?.FieldType == typeof(RenderTexture) &&
                owner.GetField("SourceCamera", BindingFlags.Public | BindingFlags.Instance)?.FieldType == typeof(Camera);
        }

        private static bool Matches(MethodInfo method, int token, int parameters) => method != null &&
            method.MetadataToken == token && !method.IsStatic && method.ReturnType == typeof(void) &&
            method.GetParameters().Length == parameters;

        internal static bool TryInstall(Harmony harmony, out string reason)
        {
            MethodInfo generation, depth, color;
            if (!TryResolveTargets(out generation, out depth, out color))
            {
                reason = "The audited PQS module or boundary signatures are unavailable.";
                return false;
            }
            Patch(harmony, generation, nameof(GenerationPrefix));
            Patch(harmony, depth, nameof(DepthPrefix));
            Patch(harmony, color, nameof(ColorPrefix));
            reason = string.Empty;
            return true;
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefix)
        {
            harmony.Patch(target, new HarmonyMethod(AccessTools.DeclaredMethod(typeof(TerrainMotionCompatibilityPatch), prefix)),
                new HarmonyMethod(AccessTools.DeclaredMethod(typeof(TerrainMotionCompatibilityPatch), nameof(Postfix))),
                finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(TerrainMotionCompatibilityPatch), nameof(Finalizer))));
        }

        private static void GenerationPrefix(PQSRenderer __instance, out TerrainMotionCompatibility.Observation __state) =>
            __state = Begin(__instance, null, null, TerrainMotionCompatibility.Boundary.Generation);

        private static void DepthPrefix(PQSRenderer __instance, Material selectedMaterial, Camera targetCamera,
            out TerrainMotionCompatibility.Observation __state) =>
            __state = Begin(__instance, targetCamera, selectedMaterial, TerrainMotionCompatibility.Boundary.Depth);

        private static void ColorPrefix(PQSRenderer __instance, Material selectedMaterial, Camera targetCamera,
            out TerrainMotionCompatibility.Observation __state) =>
            __state = Begin(__instance, targetCamera, selectedMaterial, TerrainMotionCompatibility.Boundary.Color);

        private static TerrainMotionCompatibility.Observation Begin(PQSRenderer renderer, Camera camera, Material material,
            TerrainMotionCompatibility.Boundary boundary)
        {
            TerrainMotionCompatibility service = TerrainMotionCompatibility.Current;
            try { return service == null ? default : service.Begin(renderer, camera, material, boundary); }
            catch { service?.FailObservation(); return default; }
        }

        private static void Postfix(bool __runOriginal, ref TerrainMotionCompatibility.Observation __state)
        {
            try { __state.Service?.Complete(__state, __runOriginal); }
            catch { __state.Service?.FailObservation(); }
        }

        private static Exception Finalizer(Exception __exception, ref TerrainMotionCompatibility.Observation __state)
        {
            if (__exception != null) __state.Service?.Reject(__state);
            return __exception;
        }
    }
}
