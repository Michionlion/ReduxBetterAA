using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Diagnostics
{
    internal sealed class BufferImageWriter
    {
        private readonly string _directory;
        private readonly Material _material;
        private readonly IssueReportManifest _manifest;

        public BufferImageWriter(string directory, Material material, IssueReportManifest manifest)
        {
            _directory = directory;
            _material = material;
            _manifest = manifest;
        }

        public void Capture(string name, Texture source, int previewMode = 0, string unavailableReason = null)
        {
            var record = new BufferCaptureRecord { name = name, frame = Time.frameCount };
            _manifest.buffers.Add(record);
            if (source == null)
            {
                record.status = "unavailable";
                record.error = unavailableReason ?? "Not allocated by the current scene/backend";
                return;
            }
            record.width = source.width;
            record.height = source.height;
            record.format = source.graphicsFormat.ToString();
            RenderTexture renderTexture = source as RenderTexture;
            if (source.dimension != TextureDimension.Tex2D ||
                (renderTexture != null && (!renderTexture.IsCreated() || renderTexture.volumeDepth != 1)))
            {
                record.status = "unavailable";
                record.error = "Target is uncreated or is not a single 2D surface";
                return;
            }
            RenderTexture previous = RenderTexture.active;
            RenderTexture copy = null;
            Texture2D readable = null;
            try
            {
                if (_material == null)
                    throw new InvalidOperationException("Capture shader unavailable");
                // Process one buffer at a time; do not retain a full GPU snapshot of all cloud targets.
                copy = RenderTexture.GetTemporary(source.width, source.height, 0,
                    RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                copy.filterMode = FilterMode.Point;
                _material.SetFloat("_PreviewMode", previewMode);
                Graphics.Blit(source, copy, _material, 0);
                RenderTexture.active = copy;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readable.Apply(false, false);
                record.rawFile = name + ".exr";
                File.WriteAllBytes(Path.Combine(_directory, record.rawFile),
                    readable.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
                UnityEngine.Object.DestroyImmediate(readable);
                readable = null;

                Graphics.Blit(source, copy, _material, 1);
                RenderTexture.active = copy;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readable.Apply(false, false);
                record.previewFile = name + ".png";
                File.WriteAllBytes(Path.Combine(_directory, record.previewFile), readable.EncodeToPNG());
                record.status = "captured";
            }
            catch (Exception exception)
            {
                record.status = "failed";
                record.error = exception.GetType().Name;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null)
                    UnityEngine.Object.DestroyImmediate(readable);
                if (copy != null)
                    RenderTexture.ReleaseTemporary(copy);
            }
        }
    }
}
