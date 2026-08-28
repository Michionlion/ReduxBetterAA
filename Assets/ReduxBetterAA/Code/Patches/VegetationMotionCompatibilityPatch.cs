using System;
using System.Reflection;
using AwesomeTechnologies.VegetationSystem;
using HarmonyLib;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Patches
{
    /// <summary>
    /// Version-sensitive Harmony boundary for the Redux 2.8.5 vegetation
    /// renderer. Rendering behavior remains in VegetationMotionCompatibility.
    /// </summary>
    internal static class VegetationMotionCompatibilityPatch
    {
        private const string TargetMethodName =
            "RenderVegetationItemLODIndirect";
        private static readonly Type[] TargetSignature =
        {
            typeof(VegetationItemModelInfo),
            typeof(Bounds),
            typeof(int),
            typeof(int),
            typeof(Camera),
            typeof(ShadowCastingMode),
            typeof(int),
            typeof(bool),
            typeof(CommandBuffer)
        };

        internal static bool TryInstall(
            Harmony harmony,
            out MethodBase target,
            out string reason)
        {
            target = null;
            MethodInfo resolved;
            if (!TryResolveTarget(out resolved, out reason))
            {
                return false;
            }

            MethodInfo prefix = AccessTools.DeclaredMethod(
                typeof(VegetationMotionCompatibilityPatch),
                nameof(Prefix));
            if (prefix == null)
            {
                reason = "the compatibility prefix could not be resolved";
                return false;
            }

            harmony.Patch(resolved, new HarmonyMethod(prefix));
            target = resolved;
            reason = string.Empty;
            return true;
        }

        internal static bool TryResolveTarget(
            out MethodInfo target,
            out string reason)
        {
            Type owner = typeof(VegetationSystemPro);
            target = AccessTools.DeclaredMethod(
                owner,
                TargetMethodName,
                TargetSignature);
            if (target == null || target.ReturnType != typeof(void))
            {
                reason = "the exact Redux 2.8.5 vegetation method signature " +
                    "was not found";
                target = null;
                return false;
            }

            FieldInfo visibleBufferId = AccessTools.Field(
                owner,
                "_visibleShaderDataBufferID");
            FieldInfo indirectBufferId = AccessTools.Field(
                owner,
                "_indirectShaderDataBufferID");
            if (visibleBufferId == null || visibleBufferId.FieldType != typeof(int) ||
                indirectBufferId == null ||
                indirectBufferId.FieldType != typeof(int))
            {
                reason = "the expected vegetation shader-buffer fields were not found";
                target = null;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool Prefix(
            VegetationItemModelInfo __0,
            Bounds __1,
            int __2,
            int __3,
            Camera __4,
            ShadowCastingMode __5,
            int __6,
            bool __7,
            CommandBuffer __8,
            int ____visibleShaderDataBufferID,
            int ____indirectShaderDataBufferID)
        {
            VegetationMotionCompatibility service =
                VegetationMotionCompatibility.Current;
            return service == null || service.TryRender(
                __0,
                __1,
                __2,
                __3,
                __4,
                __5,
                __6,
                __7,
                __8,
                ____visibleShaderDataBufferID,
                ____indirectShaderDataBufferID);
        }
    }
}
