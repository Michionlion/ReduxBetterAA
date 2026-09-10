using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReduxBetterAA.Tests
{
    public sealed class CustomTaaCostTests
    {
        [Serializable] private sealed class Result {
            public string note, gpu, unity;
            public int width = 2560, height = 1440, framesPerBatch = 32;
            public double[] baselineMs = new double[4], candidateMs = new double[4];
        }

        [TestCase("initial")] [TestCase("shimmer")] [TestCase("pan")]
        public void RecordAlternatingSynchronizedResolveCost(string stage)
        {
            const int width = 2560, height = 1440;
            var baseline = new Material(AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/ReduxBetterAA/Tests/EditMode/Fixtures/" + (stage == "pan" ? "CustomTaaPanBaseline.shader" : stage == "shimmer" ? "CustomTaaShimmerBaseline.shader" : "CustomTaaBaseline.shader")));
            var candidate = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/CustomTaa.shader"));
            var source = Target(width, height, RenderTextureFormat.ARGBHalf);
            var history = Target(width, height, RenderTextureFormat.ARGBHalf);
            var historyWrite = Target(width, height, RenderTextureFormat.ARGBHalf);
            var resolve = Target(width, height, RenderTextureFormat.ARGBHalf);
            var output = Target(width, height, RenderTextureFormat.ARGBHalf);
            var depth = Target(width, height, RenderTextureFormat.RFloat);
            var depthHistory = Target(width, height, RenderTextureFormat.RFloat);
            var depthWrite = Target(width, height, RenderTextureFormat.RFloat);
            var motion = Target(width, height, RenderTextureFormat.RGHalf);
            var pattern = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true);
            var fence = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            RenderTexture priorActive = RenderTexture.active;
            Vector4 priorZ = Shader.GetGlobalVector("_ZBufferParams");
            try {
                var pixels = new Color[128 * 128];
                for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
                    pixels[y * 128 + x] = new Color((x * 13 + y * 7) % 23 / 23f,
                        (x * 5 + y * 11) % 19 / 19f, (x + y * 3) % 17 / 17f, 1);
                pattern.SetPixels(pixels); pattern.Apply();
                Graphics.Blit(pattern, source); Graphics.Blit(pattern, history);
                Clear(depth, Color.white); Clear(depthHistory, new Color(.5f, 0, 0, 0));
                Clear(motion, new Color(.37f / width, -.29f / height, 0, 0));
                Shader.SetGlobalVector("_ZBufferParams", new Vector4(1, 1, 0, 0));
                foreach (Material m in new[] { baseline, candidate }) {
                    m.SetTexture("_HistoryTex", history); m.SetTexture("_CameraDepthTexture", depth);
                    m.SetTexture("_HistoryDepthTex", depthHistory); m.SetTexture("_ReduxBetterAAMotionVectors", motion);
                    m.SetVector("_SourceDimensions", new Vector4(width, height, 1f / width, 1f / height));
                    m.SetVector("_Jitter", new Vector2(.23f / width, -.17f / height));
                    m.SetFloat("_HistoryValid", 1); m.SetFloat("_StationaryHistory", .99f);
                    m.SetFloat("_MovingHistory", .1f); m.SetFloat("_MotionResponsePixels", 8);
                    m.SetFloat("_MaximumMotionPixels", 256); m.SetFloat("_DepthThreshold", .01f);
                    m.SetFloat("_DepthEdgeStability", .75f); m.SetFloat("_VarianceGamma", 1.25f);
                    m.SetFloat("_ReactiveScale", 2); m.SetFloat("_NoDepthHistory", .25f);
                }
                candidate.SetFloat("_MotionResponsePixels",Configuration.CustomTaaConfig.Conservative.MotionResponsePixels);
                var result = new Result { gpu = SystemInfo.graphicsDeviceName, unity = Application.unityVersion,
                    note = "Synchronized GPU+CPU wall time per full resolve pipeline. Persistent native-size textures, " +
                    "32 dispatches then one 1-pixel ReadPixels fence. Four batches per arm in ABBA order after warm-up. " +
                    "Includes CPU submission, driver, readback; not a hardware GPU timestamp or a gameplay FPS prediction. " +
                    "Static texture fixture, no sanitizer/UI/game. " + (stage != "initial" ? "Both arms 3 blits." : "Baseline 4 blits, candidate 3.") + " Sharpness zero." };
                for (int round = -1; round < 4; round++) {
                    for (int turn = 0; turn < 2; turn++) {
                        bool old = (round % 2 == 0) == (turn == 0);
                        Material material = old ? baseline : candidate;
                        Fence(output, fence);
                        var timer = Stopwatch.StartNew();
                        for (int frame = 0; frame < result.framesPerBatch; frame++) {
                            bool extraCopy = old && stage == "initial";
                            Graphics.Blit(source, extraCopy ? resolve : historyWrite, material, 0);
                            if (extraCopy) Graphics.Blit(resolve, historyWrite);
                            Graphics.Blit(source, depthWrite, material, 1);
                            Graphics.Blit(extraCopy ? resolve : historyWrite, output);
                        }
                        Fence(output, fence); timer.Stop();
                        if (round >= 0) (old ? result.baselineMs : result.candidateMs)[round] = timer.Elapsed.TotalMilliseconds / result.framesPerBatch;
                    }
                }
                string folder = Path.GetFullPath("Artifacts/quality-gpu-cost"); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, stage == "initial" ? "cost.json" : stage + "-cost.json"), JsonUtility.ToJson(result, true));
                TestContext.WriteLine(JsonUtility.ToJson(result, true));
                Assert.That(result.baselineMs, Has.All.GreaterThan(0));
                Assert.That(result.candidateMs, Has.All.GreaterThan(0));
            } finally {
                RenderTexture.active = priorActive; Shader.SetGlobalVector("_ZBufferParams", priorZ);
                foreach (Object item in new Object[] { baseline, candidate, source, history, historyWrite, resolve,
                    output, depth, depthHistory, depthWrite, motion, pattern, fence }) Object.DestroyImmediate(item);
            }
        }
        private static RenderTexture Target(int w, int h, RenderTextureFormat format) {
            var t = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear) {
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            t.Create(); return t;
        }
        private static void Clear(RenderTexture target, Color value) { RenderTexture.active = target; GL.Clear(false, true, value); }
        private static void Fence(RenderTexture target, Texture2D pixel) {
            RenderTexture.active = target; pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false); pixel.Apply();
        }
    }
}
