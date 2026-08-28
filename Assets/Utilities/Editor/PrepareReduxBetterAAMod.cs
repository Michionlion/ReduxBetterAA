using Ksp2UnityTools.Editor.API;
using Ksp2UnityTools.Editor.Modding;
using Ksp2UnityTools.Editor.Modding.Thunderkit;
using ThunderKit.Core.Pipelines;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Utilities.Editor
{
    public static class PrepareReduxBetterAAMod
    {
        private const string ModAssetPath = "Assets/ReduxBetterAA/swinfo.asset";
        private const string DiagnosticShaderPath =
            "Assets/ReduxBetterAA/Shaders/Phase1BufferDebug.shader";
        private const string MotionVectorPassProbeShaderPath =
            "Assets/ReduxBetterAA/Shaders/Phase1MotionVectorPassProbe.shader";
        private const string VegetationMotionVectorRepairShaderPath =
            "Assets/ReduxBetterAA/Shaders/VegetationMotionVectorRepair.shader";
        private const string CustomTaaShaderPath =
            "Assets/ReduxBetterAA/Shaders/CustomTaa.shader";
        private const string MotionVectorSanitizerShaderPath =
            "Assets/ReduxBetterAA/Shaders/MotionVectorSanitizer.shader";
        private const string DepthDisocclusionMaskShaderPath =
            "Assets/ReduxBetterAA/Shaders/DepthDisocclusionMask.shader";

        /// <summary>
        /// Creates the SDK-owned addressable groups and ThunderKit pipelines,
        /// then registers the AA shaders under their runtime addresses.
        /// This method is safe to run repeatedly from Unity batch mode.
        /// </summary>
        public static void Run()
        {
            Mod mod = AssetDatabase.LoadAssetAtPath<Mod>(ModAssetPath);
            if (mod == null)
            {
                throw new System.InvalidOperationException(
                    "Redux Better AA mod asset was not found at " + ModAssetPath
                );
            }

            mod.CreateAddressablesGroups();
            BundledAssetGroupSchema bundleSchema =
                mod.allGroup.GetSchema<BundledAssetGroupSchema>();
            if (bundleSchema == null)
            {
                throw new System.InvalidOperationException(
                    "Redux Better AA's Addressables group has no bundle schema."
                );
            }
            bundleSchema.InternalBundleIdMode =
                BundledAssetGroupSchema.BundleInternalIdMode
                    .GroupGuidProjectIdEntriesHash;
            EditorUtility.SetDirty(bundleSchema);
            AddressablesTools.MakeAddressable(
                mod.allGroup,
                DiagnosticShaderPath,
                DiagnosticShaderPath
            );
            AddressablesTools.MakeAddressable(
                mod.allGroup,
                MotionVectorPassProbeShaderPath,
                MotionVectorPassProbeShaderPath
            );
            AddressablesTools.MakeAddressable(
                mod.allGroup,
                VegetationMotionVectorRepairShaderPath,
                VegetationMotionVectorRepairShaderPath
            );
            AddressablesTools.MakeAddressable(
                mod.allGroup,
                CustomTaaShaderPath,
                CustomTaaShaderPath
            );
            AddressablesTools.MakeAddressable(
                mod.allGroup,
                MotionVectorSanitizerShaderPath,
                MotionVectorSanitizerShaderPath
            );
            AddressablesTools.MakeAddressable(
                mod.allGroup,
                DepthDisocclusionMaskShaderPath,
                DepthDisocclusionMaskShaderPath
            );
            AddressableAssetSettingsDefaultObject.Settings.activeProfileId =
                mod.addressablesProfileId;
            mod.RefreshPipelines();
            UseCuratedManifest("Assets/ReduxBetterAA/Pipelines/Build for Editor.asset");
            UseCuratedManifest("Assets/ReduxBetterAA/Pipelines/Build for Player.asset");
            UseCuratedManifest("Assets/ReduxBetterAA/Pipelines/Deploy to Zip File.asset");

            EditorUtility.SetDirty(mod);
            AssetDatabase.SaveAssets();
            Debug.Log(
                "[ReduxBetterAA/Editor] Addressables and ThunderKit pipelines are ready."
            );
        }

        private static void UseCuratedManifest(string pipelinePath)
        {
            Pipeline pipeline = AssetDatabase.LoadAssetAtPath<Pipeline>(pipelinePath);
            if (pipeline == null)
            {
                throw new System.InvalidOperationException(
                    "Generated ThunderKit pipeline was not found at " + pipelinePath
                );
            }

            for (int index = 0; index < pipeline.Data.Length; index++)
            {
                StageGeneratedTextAssets generated =
                    pipeline.Data[index] as StageGeneratedTextAssets;
                if (generated == null)
                {
                    continue;
                }
                generated.Active = false;
                EditorUtility.SetDirty(generated);
            }
            EditorUtility.SetDirty(pipeline);
        }
    }
}
