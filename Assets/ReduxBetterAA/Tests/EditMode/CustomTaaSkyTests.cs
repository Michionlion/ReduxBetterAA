using System;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Rendering;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReduxBetterAA.Tests
{
    public sealed class CustomTaaSkyTests
    {
        private const int Size = 48;
        private Material _material;
        private Texture2D _source, _history, _depth, _historyDepth, _motion, _read;
        private RenderTexture _output;
        private Vector4 _previousZ;

        [SetUp]
        public void SetUp()
        {
            _material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/ReduxBetterAA/Shaders/CustomTaa.shader"));
            _source = Texture(); _history = Texture(); _depth = Texture();
            _historyDepth = Texture(); _motion = Texture(); _read = Texture();
            _output = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            _output.Create();
            _previousZ = Shader.GetGlobalVector("_ZBufferParams");
            Shader.SetGlobalVector("_ZBufferParams", new Vector4(1, 1, 0, 0));
            Fill(_source, (x, y) => Color.gray);
            Fill(_history, (x, y) => Color.gray);
            Fill(_depth, (x, y) => Color.clear);
            Fill(_historyDepth, (x, y) => Color.white);
            Fill(_motion, (x, y) => Color.clear);
            _material.SetTexture("_CameraDepthTexture", _depth);
            _material.SetTexture("_HistoryTex", _history);
            _material.SetTexture("_HistoryDepthTex", _historyDepth);
            _material.SetTexture("_ReduxBetterAAMotionVectors", _motion);
            _material.SetVector("_SourceDimensions", new Vector4(Size, Size, 1f / Size, 1f / Size));
            _material.SetFloat("_HistoryValid", 1);
            _material.SetFloat("_SkyHistoryValid", 1);
            _material.SetMatrix("_SkyReprojection", Matrix4x4.identity);
            _material.SetFloat("_StationaryHistory", .93f);
            _material.SetFloat("_MovingHistory", .1f);
            _material.SetFloat("_NoDepthHistory", .25f);
            _material.SetFloat("_MotionResponsePixels", 8);
            _material.SetFloat("_MaximumMotionPixels", 256);
            _material.SetFloat("_DepthThreshold", .01f);
            _material.SetFloat("_DepthEdgeStability", .75f);
            _material.SetFloat("_VarianceGamma", 1.25f);
            _material.SetFloat("_ReactiveScale", 2);
        }

        [TearDown]
        public void TearDown()
        {
            Shader.SetGlobalVector("_ZBufferParams", _previousZ);
            foreach (Object item in new Object[] { _material, _source, _history, _depth,
                _historyDepth, _motion, _read, _output }) Object.DestroyImmediate(item);
        }

        [Test]
        public void SkyReprojectionIgnoresTranslationAndTracksRotationAndFov()
        {
            var projection = Matrix4x4.Perspective(60, 1.7f, .1f, 10000);
            var rotation = Quaternion.Euler(12, 30, 5);
            var view = Matrix4x4.TRS(new Vector3(200, -30, 700), rotation, Vector3.one).inverse;
            var moved = Matrix4x4.TRS(new Vector3(-800, 4000, 50), rotation, Vector3.one).inverse;
            var current = CustomTaaBackend.RotationViewProjection(view, projection);
            var translated = CustomTaaBackend.RotationViewProjection(moved, projection);
            for (int i = 0; i < 16; i++)
                Assert.That(translated[i], Is.EqualTo(current[i]).Within(.00001f));
            var previousProjection = Matrix4x4.Perspective(65, 1.7f, .3f, 20000);
            var previousRotation = Quaternion.Euler(10, 28, 5);
            var previousView = Matrix4x4.Rotate(Quaternion.Inverse(previousRotation));
            var previous = CustomTaaBackend.RotationViewProjection(previousView, previousProjection);
            Vector4 clip = new Vector4(.2f, -.3f, 1, 1);
            Vector4 actual = previous * current.inverse * clip;
            // Independent camera-space ray: no finite distance or camera origin.
            Vector4 cameraRay = projection.inverse * clip;
            Vector3 worldRay = rotation * new Vector3(cameraRay.x, cameraRay.y, cameraRay.z);
            Vector3 oldRay = Quaternion.Inverse(previousRotation) * worldRay;
            Vector4 expected = previousProjection * new Vector4(oldRay.x, oldRay.y, oldRay.z, 0);
            Assert.That(actual.x / actual.w, Is.EqualTo(expected.x / expected.w).Within(.00001f));
            Assert.That(actual.y / actual.w, Is.EqualTo(expected.y / expected.w).Within(.00001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BackgroundUsesDirectionInsteadOfForegroundMotion(bool silhouette)
        {
            Fill(_history, (x, y) => new Color(x / (float)Size, y / (float)Size, 0, 1));
            Fill(_motion, (x, y) => new Color(10f / Size, 0, 0, 0));
            if (silhouette) Fill(_depth, (x, y) => x == 23 ? Color.white : Color.clear);
            var matrix = Matrix4x4.identity;
            matrix.m03 = -4f / Size; // The previous ray is two pixels to the left.
            _material.SetMatrix("_SkyReprojection", matrix);
            _material.SetFloat("_DebugMode", 3);
            Color sample = Render(3)[24 * Size + 24];
            Assert.That(sample.r, Is.EqualTo(22f / Size).Within(.001f));
            Assert.That(sample.g, Is.EqualTo(24f / Size).Within(.001f));
            _material.SetFloat("_ReactiveScale", 0);
            _material.SetFloat("_DebugMode", 6);
            Assert.That(Render(3)[24 * Size + 24].r, Is.LessThanOrEqualTo(.501f),
                "A foreground edge must not restore stationary sky history while panning");
        }

        [TestCase(0f)]
        [TestCase(1f)]
        public void UncoveredSkyRejectsForegroundHistory(float skyValid)
        {
            Fill(_historyDepth, (x, y) => new Color(.5f, 0, 0, 0));
            _material.SetFloat("_SkyHistoryValid", skyValid);
            _material.SetFloat("_DebugMode", 6);
            Assert.That(Render(3)[24 * Size + 24].r, Is.LessThan(.00001f));
        }

        [Test]
        public void DirectionBehindPreviousCameraRejectsHistory()
        {
            var matrix = Matrix4x4.identity;
            matrix.m33 = -1;
            _material.SetMatrix("_SkyReprojection", matrix);
            _material.SetFloat("_DebugMode", 6);
            Assert.That(Render(3)[24 * Size + 24].r, Is.LessThan(.00001f));
        }

        [Test]
        public void SkyStillRespondsToLightingChanges()
        {
            Fill(_source, (x, y) => new Color(.9f, .9f, .9f, 1));
            Fill(_history, (x, y) => new Color(.1f, .1f, .1f, 1));
            _material.SetFloat("_DebugMode", 6);
            Assert.That(Render(3)[24 * Size + 24].r, Is.LessThan(.00001f));
        }

        [Test]
        public void DisappearingLightClearsAgainstUniformBackground()
        {
            Fill(_source, (x, y) => new Color(.02f, .02f, .02f, 1));
            Fill(_history, (x, y) => new Color(.1f, .1f, .1f, 1));
            Assert.That(Render(0)[24 * Size + 24].r, Is.EqualTo(.02f).Within(.00001f));
        }

        [Test]
        public void UniformColorChangeDoesNotKeepOldChroma()
        {
            Fill(_source, (x, y) => new Color(.2f, .2f, .2f, 1));
            Fill(_history, (x, y) => new Color(.3f, .2f, .1f, 1));
            Color result = Render(0)[24 * Size + 24];
            Assert.That(result.r, Is.EqualTo(.2f).Within(.00001f));
            Assert.That(result.g, Is.EqualTo(.2f).Within(.00001f));
            Assert.That(result.b, Is.EqualTo(.2f).Within(.00001f));
        }

        [Test]
        public void StarPanReversalAndStopDoNotLeaveTrails()
        {
            Fill(_history, (x, y) => Color.clear);
            float previous = 16.3f;
            for (uint frame = 0; frame < 80; frame++)
            {
                float position = 16.3f + (frame < 16 ? 0 : frame < 32 ? (frame - 15) * .5f :
                    frame < 48 ? (47 - frame) * .5f : 0);
                Vector2 jitter = SharedJitterSequence.GetCustomOffset(frame, .75f, 8);
                Fill(_source, (x, y) => {
                    float dx = x + .5f + jitter.x - position, dy = y + .5f + jitter.y - 24.2f;
                    float value = Mathf.Exp(-(dx * dx + dy * dy) / (.38f * .38f * 2));
                    return new Color(value, value, value, 1);
                });
                var matrix = Matrix4x4.identity;
                matrix.m03 = -2f * (position - previous) / Size;
                _material.SetMatrix("_SkyReprojection", matrix);
                _material.SetVector("_Jitter", jitter / Size);
                _material.SetFloat("_HistoryValid", frame == 0 ? 0 : 1);
                Color[] result = Render(0);
                _history.SetPixels(result); _history.Apply();
                for (int x = 4; x < Size - 4; x++)
                    if (Mathf.Abs(x + .5f - position) > 3)
                        Assert.That(result[24 * Size + x].r, Is.LessThan(.005f),
                            "No stale star outside its current reconstruction footprint at frame " + frame);
                previous = position;
            }
        }

        [TestCase(0f)]
        [TestCase(.25f)]
        [TestCase(.75f)]
        public void JitteredStarsRetainEnergyWithLessTemporalError(float speed)
        {
            Vector3 fallback = StarSequence(speed, false);
            Vector3 sky = StarSequence(speed, true);
            TestContext.WriteLine("Stars speed=" + speed + " fallback MSE/variance/energy=" +
                fallback.ToString("G8") + " sky=" + sky.ToString("G8"));
            // A stationary point should settle; moving points must improve both
            // reference error and its variation without erasing their energy.
            Assert.That(sky.y, Is.LessThan(fallback.y * (speed == 0 ? .1f : .9f)), "Reduce temporal error");
            Assert.That(sky.x, Is.LessThan(fallback.x * .9f), "Improve spatial reconstruction");
            Assert.That(sky.z, Is.InRange(.8f, 1.1f), "Retain the integrated star energy");
        }

        private Vector3 StarSequence(float speed, bool sky)
        {
            _material.SetFloat("_SkyHistoryValid", sky ? 1 : 0);
            var matrix = Matrix4x4.identity;
            matrix.m03 = -2f * speed / Size;
            _material.SetMatrix("_SkyReprojection", matrix);
            Fill(_motion, (x, y) => new Color(speed / Size, 0, 0, 0));
            Fill(_history, (x, y) => Color.clear);
            double errorSum = 0, energy = 0, referenceEnergy = 0;
            var sums = new double[Size * Size];
            var squares = new double[Size * Size];
            const int measuredFrames = 32;
            for (uint frame = 0; frame < 96; frame++)
            {
                Vector2 jitter = SharedJitterSequence.GetCustomOffset(frame, .75f, 8);
                float shift = speed * frame;
                Fill(_source, (x, y) => {
                    float value = Star(x + .5f + jitter.x - shift, y + .5f + jitter.y);
                    return new Color(value, value, value, 1);
                });
                _material.SetVector("_Jitter", jitter / Size);
                _material.SetFloat("_HistoryValid", frame == 0 ? 0 : 1);
                Color[] result = Render(0);
                _history.SetPixels(result); _history.Apply();
                if (frame < 64) continue;
                for (int y = 4; y < Size - 4; y++) for (int x = 4; x < Size - 4; x++)
                {
                    double reference = 0;
                    for (int sy = 0; sy < 8; sy++) for (int sx = 0; sx < 8; sx++)
                        reference += Star(x + (sx + .5f) / 8 - shift, y + (sy + .5f) / 8) / 64;
                    int i = y * Size + x;
                    double error = result[i].r - reference;
                    errorSum += error * error; sums[i] += error; squares[i] += error * error;
                    energy += result[i].r; referenceEnergy += reference;
                }
            }
            double variance = 0;
            for (int i = 0; i < sums.Length; i++)
                variance += Math.Max(0, squares[i] / measuredFrames - Math.Pow(sums[i] / measuredFrames, 2));
            int pixels = (Size - 8) * (Size - 8);
            return new Vector3((float)(errorSum / measuredFrames / pixels),
                (float)(variance / pixels), (float)(energy / referenceEnergy));
        }

        private static float Star(float x, float y)
        {
            float dx = Mathf.Repeat(x - 3.23f, 11f) - 5.5f;
            float dy = Mathf.Repeat(y - 2.17f, 13f) - 6.5f;
            return Mathf.Exp(-(dx * dx + dy * dy) / (.38f * .38f * 2));
        }

        private static Texture2D Texture() => new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

        private static void Fill(Texture2D texture, Func<int, int, Color> pixel)
        {
            var values = new Color[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) values[y * Size + x] = pixel(x, y);
            texture.SetPixels(values); texture.Apply();
        }

        private Color[] Render(int pass)
        {
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(_source, _output, _material, pass);
                RenderTexture.active = _output;
                _read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); _read.Apply();
                return _read.GetPixels();
            }
            finally { RenderTexture.active = previous; }
        }
    }
}
