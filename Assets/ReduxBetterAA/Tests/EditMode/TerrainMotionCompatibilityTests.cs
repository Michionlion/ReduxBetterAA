using System;
using System.Reflection;
using NUnit.Framework;
using ReduxBetterAA.Patches;
using ReduxBetterAA.Rendering;
using UnityEngine;
using Service = ReduxBetterAA.Rendering.TerrainMotionCompatibility;

namespace ReduxBetterAA.Tests
{
    public sealed class TerrainMotionCompatibilityTests
    {
        private GameObject _cameraObject, _otherCameraObject;
        private Camera _camera, _otherCamera;
        private Material _material, _otherMaterial;
        private RenderTexture _depth, _otherDepth;

        [SetUp]
        public void SetUp()
        {
            _cameraObject = new GameObject("Terrain history camera");
            _otherCameraObject = new GameObject("Other terrain history camera");
            _camera = _cameraObject.AddComponent<Camera>();
            _otherCamera = _otherCameraObject.AddComponent<Camera>();
            Shader shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            _material = new Material(shader); _otherMaterial = new Material(shader);
            _depth = new RenderTexture(8, 8, 0); _depth.Create();
            _otherDepth = new RenderTexture(8, 8, 0); _otherDepth.Create();
        }

        [TearDown]
        public void TearDown()
        {
            if (_depth != null) { _depth.Release(); UnityEngine.Object.DestroyImmediate(_depth); }
            if (_otherDepth != null) { _otherDepth.Release(); UnityEngine.Object.DestroyImmediate(_otherDepth); }
            UnityEngine.Object.DestroyImmediate(_material); UnityEngine.Object.DestroyImmediate(_otherMaterial);
            UnityEngine.Object.DestroyImmediate(_cameraObject); UnityEngine.Object.DestroyImmediate(_otherCameraObject);
        }

        private void Generate(Service.Entry entry, int frame, Matrix4x4 world)
        {
            var observation = Service.BeginRecord(entry, null, null, Service.Boundary.Generation, frame, world);
            Service.CompleteRecord(observation, true, world, null);
        }

        private void Draw(Service.Entry entry, int frame, Matrix4x4 world, Service.Boundary kind,
            Camera camera = null, Material material = null, RenderTexture depth = null, bool ranOriginal = true)
        {
            var observation = Service.BeginRecord(entry, camera == null ? _camera : camera,
                material == null ? _material : material, kind, frame, world);
            Service.CompleteRecord(observation, ranOriginal, world, depth == null ? _depth : depth);
        }

        private void CompleteFrame(Service.Entry entry, int frame, Matrix4x4 world,
            Camera camera = null, RenderTexture depth = null)
        {
            Generate(entry, frame, world);
            Draw(entry, frame, world, Service.Boundary.Depth, camera, depth: depth);
            Draw(entry, frame, world, Service.Boundary.Color, camera, depth: depth);
        }

        private bool Resolve(Service.Entry entry, int frame = 11) =>
            Service.TryResolveRecord(entry, _camera, frame, _depth, out _);

        [Test]
        public void AdjacentLinkedDrawsUsePreviousWorldTimesCurrentInverse()
        {
            var entry = new Service.Entry();
            var oldWorld = Matrix4x4.TRS(new Vector3(20, -4, 7), Quaternion.Euler(4, 35, -8), Vector3.one);
            var world = Matrix4x4.TRS(new Vector3(17, -3, 12), Quaternion.Euler(2, 37, -9), Vector3.one);
            CompleteFrame(entry, 10, oldWorld);
            Assert.That(Resolve(entry, 10), Is.False, "One draw has no preceding rendered history.");
            CompleteFrame(entry, 11, world);
            Assert.That(Service.TryResolveRecord(entry, _camera, 11, _depth, out TerrainMotionFrame result), Is.True);
            Assert.That(result.Depth, Is.SameAs(_depth));
            Assert.That(result.PreviousFrame, Is.EqualTo(10));
            Assert.That(result.Frame, Is.EqualTo(11));
            var localPoint = new Vector3(12, -8, 3);
            Assert.That(Vector3.Distance(result.PreviousWorldFromCurrent.MultiplyPoint3x4(world.MultiplyPoint3x4(localPoint)),
                oldWorld.MultiplyPoint3x4(localPoint)), Is.LessThan(.0001f));
        }

