using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    internal static class ValidateAssetBundleIsolation
    {
        public static void Run()
        {
            string firstPath = ReadArgument("-firstBundle=");
            string secondPath = ReadArgument("-secondBundle=");
            if (!File.Exists(firstPath) || !File.Exists(secondPath))
            {
                throw new FileNotFoundException(
                    "Both -firstBundle and -secondBundle must name existing files."
                );
            }

            AssetBundle first = null;
            AssetBundle second = null;
            try
            {
                first = AssetBundle.LoadFromFile(firstPath);
                if (first == null)
                {
                    throw new InvalidOperationException(
                        "The first AssetBundle could not be loaded: " + firstPath
                    );
                }

                second = AssetBundle.LoadFromFile(secondPath);
                if (second == null)
                {
                    throw new InvalidOperationException(
                        "The second AssetBundle could not be loaded after the first: " +
                        secondPath
                    );
                }

                Debug.Log(
                    "[ReduxBetterAA/Editor] AssetBundle isolation passed: " +
                    Path.GetFileName(firstPath) + " then " +
                    Path.GetFileName(secondPath) + "."
                );
            }
            finally
            {
                if (second != null)
                {
                    second.Unload(true);
                }
                if (first != null)
                {
                    first.Unload(true);
                }
            }
        }

        private static string ReadArgument(string prefix)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index].StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index].Substring(prefix.Length);
                }
            }
            return string.Empty;
        }
    }
}
