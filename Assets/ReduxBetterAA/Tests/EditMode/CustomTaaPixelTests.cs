using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReduxBetterAA.Tests
{
    public sealed class CustomTaaPixelTests
    {
        private const int Size = 32;
        private Material _candidate, _baseline;
        private Texture2D _source, _history, _depth, _historyDepth, _motion, _readback;
        private RenderTexture _output;
        private Vector4 _priorZ;

        [SetUp] public void Setup()
        {
            _candidate = Material("Assets/ReduxBetterAA/Shaders/CustomTaa.shader");
            _baseline = Material("Assets/ReduxBetterAA/Tests/EditMode/Fixtures/CustomTaaBaseline.shader");
            _source = Texture((x, y) => new Color(.4f, .5f, .6f, .2f));
            _history = Texture((x, y) => new Color(.6f, .4f, .5f, 1));
            _depth = Texture((x, y) => Color.white);
            _historyDepth = Texture((x, y) => new Color(.5f, 0, 0, 0));
            _motion = Texture((x, y) => Color.clear);
            _readback = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
            _output = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _output.Create();
            _priorZ = Shader.GetGlobalVector("_ZBufferParams");
            // Controlled reversed-Z fixture: raw 1 -> .5 linear, raw 0 -> sky 1.
            Shader.SetGlobalVector("_ZBufferParams", new Vector4(1, 1, 0, 0));
            foreach (Material m in new[] { _candidate, _baseline }) {
                m.SetTexture("_HistoryTex", _history); m.SetTexture("_CameraDepthTexture", _depth);
                m.SetTexture("_HistoryDepthTex", _historyDepth); m.SetTexture("_ReduxBetterAAMotionVectors", _motion);
                m.SetVector("_SourceDimensions", new Vector4(Size, Size, 1f / Size, 1f / Size));
                m.SetFloat("_HistoryValid", 1); m.SetFloat("_StationaryHistory", .99f);
                m.SetFloat("_MovingHistory", .1f); m.SetFloat("_MotionResponsePixels", 8);
                m.SetFloat("_MaximumMotionPixels", 256); m.SetFloat("_DepthThreshold", .01f);
                m.SetFloat("_DepthEdgeStability", 1); m.SetFloat("_VarianceGamma", 1.25f);
                m.SetFloat("_ReactiveScale", 0); m.SetFloat("_NoDepthHistory", .25f);
            }
        }

        [TearDown] public void Cleanup()
        {
            Shader.SetGlobalVector("_ZBufferParams", _priorZ);
            foreach (Object o in new Object[] { _candidate, _baseline, _source, _history, _depth,
                _historyDepth, _motion, _readback, _output }) if (o != null) Object.DestroyImmediate(o);
        }

        [TestCase(0f, 0f)] [TestCase(.37f, -.29f)] [TestCase(1.7f, 2.4f)]
        public void NineTapHistoryMatchesFrozenSixteenTapIncludingBorders(float mx, float my)
        {
            _candidate.SetFloat("_HistoryFilterSharpness",.5f);
            Fill(_history, (x, y) => new Color((x * 13 + y * 7) % 23 / 7f,
                (x * 5 + y * 11) % 19 / 9f, (x + y * 3) % 17 / 6f, 1));
            Fill(_motion, (x, y) => new Color(mx / Size, my / Size, 0, 0));
            _candidate.SetFloat("_DebugMode", 3); _baseline.SetFloat("_DebugMode", 3);
            Color[] expected = Render(_baseline, 3), actual = Render(_candidate, 3);
            float maximum = MaxDifference(expected, actual);
            TestContext.WriteLine("Catmull-Rom max absolute channel error: " + maximum);
            // Hardware bilinear weights have finite subtexel precision (typically
            // 8 fractional bits). This HDR stress input reaches 3.5; allow <.008
            // absolute error, below one 8-bit step normalized to that range.
            Assert.That(maximum, Is.LessThan(.008f), "Includes clamped outer footprint and HDR samples");
        }

        [TestCase(false)] [TestCase(true)]
        public void ResetAndPoisonedHistoryProduceFiniteCurrentColor(bool reset)
        {
            Fill(_history, (x, y) => new Color(float.NaN, float.PositiveInfinity, float.NaN, float.NaN));
            _candidate.SetFloat("_HistoryValid", reset ? 0 : 1);
            Color[] actual = Render(_candidate, 0), current = _source.GetPixels();
            Assert.That(MaxDifference(actual, current), Is.LessThan(1e-5f));
        }

        [Test] public void MovingDepthEdgeDoesNotRestoreStationaryHistory()
        {
            Fill(_depth, (x, y) => x < 24 ? Color.white : Color.black);
            Fill(_motion, (x, y) => new Color(8f / Size, 0, 0, 0));
            _candidate.SetFloat("_DebugMode", 6); _baseline.SetFloat("_DebugMode", 6);
            float oldWeight = Render(_baseline, 3)[16 * Size + 23].r;
            float newWeight = Render(_candidate, 3)[16 * Size + 23].r;
            Assert.That(oldWeight, Is.GreaterThan(.9f), "Fixture reproduces the old moving-edge boost");
            Assert.That(newWeight, Is.EqualTo(.1f).Within(.001f));
        }

        [Test] public void DepthOutsideReconstructionFootprintCannotRescueDisocclusion()
        {
            Fill(_depth, (x, y) => x == 16 ? Color.white : Color.black);
            Fill(_historyDepth, (x, y) => new Color(x == 15 ? .5f : 1f, 0, 0, 0));
            _candidate.SetFloat("_DebugMode", 4); _baseline.SetFloat("_DebugMode", 4);
            float oldReject = Render(_baseline, 3)[16 * Size + 16].r;
            float newReject = Render(_candidate, 3)[16 * Size + 16].r;
            Assert.That(oldReject, Is.LessThan(.01f));
            Assert.That(newReject, Is.GreaterThan(.99f));
        }

        [Test] public void AlphaTracksCurrentCoverage()
        {
            foreach (Color pixel in Render(_candidate, 0)) Assert.That(pixel.a, Is.EqualTo(.2f).Within(1e-5));
        }

        [Test] public void SharpeningCannotInventBrightOrDarkHalos()
        {
            Fill(_source, (x, y) => x < 16 ? new Color(.2f, .2f, .2f, 1) : new Color(.8f, .8f, .8f, 1));
            _candidate.SetFloat("_Sharpening", 1); _baseline.SetFloat("_Sharpening", 1);
            Color[] old = Render(_baseline, 2), current = Render(_candidate, 2);
            Assert.That(old[16].r, Is.GreaterThan(.8f));
            foreach (Color p in current) Assert.That(p.r, Is.InRange(.19999f, .80001f));
        }

        [Test] public void StationaryAnalyticEdgeSequenceDoesNotRegressReferenceError()
        {
            // Pixel-box integral of a half-plane is an independent spatial reference.
            // This sequence includes fractional coverage and repeated subpixel jitter.
            Color[] reference = new Color[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) {
                double coverage = 0;
                const int samples = 32;
                for (int sy = 0; sy < samples; sy++) for (int sx = 0; sx < samples; sx++)
                    coverage += x + (sx + .5) / samples > 10.3 + .35 * (y + (sy + .5) / samples) ? 1 : 0;
                float v = .2f + .6f * (float)(coverage / (samples * samples));
                reference[y * Size + x] = new Color(v, v, v, 1);
            }
            double oldError = SequenceError(_baseline, reference), newError = SequenceError(_candidate, reference);
            TestContext.WriteLine("Analytic stationary edge RGB MSE baseline=" + oldError + " candidate=" + newError);
            Assert.That(newError, Is.LessThanOrEqualTo(oldError * 1.02 + 1e-7));
        }

        private double SequenceError(Material material, Color[] reference)
        {
            Color[] result = null;
            for (uint frame = 0; frame < 32; frame++) {
                Vector2 jitter = Rendering.SharedJitterSequence.GetCustomOffset(frame, .75f, 8);
                Fill(_source, (x, y) => {
                    float v = x + .5f + jitter.x > 10.3f + .35f * (y + .5f + jitter.y) ? .8f : .2f;
                    return new Color(v, v, v, 1);
                });
                material.SetVector("_Jitter", jitter / Size);
                material.SetFloat("_HistoryValid", frame == 0 ? 0 : 1);
                result = Render(material, 0);
                _history.SetPixels(result); _history.Apply();
            }
            double mse = 0;
            for (int i = 0; i < result.Length; i++) mse += Math.Pow(result[i].r - reference[i].r, 2);
            return mse / result.Length;
        }

        private Color[] Render(Material material, int pass)
        {
            RenderTexture prior = RenderTexture.active;
            try {
                Graphics.Blit(_source, _output, material, pass);
                RenderTexture.active = _output;
                _readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); _readback.Apply();
                return _readback.GetPixels();
            } finally { RenderTexture.active = prior; }
        }
        private static Material Material(string path)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null); Assert.That(shader.isSupported, Is.True);
            return new Material(shader);
        }
        private static Texture2D Texture(Func<int, int, Color> pixel)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true) {
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Fill(texture, pixel); return texture;
        }
        private static void Fill(Texture2D texture, Func<int, int, Color> pixel)
        {
            var values = new Color[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) values[y * Size + x] = pixel(x, y);
            texture.SetPixels(values); texture.Apply();
        }
        private static float MaxDifference(Color[] a, Color[] b)
        {
            float maximum = 0;
            for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++) {
                Assert.That(float.IsNaN(a[i][c]) || float.IsInfinity(a[i][c]), Is.False, "Finite output");
                maximum = Mathf.Max(maximum, Mathf.Abs(a[i][c] - b[i][c]));
            }
            return maximum;
        }
    }
}
