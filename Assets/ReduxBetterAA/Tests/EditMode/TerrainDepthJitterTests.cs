using System;
using System.Reflection;
using KSP.Rendering.Planets;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class TerrainDepthJitterTests
    {
        private sealed class Source : IProjectionJitterSource
        {
            internal Camera Camera;
            internal Matrix4x4 Projection;
            internal bool Enabled = true;
            public bool TryGetRasterProjection(Camera camera, out Matrix4x4 projection)
            { projection = Projection; return Enabled && camera == Camera; }
        }

        [Test]
        public void TerrainDepthDrawHasTheValidatedSignature()
        {
            var method = typeof(PQSRenderer).GetMethod("DrawPqsDepthNow",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Material), typeof(Camera) }, null);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(void)));
        }

        [Test]
        public void AuxiliaryDrawRestoresProjectionAfterFailureAndPreservesOtherCameraState()
        {
            var go = new GameObject("terrain-depth-camera");
            var camera = go.AddComponent<Camera>();
            var original = camera.projectionMatrix;
            var nonJittered = camera.nonJitteredProjectionMatrix;
            var transparent = camera.useJitteredProjectionMatrixForTransparentRendering;
            var raster = new CameraProjectionState().GetRasterProjection(camera, new Vector2(.3f, -.2f));
            var source = new Source { Camera = camera, Projection = raster };
            var scope = new AuxiliaryProjectionScope();
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                {
                    try { scope.Apply(camera, source); Assert.That(camera.projectionMatrix, Is.EqualTo(raster)); throw new InvalidOperationException(); }
                    finally { scope.Restore(); }
                });
                scope.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(original));
                Assert.That(camera.nonJitteredProjectionMatrix, Is.EqualTo(nonJittered));
                Assert.That(camera.useJitteredProjectionMatrixForTransparentRendering, Is.EqualTo(transparent));
                scope.Apply(camera, source);
                var external = original; external.m02 += .2f; camera.projectionMatrix = external;
                scope.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(external), "Preserve an external projection write");
            }
            finally { scope.Restore(); UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void AuxiliaryDrawDoesNotDoubleJitterOrAdvanceTheSequence()
        {
            var go = new GameObject("terrain-depth-nested");
            var camera = go.AddComponent<Camera>();
            var main = new CameraProjectionState();
            var scope = new AuxiliaryProjectionScope();
            try
            {
                var jitter = SharedJitterSequence.GetCustomOffset(17, .75f, 8);
                var expected = main.GetRasterProjection(camera, jitter);
                main.Apply(camera, jitter);
                Assert.That(main.GetRasterProjection(camera, jitter), Is.EqualTo(expected));
                scope.Apply(camera, new Source { Camera = camera, Projection = main.GetRasterProjection(camera, jitter) });
                scope.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(expected));
                Assert.That(SharedJitterSequence.GetCustomOffset(17, .75f, 8), Is.EqualTo(jitter));
            }
            finally { scope.Restore(); main.Restore(); UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void NativeSourcesRejectInactiveUnsupportedAndUnrelatedCameras(int mode)
        {
            var go = new GameObject("terrain-native-source");
            var other = new GameObject("unrelated-camera");
            var camera = go.AddComponent<Camera>();
            var unrelated = other.AddComponent<Camera>();
            ITemporalBackend backend = mode == 0
                ? (ITemporalBackend)new CustomTaaBackend(null, null, null, null, null)
                : mode == 1 ? (ITemporalBackend)new NvidiaDlaaBackend(null, null, null, null, null)
                : new AmdFsr2Backend(null, null, null, null, null);
            var source = (IProjectionJitterSource)backend;
            void Set(string name, object value) => backend.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(backend, value);
            try
            {
                Assert.That(source.TryGetRasterProjection(camera, out _), Is.False);
                Set("_active", true); Set("_resolveCamera", camera);
                Assert.That(source.TryGetRasterProjection(camera, out _), Is.False, "Map/unsupported projection policy");
                Set("_projectionJitterSupported", true); Set("_frameIndex", 17u);
                Assert.That(source.TryGetRasterProjection(unrelated, out _), Is.False);
                Assert.That(source.TryGetRasterProjection(null, out _), Is.False);
                Assert.That(source.TryGetRasterProjection(camera, out var first), Is.True);
                Assert.That(source.TryGetRasterProjection(camera, out var second), Is.True);
                Assert.That(first, Is.EqualTo(second));
                Assert.That(first, Is.Not.EqualTo(camera.projectionMatrix));
                Set("_active", false);
                Assert.That(source.TryGetRasterProjection(camera, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(other); }
        }

        [Test]
        public void AuxiliaryProjectionHasNoSteadyStateManagedAllocation()
        {
            var go = new GameObject("terrain-projection-allocation");
            var camera = go.AddComponent<Camera>();
            var source = new Source { Camera = camera, Projection = new CameraProjectionState().GetRasterProjection(camera, new Vector2(.2f,.3f)) };
            var scope = new AuxiliaryProjectionScope();
            try
            {
                for (int i=0;i<100;i++) { scope.Apply(camera, source); scope.Restore(); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i=0;i<1000;i++) { scope.Apply(camera, source); scope.Restore(); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(allocated, Is.Zero);
                source.Enabled = false;
                var projection = camera.projectionMatrix;
                scope.Apply(camera, source); scope.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
            }
            finally { scope.Restore(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
