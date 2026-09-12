using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Player;
using UnityEngine;

namespace ReduxBetterAA.Editor
{
    public static class BuildMod
    {
        private const string GroupName = "addressables_ReduxBetterAA_all";

        public static void Prepare()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) throw new InvalidOperationException("Missing Addressables settings");
            var group = settings.FindGroup(GroupName);
            if (group == null) throw new InvalidOperationException("Missing Better AA shader group");
            settings.activeProfileId = settings.profileSettings.GetProfileId("ReduxBetterAA");
            foreach (var candidate in settings.groups)
            {
                if (candidate == null) continue;
                var schema = candidate.GetSchema<BundledAssetGroupSchema>();
                if (schema == null) continue;
                schema.IncludeInBuild = candidate == group;
                if (candidate == group)
                    schema.InternalBundleIdMode = BundledAssetGroupSchema.BundleInternalIdMode.GroupGuidProjectIdEntriesHash;
            }
            foreach (string path in Directory.GetFiles("Assets/ReduxBetterAA/Shaders", "*.shader"))
            {
                string address = path.Replace('\\', '/');
                var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(address), group);
                entry.address = address;
            }
            AssetDatabase.SaveAssets();
        }

        public static void Package()
        {
            Prepare();
            // A new staging directory prevents old DLLs or bundles entering this build.
            string staging = Path.Combine("Library", "BetterAA", Guid.NewGuid().ToString("N"));
            string scripts = Path.Combine(staging, "scripts");
            Directory.CreateDirectory(scripts);
            var compilation = new ScriptCompilationSettings
            {
                target = BuildTarget.StandaloneWindows64,
                group = BuildTargetGroup.Standalone,
                options = ScriptCompilationOptions.None
            };
            var result = PlayerBuildInterface.CompilePlayerScripts(compilation, scripts);
            string assembly = Path.Combine(scripts, "ReduxBetterAA.dll");
            if (result.assemblies == null || !File.Exists(assembly))
                throw new InvalidOperationException("Player script compilation failed");

            // Addressables compiles scripts again and may clear the previous output.
            string payload = Path.Combine(staging, "payload");
            Directory.CreateDirectory(payload);
            File.Copy(assembly, Path.Combine(payload, "ReduxBetterAA.dll"));
            UnityEditor.AddressableAssets.Settings.AddressableAssetSettings.BuildPlayerContent(
                out AddressablesPlayerBuildResult content);
            if (!string.IsNullOrEmpty(content.Error)) throw new InvalidOperationException(content.Error);

            File.Copy("Assets/ReduxBetterAA/Copied/swinfo.json", Path.Combine(payload, "swinfo.json"));
            string addressables = Path.Combine(payload, "addressables");
            Directory.CreateDirectory(addressables);
            const string built = "Library/com.unity.addressables/aa/Windows";
            File.Copy(Path.Combine(built, "catalog.json"), Path.Combine(addressables, "catalog.json"));
            File.Copy(Path.Combine(built, "catalog.hash"), Path.Combine(addressables, "catalog.hash"));
            string bundles = Path.Combine(addressables, "StandaloneWindows64");
            Directory.CreateDirectory(bundles);
            if (content.AssetBundleBuildResults.Count != 1)
                throw new InvalidOperationException("Expected exactly one Better AA shader bundle");
            string bundle = content.AssetBundleBuildResults[0].FilePath;
            File.Copy(bundle, Path.Combine(bundles, Path.GetFileName(bundle)));

            Directory.CreateDirectory("Deploy");
            string archive = Path.Combine(staging, "ReduxBetterAA.zip");
            ZipFile.CreateFromDirectory(payload, archive);
            File.Copy(archive, "Deploy/ReduxBetterAA.zip", true);
            Debug.Log("[ReduxBetterAA/Build] Package complete: Deploy/ReduxBetterAA.zip");
        }
    }
}
