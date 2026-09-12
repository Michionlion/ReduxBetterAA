using System;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Tests
{
    public sealed class MaintenanceTests
    {
        [Test]
        public void CameraClaimRestoresStateBeforeRebindingAndToleratesDestroyedCamera()
        {
            var first = new GameObject("first");
            var second = new GameObject("second");
            var camera = first.AddComponent<Camera>();
            var layer = Ppv2TestLayer.Create(first);
            var other = second.AddComponent<Camera>();
            camera.depthTextureMode = DepthTextureMode.DepthNormals;
            layer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
            var state = new SceneCameraState();
            try
            {
                state.Capture(camera, layer);
                Assert.That(camera.depthTextureMode, Is.EqualTo(DepthTextureMode.DepthNormals |
                    DepthTextureMode.Depth | DepthTextureMode.MotionVectors));
                Assert.That(layer.antialiasingMode, Is.EqualTo(PostProcessLayer.Antialiasing.None));
                state.Capture(other, null);
                Assert.That(camera.depthTextureMode, Is.EqualTo(DepthTextureMode.DepthNormals));
                Assert.That(layer.antialiasingMode, Is.EqualTo(PostProcessLayer.Antialiasing.FastApproximateAntialiasing));
                UnityEngine.Object.DestroyImmediate(second);
                Assert.DoesNotThrow(() => { state.Restore(); state.Restore(); });
            }
            finally
            {
                state.Restore();
                UnityEngine.Object.DestroyImmediate(first);
                if (second != null) UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void TextureValidationRejectsLostWrongSizedAndArrayResources()
        {
            var texture = new RenderTexture(8, 8, 0);
            try
            {
                Assert.That(TemporalTextures.Matches(texture, 8, 8), Is.False);
                texture.Create();
                Assert.That(TemporalTextures.Matches(texture, 8, 8), Is.True);
                Assert.That(TemporalTextures.Matches(texture, 9, 8), Is.False);
                texture.Release();
                Assert.That(TemporalTextures.Matches(texture, 8, 8), Is.False);
                texture.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
                texture.volumeDepth = 2;
                texture.Create();
                Assert.That(TemporalTextures.Matches(texture, 8, 8), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void VendorOutputsDoNotInheritMemorylessStorage()
        {
            var source = new RenderTextureDescriptor(8, 8, RenderTextureFormat.ARGB32, 24)
            {
                memoryless = RenderTextureMemoryless.Color,
                useDynamicScale = true,
                msaaSamples = 4
            };
            var output = TemporalTextures.VendorOutputDescriptor(source);
            Assert.That(output.memoryless, Is.EqualTo(RenderTextureMemoryless.None));
            Assert.That(output.useDynamicScale, Is.False);
            Assert.That(output.enableRandomWrite, Is.True);
            Assert.That(output.msaaSamples, Is.EqualTo(1));
            Assert.That(output.depthBufferBits, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedConfigurationReleasesPartialOwnership(bool throws)
        {
            var backend = new FailedBackend { Throws = throws };
            Assert.That(TemporalCoordinator.TryConfigure(backend, null, out string reason), Is.False);
            Assert.That(backend.Active, Is.False);
            Assert.That(backend.Deactivations, Is.EqualTo(1));
            Assert.That(reason, Is.Not.Empty);
        }

        [Test]
        public void RegistryPreservesEveryBackendAndUnknownSelectionFallsBackToOff()
        {
            using (var coordinator = new TemporalCoordinator(null))
            {
                // Legacy numeric mode 4 resolves to the same Custom TAA instance as 5.
                string[] names = { "Off", "FXAA Low", "FXAA High", "SMAA", "Custom TAA",
                    "Custom TAA", "NVIDIA DLAA", "FSR2 Native AA", "Supersampling" };
                for (int index = 0; index < names.Length; index++)
                    Assert.That(coordinator.GetBackend((BackendSelection)index).Id, Is.EqualTo(names[index]));
                Assert.That(coordinator.GetBackend((BackendSelection)4),
                    Is.SameAs(coordinator.GetBackend(BackendSelection.CustomTaa)));
                Assert.That(coordinator.GetBackend((BackendSelection)999).Id, Is.EqualTo("Off"));
                Assert.That(coordinator.GetBackend((BackendSelection)(-1)).Id, Is.EqualTo("Off"));
                coordinator.SetRequestedBackend((BackendSelection)4);
                Assert.That(coordinator.RequestedBackend, Is.EqualTo(BackendSelection.CustomTaa));
                coordinator.SetRequestedBackend((BackendSelection)999);
                Assert.That(coordinator.RequestedBackend, Is.EqualTo(BackendSelection.Off));
            }
        }

        [Test]
        public void ResolveObserverReceivesTrueInputOnceAndInactiveHookPassesThrough()
        {
            var obj = new GameObject("resolve-hook-test");
            var hook = obj.AddComponent<TemporalRenderHook>();
            var source = new RenderTexture(8, 8, 0);
            var output = new RenderTexture(8, 8, 0);
            var pixel = new Texture2D(1, 1);
            RenderTexture previous = RenderTexture.active;
            try
            {
                source.Create(); output.Create();
                RenderTexture.active = source;
                GL.Clear(false, true, Color.red);
                var resolver = new GreenResolve();
                hook.Owner = resolver;
                int captures = 0;
                hook.CaptureInput = input =>
                {
                    captures++;
                    Assert.That(resolver.Renders, Is.Zero, "Input must precede execution");
                    Assert.That(ReadPixel(input, pixel).r, Is.GreaterThan(0.99f));
                };
                hook.Render(source, output);
                Assert.That(captures, Is.EqualTo(1));
                Assert.That(ReadPixel(output, pixel).g, Is.GreaterThan(0.99f));
                hook.Render(source, output);
                Assert.That(captures, Is.EqualTo(1));
                Assert.That(resolver.Renders, Is.EqualTo(2));
                hook.Owner = null;
                hook.Render(source, output);
                Assert.That(ReadPixel(output, pixel).r, Is.GreaterThan(0.99f));
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(pixel);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(output);
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        private static Color ReadPixel(RenderTexture texture, Texture2D pixel)
        {
            RenderTexture.active = texture;
            pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            return pixel.GetPixel(0, 0);
        }

        private sealed class GreenResolve : ISceneResolve
        {
            public bool Active => true;
            public int Renders;
            public void Render(RenderTexture source, RenderTexture destination)
            {
                Renders++;
                RenderTexture.active = destination;
                GL.Clear(false, true, Color.green);
            }
        }

        private sealed class FailedBackend : ITemporalBackend
        {
            public bool Throws;
            public int Deactivations;
            public string Id => "Failure fixture";
            public bool Active { get; private set; }
            public bool ProbeSupport(TemporalCameraSet cameras, out string reason) { reason = ""; return true; }
            public bool Configure(TemporalCameraSet cameras, out string reason)
            {
                Active = true;
                reason = "Partial setup";
                if (Throws) throw new InvalidOperationException();
                return false;
            }
            public void Tick(uint frame) { }
            public void ResetHistory(HistoryResetReason reason) { }
            public void Deactivate() { Active = false; Deactivations++; }
            public void Dispose() => Deactivate();
        }
    }
}
