using System;
using System.Reflection;
using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal static class VendorCapabilityProbe
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        public static CapabilityRecord Capture(bool probeRuntime)
        {
            return new CapabilityRecord
            {
                // Unity 6 guarantees render-texture support and deprecates the
                // former SystemInfo query as an always-true property.
                supportsRenderTextures = true,
                supportsMotionVectors = ReadSupportsMotionVectors(),
                motionVectorSupportSource = "UnityEngine.SystemInfo.supportsMotionVectors",
                supportsAsyncGpuReadback = SystemInfo.supportsAsyncGPUReadback,
                nvidia = Probe(
                    "NVIDIA",
                    "UnityEngine.NVIDIAModule",
                    "UnityEngine.NVIDIA.NVUnityPlugin",
                    "UnityEngine.NVIDIA.GraphicsDevice",
                    "UnityEngine.NVIDIA.GraphicsDeviceFeature",
                    "DLSS",
                    probeRuntime
                ),
                amd = Probe(
                    "AMD",
                    "UnityEngine.AMDModule",
                    "UnityEngine.AMD.AMDUnityPlugin",
                    "UnityEngine.AMD.GraphicsDevice",
                    null,
                    "FSR2",
                    probeRuntime
                )
            };
        }

        private static VendorModuleRecord Probe(
            string vendor,
            string assemblyName,
            string pluginTypeName,
            string deviceTypeName,
            string featureEnumTypeName,
            string featureName,
            bool probeRuntime)
        {
            var record = new VendorModuleRecord
            {
                vendor = vendor,
                featureName = featureName,
                nativeFeatureCreationAttempted = false,
                status = "Managed module absent"
            };

            try
            {
                Type pluginType = Type.GetType(pluginTypeName + ", " + assemblyName, false);
                Type deviceType = Type.GetType(deviceTypeName + ", " + assemblyName, false);
                record.managedAssemblyPresent = pluginType != null || deviceType != null;
                record.apiTypesPresent = pluginType != null && deviceType != null;

                Assembly assembly = pluginType == null ? deviceType?.Assembly : pluginType.Assembly;
                record.managedAssemblyVersion = assembly == null
                    ? string.Empty
                    : assembly.GetName().Version.ToString();

                if (!record.apiTypesPresent)
                {
                    record.status = record.managedAssemblyPresent
                        ? "Managed module present; required API types incomplete"
                        : "Managed module absent";
                    return record;
                }

                MethodInfo isLoaded = pluginType.GetMethod("IsLoaded", PublicStatic);
                MethodInfo load = pluginType.GetMethod("Load", PublicStatic);
                record.pluginWasLoaded = InvokeBoolean(isLoaded, null);

                if (!probeRuntime)
                {
                    record.status = record.pluginWasLoaded
                        ? "Plugin already loaded; active feature query disabled by configuration"
                        : "Managed API present; runtime load query disabled by configuration";
                    return record;
                }

                record.pluginLoadAttempted = !record.pluginWasLoaded;
                record.pluginLoadSucceeded = record.pluginWasLoaded || InvokeBoolean(load, null);
                if (!record.pluginLoadSucceeded)
                {
                    record.status = "Managed API present; native Unity vendor plugin did not load";
                    return record;
                }

                PropertyInfo deviceProperty = deviceType.GetProperty("device", PublicStatic);
                object device = deviceProperty?.GetValue(null, null);
                record.graphicsDeviceAvailable = device != null;
                if (device == null)
                {
                    record.status = "Vendor plugin loaded; graphics device unavailable";
                    return record;
                }

                PropertyInfo versionProperty = deviceType.GetProperty("version", PublicStatic);
                object version = versionProperty?.GetValue(null, null);
                if (version is uint uintVersion)
                {
                    record.graphicsDeviceVersion = uintVersion;
                }

                if (featureEnumTypeName == null)
                {
                    record.status = "Vendor plugin and graphics device available; no feature created";
                    return record;
                }

                Type featureType = Type.GetType(featureEnumTypeName + ", " + assemblyName, false);
                MethodInfo query = deviceType.GetMethod("IsFeatureAvailable", PublicInstance);
                if (featureType == null || query == null)
                {
                    record.status = "Vendor device available; feature query API absent";
                    return record;
                }

                object feature = Enum.Parse(featureType, featureName);
                record.featureQueryAttempted = true;
                record.featureAvailable = InvokeBoolean(query, device, feature);
                record.status = record.featureAvailable
                    ? featureName + " reported available; no feature context created"
                    : featureName + " reported unavailable; no feature context created";
            }
            catch (Exception exception)
            {
                Exception root = exception is TargetInvocationException invocation &&
                                 invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;
                record.errorType = root.GetType().FullName;
                record.errorMessage = root.Message;
                record.status = "Probe failed safely";
            }

            return record;
        }

        private static bool ReadSupportsMotionVectors()
        {
            PropertyInfo property = typeof(SystemInfo).GetProperty(
                "supportsMotionVectors",
                PublicStatic
            );
            object value = property?.GetValue(null, null);
            return value is bool supported && supported;
        }

        private static bool InvokeBoolean(MethodInfo method, object instance, params object[] arguments)
        {
            if (method == null)
            {
                return false;
            }
            object value = method.Invoke(instance, arguments);
            return value is bool result && result;
        }
    }
}
