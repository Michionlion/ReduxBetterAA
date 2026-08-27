using ThunderKit.Core.Data;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    public static class InvokeThunderKitImport
    {
        /// <summary>
        /// Command-line entry point for the template's existing ThunderKit import
        /// configuration. The configuration itself remains the source of truth.
        /// </summary>
        public static void Run()
        {
            ImportConfiguration configuration =
                ThunderKitSetting.GetOrCreateSettings<ImportConfiguration>();

            if (configuration.ConfigurationIndex < 0)
            {
                configuration.ConfigurationIndex = 0;
            }

            int stepLimit = configuration.ConfigurationExecutors.Length + 1;
            for (int step = 0; step < stepLimit; step++)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    break;
                }

                int previousIndex = configuration.ConfigurationIndex;
                configuration.ImportGame();
                if (configuration.ConfigurationIndex == previousIndex ||
                    configuration.ConfigurationIndex >= configuration.ConfigurationExecutors.Length)
                {
                    break;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log(
                $"[ReduxBetterAA/Editor] ThunderKit import stopped at step " +
                $"{configuration.ConfigurationIndex}/{configuration.ConfigurationExecutors.Length}."
            );
        }
    }
}
