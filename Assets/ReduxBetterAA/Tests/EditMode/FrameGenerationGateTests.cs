using System;
using NUnit.Framework;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationGateTests
    {
        private static SceneOutputFrame Token(int owner = 1, int frame = 77, int generation = 4) =>
            new SceneOutputFrame(owner, 22, 3, frame, generation, 8, 4, 16, 8);

        private static FrameGenerationCapabilities Capabilities(bool renderInputs = true, bool close = true) =>
            new FrameGenerationCapabilities("Test provider", "fixture", true, GraphicsDeviceType.Direct3D11,
                FrameGenerationColorDomain.DisplayLinear, FrameGenerationMotionUnits.NormalizedUv,
                (1u << 2) | (1u << 4), renderInputs, close);

        [Test]
        public void NoProviderIsAvailableByDefault()
        {
            FrameGenerationCapabilities caps = default;
            FrameGenerationFrame frame = default;
            var token = Token();
            Assert.That(FrameGenerationGate.TryValidate(in frame, in token, in caps,
                GraphicsDeviceType.Direct3D11, 2, out string reason), Is.False);
            Assert.That(reason, Does.Contain("No frame-generation provider"));
        }

        [TestCase(GraphicsDeviceType.Direct3D12, 2)]
        [TestCase(GraphicsDeviceType.Null, 2)]
        [TestCase(GraphicsDeviceType.Direct3D11, 1)]
        [TestCase(GraphicsDeviceType.Direct3D11, 3)]
        [TestCase(GraphicsDeviceType.Direct3D11, 32)]
        public void ApiAndActualMultiplierSupportAreIndependent(GraphicsDeviceType api, int multiplier)
        {
            var caps = Capabilities();
            FrameGenerationFrame frame = default;
            var token = Token();
            Assert.That(FrameGenerationGate.TryValidate(in frame, in token, in caps,
                api, multiplier, out string reason), Is.False);
            Assert.That(reason, Does.Contain("graphics API or multiplier"));
        }

        [Test]
        public void CompleteRealFrameCanPassInputEligibilityWithoutClaimingProviderExecution()
        {
            using (var fixture = new Fixture())
                AssertAccepted(fixture.Frame());
        }

        [Test]
        public void ForeignOwnerOldFrameAndOldGraphAreRejected()
        {
            using (var fixture = new Fixture())
            {
                var frame = fixture.Frame();
                var caps = Capabilities();
                foreach (var expected in new[] { Token(owner: 2), Token(frame: 78), Token(generation: 5) })
                {
                    Assert.That(FrameGenerationGate.TryValidate(in frame, in expected, in caps,
                        GraphicsDeviceType.Direct3D11, 2, out string reason), Is.False);
                    Assert.That(reason, Does.Contain("scene-output token"));
                }
            }
        }

        [Test]
        public void ACurrentColorCannotLegitimizePreviousFrameMotion()
        {
            using (var fixture = new Fixture())
            {
                var previous = Token(frame: 76);
                var staleMotion = new FrameGenerationBuffer(fixture.Motion, in previous);
                AssertRejected(fixture.Frame(motion: staleMotion), "same frame token");
            }
        }

        [Test]
        public void LostGpuStorageInvalidatesAnOtherwiseMatchingSnapshot()
        {
            using (var fixture = new Fixture())
            {
                var frame = fixture.Frame();
                fixture.Motion.Release();
                AssertRejected(frame, "current storage");
            }
        }

        [Test]
        public void DisplayOnlyProviderRejectsRenderSizedAuxiliaryBuffers()
        {
            using (var fixture = new Fixture())
            {
                var frame = fixture.Frame();
                var caps = Capabilities(renderInputs: false);
                var expected = Token();
                Assert.That(FrameGenerationGate.TryValidate(in frame, in expected, in caps,
                    GraphicsDeviceType.Direct3D11, 2, out string reason), Is.False);
                Assert.That(reason, Does.Contain("input resolution"));
            }
        }

        [Test]
        public void MismatchedDepthAndMotionResolutionsAreRejected()
        {
            using (var fixture = new Fixture())
            {
                var token = Token();
                var wrongDepth = new FrameGenerationBuffer(fixture.Scene, in token);
                AssertRejected(fixture.Frame(depth: wrongDepth), "input resolution");
            }
        }

        [Test]
        public void HudContaminationAndUnseparableUiAreRejected()
        {
            using (var fixture = new Fixture())
            {
                AssertRejected(fixture.Frame(composition: Composition(hudless: false)), "separate current");
                AssertRejected(fixture.Frame(composition: Composition(separate: false)), "separate current");
                AssertRejected(fixture.Frame(composition: Composition(premultiplied: false)), "separate current");
                var token = Token();
                var sameSurface = new FrameGenerationBuffer(fixture.Scene, in token);
                AssertRejected(fixture.Frame(ui: sameSurface), "separate current");
                var noAlpha = new FrameGenerationBuffer(fixture.Coverage, in token);
                AssertRejected(fixture.Frame(ui: noAlpha), "separate current");
            }
        }

        [Test]
        public void NativeCloseGeometryNeedsCompleteDisplayDepthMotionAndCoverage()
        {
            using (var low = new Fixture())
                AssertRejected(low.Frame(composition: Composition(close: true)), "Native close-vessel");
            using (var display = new Fixture(displayInputs: true))
            {
                AssertAccepted(display.Frame(composition: Composition(close: true)));
                AssertRejected(display.Frame(composition: Composition(close: true, closeDepth: false)), "Native close-vessel");
                AssertRejected(display.Frame(composition: Composition(close: true, closeMotion: false)), "Native close-vessel");
                AssertRejected(display.Frame(composition: Composition(close: true, closeCoverage: false)), "Native close-vessel");
                AssertRejected(display.Frame(composition: Composition(close: true, closeColor: false)), "Native close-vessel");
                AssertRejected(display.Frame(composition: Composition(close: true), coverage: default(FrameGenerationBuffer)), "Native close-vessel");
            }
        }

        [Test]
        public void ProducerCompletionAndRetentionAreRequiredAndCutsWarmUp()
        {
            using (var fixture = new Fixture())
            {
                AssertRejected(fixture.Frame(retained: false), "GPU lifetime");
                AssertRejected(fixture.Frame(complete: false), "GPU lifetime");
                AssertRejected(fixture.Frame(reset: true), "warm up");
            }
        }

        [Test]
        public void InvalidExposureJitterAndUndeclaredColorDomainCannotDispatch()
        {
            using (var fixture = new Fixture())
            {
                AssertRejected(fixture.Frame(view: View(exposure: float.NaN)), "conventions");
                AssertRejected(fixture.Frame(view: View(preExposure: 0)), "conventions");
                AssertRejected(fixture.Frame(view: View(jitterX: float.PositiveInfinity)), "conventions");
                AssertRejected(fixture.Frame(view: View(color: FrameGenerationColorDomain.SceneLinearHdr)), "conventions");
            }
        }

        private static FrameGenerationComposition Composition(bool close = false, bool closeDepth = true,
            bool closeMotion = true, bool closeCoverage = true, bool closeColor = true,
            bool hudless = true, bool separate = true, bool premultiplied = true) =>
            new FrameGenerationComposition(hudless, separate, premultiplied, true, true,
                close, closeColor, closeDepth, closeMotion, closeCoverage);

        private static FrameGenerationView View(float exposure = 1f, float preExposure = 1f,
            float jitterX = .25f, FrameGenerationColorDomain color = FrameGenerationColorDomain.DisplayLinear) =>
            new FrameGenerationView(color, FrameGenerationMotionUnits.NormalizedUv,
                new Vector2(jitterX, -.25f), new Vector2(-1, 1), preExposure, exposure,
                16.67f, .2f, 100000f, true, Matrix4x4.identity, Matrix4x4.identity);

        private static void AssertAccepted(FrameGenerationFrame frame)
        {
            var caps = Capabilities();
            var expected = Token();
            Assert.That(FrameGenerationGate.TryValidate(in frame, in expected, in caps,
                GraphicsDeviceType.Direct3D11, 2, out string reason), Is.True, reason);
        }

        private static void AssertRejected(FrameGenerationFrame frame, string fragment)
        {
            var caps = Capabilities();
            var expected = Token();
            Assert.That(FrameGenerationGate.TryValidate(in frame, in expected, in caps,
                GraphicsDeviceType.Direct3D11, 2, out string reason), Is.False);
            Assert.That(reason, Does.Contain(fragment));
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly RenderTexture Scene, Depth, Motion, Ui, Coverage;

            internal Fixture(bool displayInputs = false)
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    Assert.Ignore("Texture lifetime validation requires a Unity graphics device");
                Scene = Create(16, 8, RenderTextureFormat.ARGBHalf);
                Depth = Create(displayInputs ? 16 : 8, displayInputs ? 8 : 4, RenderTextureFormat.RFloat);
                Motion = Create(displayInputs ? 16 : 8, displayInputs ? 8 : 4, RenderTextureFormat.RGFloat);
                Ui = Create(16, 8, RenderTextureFormat.ARGB32);
                Coverage = Create(16, 8, RenderTextureFormat.R8);
            }

            internal FrameGenerationFrame Frame(FrameGenerationComposition? composition = null,
                FrameGenerationView? view = null, FrameGenerationBuffer? depth = null,
                FrameGenerationBuffer? motion = null, FrameGenerationBuffer? ui = null,
                FrameGenerationBuffer? coverage = null, bool reset = false,
                bool retained = true, bool complete = true)
            {
                var token = Token();
                var scene = new FrameGenerationBuffer(Scene, in token);
                var frameDepth = depth ?? new FrameGenerationBuffer(Depth, in token);
                var frameMotion = motion ?? new FrameGenerationBuffer(Motion, in token);
                var frameUi = ui ?? new FrameGenerationBuffer(Ui, in token);
                var frameCoverage = coverage ?? new FrameGenerationBuffer(Coverage, in token);
                var frameView = view ?? View();
                var frameComposition = composition ?? Composition();
                return new FrameGenerationFrame(in token, in scene, in frameDepth, in frameMotion,
                    in frameUi, in frameCoverage, in frameView, in frameComposition, reset, retained, complete);
            }

            private static RenderTexture Create(int width, int height, RenderTextureFormat format)
            {
                var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
                Assert.That(texture.Create(), Is.True, "Fixture texture allocation");
                return texture;
            }

            public void Dispose()
            {
                foreach (var texture in new[] { Scene, Depth, Motion, Ui, Coverage })
                    if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
            }
        }
    }
}
