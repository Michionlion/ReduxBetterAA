using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal static class CloudDiagnosticCapture
    {
        private const string RendererTypeName =
            "KSP.VolumeCloud.VolumeCloudRenderer";

        private static readonly BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] CaptureFields =
        {
            "_finalSceneColor",
            "_finalSceneColor",
            "_preUpsample",
            "_newCloudRaysColorBuffer",
            "_previousCloudLayerRaysColorBuffer"
        };

        private static readonly string[] CaptureChannels =
        {
            "rgb",
            "alpha",
            "rgb",
            "alpha",
            "alpha"
        };

        private static readonly string[] CaptureSuffixes =
        {
            "cloud-final-rgb",
            "cloud-final-alpha",
            "cloud-pre-upsample-rgb",
            "cloud-new-rays-alpha",
            "cloud-previous-rays-alpha"
        };

        public static CloudDiagnosticRecord CaptureRecord(Camera camera)
        {
            var record = new CloudDiagnosticRecord
            {
                selectedCamera = camera == null ? "NoCamera" : camera.name,
                cameraAvailable = camera != null,
                textures = new CloudTextureRecord[0]
            };
            if (camera == null)
            {
                record.status = "No selected diagnostic camera";
                return record;
            }

            Component renderer = FindCloudRenderer(camera);
            if (renderer == null)
            {
                record.status = "Selected camera has no VolumeCloudRenderer";
                return record;
            }

            Type type = renderer.GetType();
            record.rendererFound = true;
            record.rendererType = type.FullName;
            Behaviour behaviour = renderer as Behaviour;
            record.enabled = behaviour != null && behaviour.enabled;
            record.configuration = DescribeConfiguration(
                ReadField(type, renderer, "configuration")
            );
            record.enableTaa = ReadBoolean(type, renderer, "EnableTAA");
            record.enableDynamicResolution = ReadBoolean(
                type,
                renderer,
                "EnableDynamicResolution"
            );
            record.dynamicResolutionLevel = ReadInteger(
                type,
                renderer,
                "_dynamicResolutionLevel"
            );
            record.useScaledCloudsOnly = ReadBoolean(
                type,
                renderer,
                "_IsUseScaleCloudsOnly"
            );
            record.readyToEnableTemporalUpscaling = ReadBoolean(
                type,
                renderer,
                "_readyToEnableTemporalUpscaling"
            );
            record.startEnableTemporalUpscaling = ReadBoolean(
                type,
                renderer,
                "_startEnableTemporalUpscaling"
            );
            record.startDisableTemporalUpscaling = ReadBoolean(
                type,
                renderer,
                "_startDisableTemporalUpscaling"
            );
            record.firstFrame = ReadBoolean(type, renderer, "_firstFrame");
            record.readComplete = ReadBoolean(type, renderer, "_readIsComplete");
            record.sampleCountSubmitted = ReadBoolean(
                type,
                renderer,
                "_sampleCountSubmmited"
            );
            record.renderTextureChanged = ReadBoolean(
                type,
                renderer,
                "_renderTextureChanged"
            );
            record.resolutionScale = ReadSingle(
                type,
                renderer,
                "_resolutionScale"
            );
            record.renderWidth = ReadInteger(type, renderer, "_renderWidth");
            record.renderHeight = ReadInteger(type, renderer, "_renderHeight");
            record.renderWidthCurrent = ReadInteger(
                type,
                renderer,
                "_renderWidthCurrent"
            );
            record.renderHeightCurrent = ReadInteger(
                type,
                renderer,
                "_renderHeightCurrent"
            );
            record.originalWidth = ReadInteger(type, renderer, "_originalWidth");
            record.originalHeight = ReadInteger(type, renderer, "_originalHeight");
            record.textures = CaptureTextureRecords(type, renderer);
            record.status = "Captured " + record.textures.Length.ToString(
                CultureInfo.InvariantCulture
            ) + " cloud render-target descriptors";
            return record;
        }

        public static int CaptureTextures(
            Camera camera,
            string directory,
            string captureBaseName,
            out string status)
        {
            if (camera == null)
            {
                status = "no selected diagnostic camera";
                return 0;
            }

            Component renderer = FindCloudRenderer(camera);
            if (renderer == null)
            {
                status = "selected camera has no VolumeCloudRenderer";
                return 0;
            }

            int captured = 0;
            string lastFailure = string.Empty;
            Type type = renderer.GetType();
            for (int index = 0; index < CaptureFields.Length; index++)
            {
                try
                {
                    FieldInfo field = type.GetField(
                        CaptureFields[index],
                        InstanceFields
                    );
                    RenderTexture source = field == null
                        ? null
                        : field.GetValue(renderer) as RenderTexture;
                    if (source == null || !source.IsCreated())
                    {
                        lastFailure = CaptureFields[index] + " is unavailable";
                        continue;
                    }

                    string path = Path.Combine(
                        directory,
                        captureBaseName + "-" + CaptureSuffixes[index] + ".png"
                    );
                    CaptureTexture(source, CaptureChannels[index], path);
                    captured++;
                }
                catch (Exception exception)
                {
                    lastFailure = CaptureFields[index] + " failed with " +
                                  exception.GetType().Name;
                }
            }

            status = captured.ToString(CultureInfo.InvariantCulture) + "/" +
                     CaptureFields.Length.ToString(CultureInfo.InvariantCulture) +
                     " source images written";
            if (!string.IsNullOrEmpty(lastFailure))
            {
                status += "; last issue: " + lastFailure;
            }
            return captured;
        }

        private static Component FindCloudRenderer(Camera camera)
        {
            Component[] components = camera.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                Component component = components[index];
                if (component != null && string.Equals(
                        component.GetType().FullName,
                        RendererTypeName,
                        StringComparison.Ordinal))
                {
                    return component;
                }
            }
            return null;
        }

        private static CloudTextureRecord[] CaptureTextureRecords(
            Type type,
            object renderer)
        {
            FieldInfo[] fields = type.GetFields(InstanceFields);
            var records = new List<CloudTextureRecord>();
            for (int index = 0; index < fields.Length; index++)
            {
                FieldInfo field = fields[index];
                RenderTexture texture;
                try
                {
                    texture = field.GetValue(renderer) as RenderTexture;
                }
                catch
                {
                    continue;
                }
                if (texture == null)
                {
                    continue;
                }
                records.Add(new CloudTextureRecord
                {
                    field = field.Name,
                    name = texture.name,
                    width = texture.width,
                    height = texture.height,
                    format = texture.format.ToString(),
                    graphicsFormat = texture.graphicsFormat.ToString(),
                    created = texture.IsCreated()
                });
            }
            return records.ToArray();
        }

        private static object ReadField(Type type, object instance, string name)
        {
            try
            {
                FieldInfo field = type.GetField(name, InstanceFields);
                return field == null ? null : field.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadBoolean(Type type, object instance, string name)
        {
            object value = ReadField(type, instance, name);
            return value is bool && (bool)value;
        }

        private static int ReadInteger(Type type, object instance, string name)
        {
            object value = ReadField(type, instance, name);
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static float ReadSingle(Type type, object instance, string name)
        {
            object value = ReadField(type, instance, name);
            if (value == null)
            {
                return 0.0f;
            }
            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0.0f;
            }
        }

        private static string DescribeConfiguration(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            UnityEngine.Object unityObject = value as UnityEngine.Object;
            return unityObject == null ? value.ToString() : unityObject.name;
        }

        private static void CaptureTexture(
            RenderTexture source,
            string channel,
            string path)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                RenderTexture.active = source;
                readable = new Texture2D(
                    source.width,
                    source.height,
                    TextureFormat.RGBA32,
                    false,
                    true
                );
                readable.ReadPixels(
                    new Rect(0.0f, 0.0f, source.width, source.height),
                    0,
                    0,
                    false
                );
                readable.Apply(false, false);

                Color32[] pixels = readable.GetPixels32();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color32 pixel = pixels[index];
                    if (string.Equals(channel, "alpha", StringComparison.Ordinal))
                    {
                        byte alpha = pixel.a;
                        pixels[index] = new Color32(alpha, alpha, alpha, 255);
                    }
                    else
                    {
                        pixel.a = 255;
                        pixels[index] = pixel;
                    }
                }
                readable.SetPixels32(pixels);
                readable.Apply(false, false);
                File.WriteAllBytes(path, readable.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null)
                {
                    UnityEngine.Object.Destroy(readable);
                }
            }
        }
    }
}
