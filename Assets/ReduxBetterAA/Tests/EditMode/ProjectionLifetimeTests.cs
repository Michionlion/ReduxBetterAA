using System;
using System.Reflection;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReduxBetterAA.Tests
{
    public sealed class ProjectionLifetimeTests
    {
        private sealed class Source : IProjectionJitterSource
        {
            public bool TryGetRasterProjection(Camera camera, out Matrix4x4 projection)
            {
                projection = new CameraProjectionState().GetRasterProjection(camera, new Vector2(.25f, -.25f));
                return true;
            }
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
        public void AutomaticProjectionTracksParametersAfterAuxiliaryAndMainDraws(bool orthographic, bool physical)
        {
            var go = new GameObject("projection-lifetime");
            var camera = go.AddComponent<Camera>();
            var main = new CameraProjectionState();
            var auxiliary = new AuxiliaryProjectionScope();
            try
            {
                Assert.That(ProjectionOverride.Available, Is.True);
                camera.orthographic = orthographic;
                camera.usePhysicalProperties = physical;
                camera.nearClipPlane = .5f; camera.farClipPlane = 100000;
                auxiliary.Apply(camera, new Source()); auxiliary.Restore();
                camera.farClipPlane = 300000;
                AssertAutomatic(camera);
                var before = camera.projectionMatrix;
                main.Apply(camera, new Vector2(.25f, -.25f), jitterTransparentRendering: true);
                Assert.That(main.Projection, Is.EqualTo(before));
                Assert.That(camera.useJitteredProjectionMatrixForTransparentRendering, Is.True);
                auxiliary.Apply(camera, new Source()); auxiliary.Restore();
                // A camera update while owned must survive cleanup too.
                camera.farClipPlane = 600000;
                main.Restore(); main.Restore();
                AssertAutomatic(camera);
                // Off has no projection callback to repair a stale matrix.
                camera.nearClipPlane = 2; camera.farClipPlane = 900000;
                camera.fieldOfView = 75; camera.aspect = 1.4f; camera.orthographicSize = 20;
                if (physical) camera.lensShift = new Vector2(.1f, -.2f);
                AssertAutomatic(camera);
            }
            finally { auxiliary.Restore(); main.Restore(); Object.DestroyImmediate(go); }
        }

        private static void AssertAutomatic(Camera camera)
        {
            Matrix4x4 observed = camera.projectionMatrix;
            camera.ResetProjectionMatrix();
            Assert.That(observed, Is.EqualTo(camera.projectionMatrix), "Camera parameters must update projection without an AA callback");
        }

        [TestCase(false)] [TestCase(true)]
        public void ExplicitProjectionRemainsExplicitEvenWhenEqualToAutomatic(bool auxiliary)
        {
            var go = new GameObject("explicit-projection");
            var camera = go.AddComponent<Camera>();
            var original = camera.projectionMatrix;
            camera.projectionMatrix = original;
            var main = new CameraProjectionState();
            var scope = new AuxiliaryProjectionScope();
            try
            {
                if (auxiliary) scope.Apply(camera, new Source());
                else main.Apply(camera, new Vector2(.25f, -.25f));
                scope.Restore(); main.Restore();
                camera.fieldOfView = 90; camera.farClipPlane = 300000;
                Assert.That(camera.projectionMatrix, Is.EqualTo(original));
            }
            finally { scope.Restore(); main.Restore(); Object.DestroyImmediate(go); }
        }

        [Test]
        public void FailedJitterProviderLeavesAutomaticCameraUntouched()
        {
            var go = new GameObject("failed-jitter");
            var camera = go.AddComponent<Camera>();
            var state = new CameraProjectionState();
            var original = camera.projectionMatrix;
            try
            {
                Assert.Throws<InvalidOperationException>(() => state.Apply(camera, Vector2.zero,
                    (c, j) => throw new InvalidOperationException()));
                state.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(original));
                camera.fieldOfView = 80;
                AssertAutomatic(camera);
            }
            finally { state.Restore(); Object.DestroyImmediate(go); }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void BackendDeactivationReleasesBothCamerasBeforeOff(int mode)
        {
            var go = new GameObject("resolve-cleanup");
            var scaledGo = new GameObject("scaled-cleanup");
            var camera = go.AddComponent<Camera>();
            var scaled = scaledGo.AddComponent<Camera>();
            var motion = new MotionVectorSanitizer(null, null);
            ITemporalBackend backend = mode == 0 ? (ITemporalBackend)new CustomTaaBackend(null, null, null, motion, null) :
                mode == 1 ? new NvidiaDlaaBackend(null, null, null, motion, null) :
                new AmdFsr2Backend(null, null, null, motion, null);
            try
            {
                var resolveState = new CameraProjectionState();
                var scaledState = new CameraProjectionState();
                resolveState.Apply(camera, new Vector2(.25f, -.25f), jitterTransparentRendering: true);
                scaledState.Apply(scaled, new Vector2(.25f, -.25f), jitterTransparentRendering: true);
                backend.GetType().GetField("_resolveProjection", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(backend, resolveState);
                backend.GetType().GetField("_sharedProjection", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(backend, scaledState);
                // Represents a mode switch or an aborted render, before post-render cleanup.
                backend.Deactivate(); backend.Deactivate();
                camera.farClipPlane = 300000; scaled.farClipPlane = 900000;
                camera.fieldOfView = 70; scaled.fieldOfView = 70;
                AssertAutomatic(camera); AssertAutomatic(scaled);
                Assert.That(((IProjectionJitterSource)backend).TryGetRasterProjection(camera, out _), Is.False);
            }
            finally { backend.Dispose(); motion.Dispose(); Object.DestroyImmediate(go); Object.DestroyImmediate(scaledGo); }
        }

        [Test]
        public void ExternalResetIsNotReplacedByAnOlderExplicitMatrix()
        {
            var go = new GameObject("external-reset");
            var camera = go.AddComponent<Camera>();
            var scope = new ProjectionOverride();
            try
            {
                var automatic = camera.projectionMatrix;
                camera.projectionMatrix = Matrix4x4.Perspective(80, 1, 1, 100);
                scope.Apply(camera, automatic);
                camera.ResetProjectionMatrix(); // Same applied values, different owner/mode.
                scope.Restore();
                camera.fieldOfView = 90;
                AssertAutomatic(camera);
            }
            finally { scope.Restore(); Object.DestroyImmediate(go); }
        }

        [Test]
        public void SmallExternalDepthChangeIsPreservedExactly()
        {
            var go = new GameObject("external-depth-change");
            var camera = go.AddComponent<Camera>();
            var scope = new ProjectionOverride();
            try
            {
                var applied = camera.projectionMatrix;
                scope.Apply(camera, applied);
                var external = applied; external.m22 += .000001f;
                camera.projectionMatrix = external;
                scope.Restore();
                camera.farClipPlane = 900000;
                Assert.That(camera.projectionMatrix.Equals(external), Is.True);
            }
            finally { scope.Restore(); Object.DestroyImmediate(go); }
        }
    }
}
