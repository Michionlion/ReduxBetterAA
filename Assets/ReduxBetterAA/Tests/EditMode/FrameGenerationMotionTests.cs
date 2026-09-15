using System.Reflection;
using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEditor;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationMotionTests
    {
        private const int Width = 512, Height = 256;

        [TestCase(false, 1, 1)]
        [TestCase(true, -1, 1)]
        [TestCase(false, 1, -1)]
        [TestCase(true, -1, -1)]
        public void SnapshotProtectionRepairsRadialMotionWithExactSignsJitterAndProjection(bool renderTexture, int signX, int signY)
        {
            VerifySnapshot(renderTexture, new Vector2(signX, signY), true, false);
        }

        [TestCase(false, -1, 1)]
        [TestCase(true, 1, -1)]
        public void SnapshotProtectionPreservesPlausibleObjectMotionWithoutChangingAaBypass(bool renderTexture, int signX, int signY)
        {
            VerifySnapshot(renderTexture, new Vector2(signX, signY), false, false);
        }

        [Test]
        public void ResetSnapshotCannotReusePreviousMotionOrPrivateCameraHistory()
        {
            VerifySnapshot(true, new Vector2(-1, -1), true, true);
        }

        private static void VerifySnapshot(bool renderTexture, Vector2 signs, bool corrupt, bool reset)
        {
            var go = new GameObject("FG snapshot motion test");
            var camera = go.AddComponent<Camera>(); camera.enabled = false;
            camera.nearClipPlane = .3f; camera.farClipPlane = 100; camera.aspect = (float)Width / Height;
            var color = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBHalf); color.Create();
            if (renderTexture) camera.targetTexture = color;
            var depth = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            var motion = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/MotionVectorSanitizer.shader");
            var material = new Material(shader);
            // An FG filter receives already-repaired producer motion. Even a
            // reused material carrying terrain uniforms must not apply it twice.
            material.SetFloat("_TerrainMotionValid", 1);
            material.SetTexture("_TerrainDepthTexture", depth);
            material.SetMatrix("_TerrainPreviousWorldFromCurrent", Matrix4x4.Translate(new Vector3(20, 30, 20)));
            var sanitizer = new MotionVectorSanitizer(null, null);
            Set(sanitizer, "_shader", shader); Set(sanitizer, "_material", material);
            // Poison the unrelated instance history. Explicit snapshot dispatch
            // must use request matrices even when its own AA history is invalid.
            Set(sanitizer, "_previousViewProjection", Matrix4x4.zero);
            Set(sanitizer, "_currentInverseViewProjection", Matrix4x4.zero);
            var jitter = new Vector2(.375f, -.21875f);
            var view = new ResolvedFrameCamera(camera, camera.projectionMatrix, jitter, 1);
            var current = FrameGenerationNative.Camera.NativeClip(view.ViewProjection, renderTexture);
            var previous = current * Matrix4x4.TRS(new Vector3(.015f, -.012f, .08f), Quaternion.Euler(.5f, 1, .2f), Vector3.one);
            var requestPrevious = FrameGenerationNative.Camera.NativeClip(previous, renderTexture);
            var token = new ResolvedFrameToken(1, 1, 1, 100, 1);
            SceneOutputFrame noSr = default;
            var request = new ResolvedFrameRequest(in token, BackendSelection.AmdFsr2, in view, requestPrevious,
                color.descriptor, Width, Height, in noSr, reset ? HistoryResetReason.FirstFrame : HistoryResetReason.None,
                reset, false, renderTexture, 1, reset ? 0 : 16, signs);
            var expected = new Vector2[Width * Height];
            var raw = new Color[expected.Length]; var z = new Color[expected.Length];
            var inverse = current.inverse;
            for (int y = 0; y < Height; ++y) for (int x = 0; x < Width; ++x)
            {
                int index = y * Width + x;
                // Asymmetric depth and jitter make fallback a spatial result,
                // rather than a constant that could hide an axis/projection bug.
                float rawDepth = .08f + .2f * ((float)x / Width) + .12f * ((float)y / Height);
                int depthIndex = renderTexture ? (Height - 1 - y) * Width + x : index;
                z[depthIndex] = new Color(rawDepth, 0, 0, 1);
                var uv = new Vector2((x + .5f + jitter.x) / Width, (y + .5f + jitter.y) / Height);
                var world = inverse * new Vector4(uv.x * 2 - 1, uv.y * 2 - 1, rawDepth, 1);
                var prior = previous * world;
                var cameraMotion = uv - new Vector2(prior.x / prior.w * .5f + .5f, prior.y / prior.w * .5f + .5f);
                var objectMotion = cameraMotion + new Vector2(12f / Width, -7f / Height);
                var input = corrupt ? new Vector2((x + .5f) / Width - .5f, (y + .5f) / Height - .5f) : objectMotion;
                raw[index] = new Color(input.x * signs.x, input.y * signs.y, 0, 1);
                var result = reset ? Vector2.zero : corrupt ? cameraMotion : objectMotion;
                expected[index] = Vector2.Scale(result, signs);
            }
            depth.SetPixels(z); depth.Apply(); motion.SetPixels(raw); motion.Apply();
            try
            {
                Assert.That(MotionVectorSanitizer.DefaultEnabled, Is.False);
                Assert.That(sanitizer.Enabled, Is.False);
                var frame = new BorrowedResolvedFrame(in request, color, depth, motion);
                Assert.That(sanitizer.TrySanitizeFrame(in frame, depth, renderTexture, out Texture result), Is.True);
                var actual = Read((RenderTexture)result);
                float maximumPixels = 0;
                for (int y = 1; y < Height - 1; y += 7) for (int x = 1; x < Width - 1; x += 7)
                {
                    int index = y * Width + x;
                    maximumPixels = Mathf.Max(maximumPixels, Mathf.Abs(actual[index].r - expected[index].x) * Width,
                        Mathf.Abs(actual[index].g - expected[index].y) * Height);
                }
                Assert.That(maximumPixels, Is.LessThan(.025f), "Stored signs or snapshot depth/projection/jitter are inconsistent");
                Assert.That(sanitizer.Enabled, Is.False, "FG must not change the AA diagnostic preference");
                Assert.That(Get<Matrix4x4>(sanitizer, "_previousViewProjection"), Is.EqualTo(Matrix4x4.zero), "FG must not advance AA camera history");
                // An ordinary AA dispatch on this same test instance must reset
                // the new input-sign/reset uniforms and preserve its bypass.
                Assert.That(sanitizer.TrySanitize(motion, depth, Width, Height, Vector2.zero, false, false, out Texture bypass), Is.True);
                var unchanged = Read((RenderTexture)bypass);
                Assert.That(unchanged[Width + 1].r, Is.EqualTo(raw[Width + 1].r).Within(.0003f));
                Assert.That(unchanged[Width + 1].g, Is.EqualTo(raw[Width + 1].g).Within(.0003f));
            }
            finally
            {
                // The production helper defers Destroy for in-player GPU
                // resources. This synchronous-readback EditMode fixture owns
                // them outright and must use immediate editor destruction.
                Object.DestroyImmediate(Get<RenderTexture>(sanitizer, "_sanitizedMotion"));
                Object.DestroyImmediate(Get<RenderTexture>(sanitizer, "_frameCorruption"));
                Set(sanitizer, "_sanitizedMotion", null); Set(sanitizer, "_frameCorruption", null);
                sanitizer.ReleaseResources();
                Object.DestroyImmediate(material); Object.DestroyImmediate(motion); Object.DestroyImmediate(depth);
                camera.targetTexture = null; Object.DestroyImmediate(color); Object.DestroyImmediate(go);
            }
        }

        private static void Set(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static T Get<T>(object target, string name) => (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static Color[] Read(RenderTexture source)
        {
            var old = RenderTexture.active;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = source; texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); texture.Apply();
                return texture.GetPixels();
            }
            finally { RenderTexture.active = old; Object.DestroyImmediate(texture); }
        }
    }
}
