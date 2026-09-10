using System.Reflection;
using KSP.Rendering;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Tests
{
    public sealed class OwnershipTests
    {
        [Test]
        public void CameraCleanupPreservesAnExternalOwnersChanges()
        {
            var go = new GameObject("external-camera-owner");
            var camera = go.AddComponent<Camera>();
            var layer = go.AddComponent<PostProcessLayer>();
            var claim = new SceneCameraState();
            var projection = new CameraProjectionState();
            try
            {
                claim.Capture(camera, layer);
                layer.antialiasingMode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
                camera.depthTextureMode |= DepthTextureMode.DepthNormals;
                projection.Apply(camera, new Vector2(.2f, .1f));
                Matrix4x4 external = Matrix4x4.Perspective(72, 1.2f, .1f, 8000);
                camera.projectionMatrix = external;
                projection.Restore();
                claim.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(external));
                Assert.That(layer.antialiasingMode, Is.EqualTo(PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing));
                Assert.That((camera.depthTextureMode & DepthTextureMode.DepthNormals) != 0, Is.True);
                claim.Restore(); projection.Restore();
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SpatialCleanupPreservesReplacedSettingsAndDoesNotAllocateOtherModes()
        {
            var go = new GameObject("spatial-owner");
            var camera = go.AddComponent<Camera>();
            var layer = go.AddComponent<PostProcessLayer>();
            layer.subpixelMorphologicalAntialiasing = null;
            var backend = new Ppv2SpatialAaBackend("FXAA", PostProcessLayer.Antialiasing.FastApproximateAntialiasing, true);
            try
            {
                Assert.That(backend.Configure(new TemporalCameraSet { SceneKind = TemporalSceneKind.Flight,
                    ResolveCamera = camera, ResolveLayer = layer }, out _), Is.True);
                Assert.That(layer.subpixelMorphologicalAntialiasing, Is.Null);
                var external = new FastApproximateAntialiasing { fastMode = false };
                layer.fastApproximateAntialiasing = external;
                layer.antialiasingMode = PostProcessLayer.Antialiasing.TemporalAntialiasing;
                backend.Deactivate();
                Assert.That(layer.fastApproximateAntialiasing, Is.SameAs(external));
                Assert.That(layer.antialiasingMode, Is.EqualTo(PostProcessLayer.Antialiasing.TemporalAntialiasing));
            }
            finally { backend.Dispose(); Object.DestroyImmediate(go); }
        }

        [Test]
        public void ScaleOwnershipRestoresNativeAndOriginalWithoutFightingExternalWrites()
        {
            var go = new GameObject("scale-owner");
            var presenter = go.AddComponent<RenderScalePresenter>();
            var owner = new RenderScaleOwnership();
            var field = typeof(RenderScalePresenter).GetField("_renderScalePercent", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                presenter.SetRenderScalePercent(175);
                Assert.That(owner.Apply(150), Is.True);
                Assert.That((int)field.GetValue(presenter), Is.EqualTo(150));
                owner.Apply(100);
                Assert.That((int)field.GetValue(presenter), Is.EqualTo(100));
                owner.Dispose(); owner.Dispose();
                Assert.That((int)field.GetValue(presenter), Is.EqualTo(175));
                owner.Apply(150);
                presenter.SetRenderScalePercent(125);
                Assert.That(owner.Apply(100), Is.False);
                Assert.That((int)field.GetValue(presenter), Is.EqualTo(125));
                owner.Dispose();
                Assert.That((int)field.GetValue(presenter), Is.EqualTo(125));
            }
            finally { owner.Dispose(); Object.DestroyImmediate(go); }
        }

        [TestCase(400, 1920, 1080, 16384, 200)]
        [TestCase(200, 7680, 4320, 8192, 106)]
        [TestCase(50, 1920, 1080, 16384, 100)]
        public void SupersamplingRespectsReduxAndDeviceLimits(int requested, int width, int height, int limit, int expected)
        {
            Assert.That(RenderScaleOwnership.ClampPercent(requested, width, height, limit), Is.EqualTo(expected));
            Assert.That(UserSettingsPolicy.ParseBackend("Supersampling", false, false), Is.EqualTo(BackendSelection.Supersampling));
        }
    }
}