        [TestCase((int)Service.Boundary.Depth)]
        [TestCase((int)Service.Boundary.Color)]
        public void DuplicateDrawCannotCertifyEitherTheDuplicateFrameOrItsSuccessor(int boundaryValue)
        {
            var boundary = (Service.Boundary)boundaryValue;
            var entry = new Service.Entry();
            CompleteFrame(entry, 10, Matrix4x4.identity);
            CompleteFrame(entry, 11, Matrix4x4.identity);
            Draw(entry, 11, Matrix4x4.identity, boundary);
            Assert.That(Resolve(entry), Is.False);
            CompleteFrame(entry, 12, Matrix4x4.identity);
            Assert.That(Resolve(entry, 12), Is.False);
            CompleteFrame(entry, 13, Matrix4x4.identity);
            Assert.That(Resolve(entry, 13), Is.True, "Two fresh unique frames restore history.");
        }

        [TestCase(9)]
        [TestCase(12)]
        public void NonAdjacentDrawHistoryIsUnavailable(int nextFrame)
        {
            var entry = new Service.Entry();
            CompleteFrame(entry, 10, Matrix4x4.identity);
            CompleteFrame(entry, nextFrame, Matrix4x4.identity);
            Assert.That(Resolve(entry, nextFrame), Is.False);
        }

        [Test]
        public void RegenerationBetweenDepthAndColorCannotJoinDifferentVertexBuffers()
        {
            var entry = new Service.Entry(); CompleteFrame(entry, 10, Matrix4x4.identity);
            Generate(entry, 11, Matrix4x4.identity);
            Draw(entry, 11, Matrix4x4.identity, Service.Boundary.Depth);
            var next = Matrix4x4.Translate(Vector3.right);
            Generate(entry, 11, next);
            Draw(entry, 11, next, Service.Boundary.Color);
            Assert.That(Resolve(entry), Is.False);
        }

        [Test]
        public void StaleQueriesDoNotReuseTheLastCompleteDepthBinding()
        {
            var entry = new Service.Entry();
            CompleteFrame(entry, 10, Matrix4x4.identity);
            CompleteFrame(entry, 11, Matrix4x4.identity);
            Assert.That(Resolve(entry, 10), Is.False);
            Assert.That(Resolve(entry, 12), Is.False);
            Assert.That(Resolve(entry, 11), Is.True);
        }

        [Test]
        public void UnrenderedGenerationDoesNotReplacePreviousDrawWorld()
        {
            var entry = new Service.Entry();
            CompleteFrame(entry, 10, Matrix4x4.Translate(new Vector3(10, 0, 0)));
            Generate(entry, 11, Matrix4x4.Translate(new Vector3(500, 0, 0)));
            CompleteFrame(entry, 11, Matrix4x4.Translate(new Vector3(13, 0, 0)));
            Assert.That(Service.TryResolveRecord(entry, _camera, 11, _depth, out var result), Is.True);
            Assert.That(result.PreviousWorldFromCurrent.m03, Is.EqualTo(-3).Within(.0001f));
        }

        [Test]
        public void SameFrameCameraAndMaterialMustAgreeAcrossDepthAndColor()
        {
            foreach (bool cameraMismatch in new[] { true, false })
            {
                var entry = new Service.Entry(); CompleteFrame(entry, 10, Matrix4x4.identity);
                Generate(entry, 11, Matrix4x4.identity);
                Draw(entry, 11, Matrix4x4.identity, Service.Boundary.Depth);
                Draw(entry, 11, Matrix4x4.identity, Service.Boundary.Color,
                    cameraMismatch ? _otherCamera : _camera, cameraMismatch ? _material : _otherMaterial);
                Assert.That(Resolve(entry), Is.False);
            }
        }

        [Test]
        public void ChangedCameraOrDepthAllocationBreaksPreviousFrameHistory()
        {
            foreach (bool cameraChanged in new[] { true, false })
            {
                var entry = new Service.Entry();
                CompleteFrame(entry, 10, Matrix4x4.identity, cameraChanged ? _otherCamera : _camera,
                    cameraChanged ? _depth : _otherDepth);
                CompleteFrame(entry, 11, Matrix4x4.identity);
                Assert.That(Resolve(entry), Is.False);
            }
        }

        [Test]
        public void DestroyedDisabledOrReboundInputCannotBeBorrowed()
        {
            var entry = new Service.Entry();
            CompleteFrame(entry, 10, Matrix4x4.identity); CompleteFrame(entry, 11, Matrix4x4.identity);
            Assert.That(Service.TryResolveRecord(entry, _camera, 11, _otherDepth, out _), Is.False);
            _camera.enabled = false; Assert.That(Resolve(entry), Is.False);
            _camera.enabled = true; Assert.That(Resolve(entry), Is.True);
            _depth.Release(); Assert.That(Resolve(entry), Is.False);
        }

