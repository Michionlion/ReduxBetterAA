using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class AaComparisonTests
    {
        [Test]
        public void DiagnosticChoicesMapLabelsToStableBackendIds()
        {
            int[] ids = { 0, 1, 2, 3, 5, 8, 6, 7 };
            string[] labels = { "Off", "FXAA Low", "FXAA High", "SMAA", "TAA",
                "Supersampling", "NVIDIA DLAA", "FSR 2 Native AA" };
            Assert.That(DebugMenu.Backends.Length, Is.EqualTo(ids.Length));
            Assert.That(DebugMenu.Modes, Is.EqualTo(labels));
            for (int index = 0; index < ids.Length; index++)
            {
                Assert.That((int)DebugMenu.Backends[index], Is.EqualTo(ids[index]));
                Assert.That(DebugMenu.ModeName((BackendSelection)ids[index]), Is.EqualTo(labels[index]));
            }
        }

        [TestCase(2560, 1440, 16384, 400)]
        [TestCase(7680, 4320, 16384, 200)]
        public void SupersamplingFitsDevice(int width, int height, int limit, int expected)
        {
            Assert.That(AaComparison.MaximumScale(width, height, limit), Is.EqualTo(expected));
        }

        [TestCase(0f, 16)] [TestCase(.25f, 16)] [TestCase(.5f, 16)] [TestCase(1f, 16)] [TestCase(.5f, 2560)]
        public void SplitCropsFullViewsWithoutRescalingOrFlipping(float split, int width)
        {
            var left = new Texture2D(width, 16, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            var right = new Texture2D(width * 2, 32, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            var result = new RenderTexture(width, 16, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(width, 16, TextureFormat.RGBAFloat, false, true);
            var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(AaComparison.ShaderAddress));
            RenderTexture prior = RenderTexture.active;
            try
            {
                for (int y = 0; y < 16; y++) for (int x = 0; x < width; x++) left.SetPixel(x, y, new Color(x / (float)width, y / 16f, 0, .3f));
                for (int y = 0; y < 32; y++) for (int x = 0; x < width * 2; x++) right.SetPixel(x, y, new Color((x / 2) / (float)width, (y / 2) / 16f, 1, .7f));
                left.Apply(); right.Apply(); result.Create();
                material.SetTexture("_RightTex", right); material.SetFloat("_Split", split);
                material.SetFloat("_OutputWidth", width);
                Graphics.Blit(left, result, material);
                RenderTexture.active = result; readback.ReadPixels(new Rect(0, 0, width, 16), 0, 0); readback.Apply();
                for (int y = 0; y < 16; y++) for (int x = 0; x < width; x++)
                {
                    Color p = readback.GetPixel(x, y);
                    // Endpoints keep the full selected image unobstructed.
                    int center = Mathf.RoundToInt(split * width);
                    if (split > 0 && split < 1 && Mathf.Abs(x - center) <= 1)
                    {
                        Color line = x == center ? Color.white : Color.black;
                        Assert.That(p.r, Is.EqualTo(line.r).Within(.001));
                        Assert.That(p.g, Is.EqualTo(line.g).Within(.001));
                        Assert.That(p.b, Is.EqualTo(line.b).Within(.001));
                        Assert.That(p.a, Is.EqualTo(1).Within(.001));
                        continue;
                    }
                    Assert.That(p.r, Is.EqualTo(x / (float)width).Within(.001));
                    Assert.That(p.g, Is.EqualTo(y / 16f).Within(.001));
                    Assert.That(p.b, Is.EqualTo((x + .5f) / width < split ? 0 : 1).Within(.001));
                }
            }
            finally
            {
                RenderTexture.active = prior;
                foreach (Object o in new Object[] { left, right, result, readback, material }) Object.DestroyImmediate(o);
            }
        }
    }
}