        [TestCase((int)Service.Boundary.Generation)]
        [TestCase((int)Service.Boundary.Depth)]
        [TestCase((int)Service.Boundary.Color)]
        public void SkippedOriginalOrExceptionInvalidatesTheFrame(int boundaryValue)
        {
            var boundary = (Service.Boundary)boundaryValue;
            foreach (bool exception in new[] { false, true })
            {
                var entry = new Service.Entry(); CompleteFrame(entry, 10, Matrix4x4.identity);
                var generation = Service.BeginRecord(entry, null, null, Service.Boundary.Generation, 11, Matrix4x4.identity);
                Service.CompleteRecord(generation, boundary != Service.Boundary.Generation || exception, Matrix4x4.identity, null);
                if (boundary == Service.Boundary.Generation && exception) Service.RejectRecord(generation);
                foreach (var draw in new[] { Service.Boundary.Depth, Service.Boundary.Color })
                {
                    var observation = Service.BeginRecord(entry, _camera, _material, draw, 11, Matrix4x4.identity);
                    Service.CompleteRecord(observation, boundary != draw || exception, Matrix4x4.identity, _depth);
                    if (boundary == draw && exception) Service.RejectRecord(observation);
                }
                Assert.That(Resolve(entry), Is.False);
            }
        }

        [TestCase((int)Service.Boundary.Generation)]
        [TestCase((int)Service.Boundary.Depth)]
        [TestCase((int)Service.Boundary.Color)]
        public void ChangedWorldBookendInvalidatesAttribution(int boundaryValue)
        {
            var boundary = (Service.Boundary)boundaryValue;
            var entry = new Service.Entry(); CompleteFrame(entry, 10, Matrix4x4.identity);
            if (boundary != Service.Boundary.Generation) Generate(entry, 11, Matrix4x4.identity);
            var observation = Service.BeginRecord(entry, _camera, _material, boundary, 11, Matrix4x4.identity);
            Service.CompleteRecord(observation, true, Matrix4x4.Translate(Vector3.right), _depth);
            if (boundary != Service.Boundary.Depth) Draw(entry, 11, Matrix4x4.identity, Service.Boundary.Depth);
            if (boundary != Service.Boundary.Color) Draw(entry, 11, Matrix4x4.identity, Service.Boundary.Color);
            Assert.That(Resolve(entry), Is.False);
        }

        [Test]
        public void EpochResetIgnoresAnInProgressOldObservation()
        {
            using (var service = new Service(null))
            {
                // Exercise the real epoch check without installing game patches.
                typeof(Service).GetField("_installed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, true);
                typeof(Service).GetField("_ownerThread", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, Environment.CurrentManagedThreadId);
                var entry = new Service.Entry();
                var observation = new Service.Observation(service, entry, 1, 11, Service.Boundary.Depth, Matrix4x4.identity);
                service.Reject(observation);
                Assert.That(entry.Current.Rejected, Is.True, "The same observation is accepted before reset.");
                entry.Current.Rejected = false;
                service.Invalidate(); service.Reject(observation);
                Assert.That(entry.Current.Rejected, Is.False, "An old callback cannot mutate the new history epoch.");
                Assert.That(service.TryGetFrame(_camera, 11, out _), Is.False);
            }
        }

        [Test]
        public void CurrentPlayerTerrainBoundariesMatchTheAudit()
        {
            // The legacy player intentionally has no audited terrain observer.
            if (Application.unityVersion == "6000.4.1f1") return;
            Assert.That(TerrainMotionCompatibilityPatch.TryResolveTargets(out _, out _, out _), Is.True);
        }

        [Test]
        public void InvalidMatricesAndUnauditedModuleRemainUnavailable()
        {
            Assert.That(Service.TryPreviousWorldFromCurrent(Matrix4x4.identity, Matrix4x4.zero, out _), Is.False);
            var invalid = Matrix4x4.identity; invalid.m03 = float.NaN;
            Assert.That(Service.TryPreviousWorldFromCurrent(invalid, Matrix4x4.identity, out _), Is.False);
            invalid = Matrix4x4.identity; invalid.m30 = 1;
            Assert.That(Service.TryPreviousWorldFromCurrent(Matrix4x4.identity, invalid, out _), Is.False);
            Assert.That(TerrainMotionCompatibilityPatch.SupportsModule(Guid.Empty), Is.False);
            using (var service = new Service(null))
            {
                Assert.That(service.Available, Is.False);
                Assert.That(service.TryGetFrame(_camera, 11, out _), Is.False);
            }
        }
    }
}
