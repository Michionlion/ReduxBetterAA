using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

namespace ReduxBetterAA.Tests
{
    public sealed class ResolvedFrameCaptureTests
    {
        private GameObject _go;
        private Camera _camera;
        private RenderTexture _color, _target, _depth, _motion;
        private ResolvedFrameCapture _publisher;
        private ResolvedFrameRegistration _registration;
        private Consumer _consumer;

        private sealed class Consumer : IResolvedFrameConsumer
        {
            internal int Begins, Captures, Ends, Stops;
            internal bool Decline, ThrowBegin, ThrowCapture, ThrowEnd, ThrowStop, Captured;
            internal RenderTexture Target;
            internal ResolvedFrameRequest LastRequest;
            internal BorrowedResolvedFrame LastBorrow;
            internal Action OnBegin, OnCapture;
            public bool TryBegin(in ResolvedFrameRequest request, out RenderTexture finalColorTarget)
            {
                ++Begins; LastRequest = request; finalColorTarget = Target;
                OnBegin?.Invoke();
                if (ThrowBegin) throw new InvalidOperationException("begin");
                return !Decline;
            }
            public void Capture(in BorrowedResolvedFrame frame)
            {
                ++Captures; LastBorrow = frame;
                OnCapture?.Invoke();
                if (ThrowCapture) throw new InvalidOperationException("capture");
                // Test only: retaining this snapshot verifies identity, and never
                // pretends to retain its pixels or establish a native GPU lease.
            }
            public void End(in ResolvedFrameRequest request, bool captured)
            { ++Ends; Captured = captured; if (ThrowEnd) throw new InvalidOperationException("end"); }
            public void ProducerStopped(int producerId)
            { ++Stops; if (ThrowStop) throw new InvalidOperationException("stop"); }
        }

        [SetUp]
        public void Setup()
        {
            _go = new GameObject("resolved-frame-capture-test");
            _camera = _go.AddComponent<Camera>();
            _camera.enabled = false;
            _color = Texture(32, 16, RenderTextureFormat.ARGBHalf);
            _target = Texture(32, 16, RenderTextureFormat.ARGBHalf);
            _depth = Texture(32, 16, RenderTextureFormat.RFloat);
            _motion = Texture(32, 16, RenderTextureFormat.RGHalf);
            _publisher = new ResolvedFrameCapture();
            _publisher.Configure(_camera, BackendSelection.CustomTaa);
            _consumer = new Consumer { Target = _target };
        }

        [TearDown]
        public void Cleanup()
        {
            _publisher.Deactivate();
            _registration?.Dispose();
            foreach (Object item in new Object[] { _color, _target, _depth, _motion, _go })
                if (item != null) Object.DestroyImmediate(item);
        }

        [Test]
        public void NoConsumerDoesNotSnapshotAllocateOrRequireTextures()
        {
            Assert.That(ResolvedFrameCapture.HasConsumer, Is.False);
            SceneOutputFrame noClaim = default;
            // Warm-up first, then measure only the dormant hot path.
            for (int i = 0; i < 10; ++i)
            {
                _publisher.Snapshot(_camera, Matrix4x4.identity, Vector2.zero, i, i);
                _publisher.TryBegin(null, 0, 0, false, 1, Vector2.one, true, false, in noClaim, out _, i);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; ++i)
            {
                _publisher.Snapshot(_camera, Matrix4x4.identity, Vector2.zero, i, i);
                _publisher.TryBegin(null, 0, 0, false, 1, Vector2.one, true, false, in noClaim, out _, i);
                _publisher.PublishResolved(null, null, null, false, 1, Vector2.one, false, in noClaim);
            }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
            Register();
            Assert.That(Begin(99), Is.False, "Registration must not revive an unobserved pre-cull snapshot");
            Assert.That(_consumer.Begins, Is.Zero);
        }

        [Test]
        public void SuccessfulBorrowPreservesRawInputsCameraSnapshotAndUnscaledTime()
        {
            Register();
            Matrix4x4 projection = Matrix4x4.Perspective(57, 2, .3f, 10000);
            _camera.transform.position = new Vector3(3, 4, 5);
            Matrix4x4 view = _camera.worldToCameraMatrix;
            _publisher.Snapshot(_camera, projection, new Vector2(.25f, -.3f), 10, 20);
            _camera.transform.position = new Vector3(900, 800, 700);
            Assert.That(Begin(10, reset: true), Is.True);
            _publisher.Publish(_target, _depth, _motion);
            var first = _consumer.LastRequest;
            Assert.That(first.Token.FrameId, Is.EqualTo(10));
            Assert.That(first.AcceptedSceneOutput.OwnerId, Is.Zero, "Native AA takes no SR claim");
            Assert.That(first.Camera.WorldToView, Is.EqualTo(view));
            Assert.That(first.Camera.NonJitteredProjection, Is.EqualTo(projection));
            Assert.That(first.Camera.JitterRenderPixels, Is.EqualTo(new Vector2(.25f, -.3f)));
            Assert.That(first.DispatchPreExposure, Is.EqualTo(1.25f));
            Assert.That(first.MotionComponentSigns, Is.EqualTo(new Vector2(-1, 1)));
            Assert.That(first.ResetHistory, Is.True);
            Assert.That(_consumer.LastBorrow.ResolvedColor, Is.SameAs(_target));
            Assert.That(_consumer.LastBorrow.RawDeviceDepth, Is.SameAs(_depth));
            Assert.That(_consumer.LastBorrow.SanitizedMotion, Is.SameAs(_motion));
            Assert.That(_consumer.Ends, Is.EqualTo(1));
            _publisher.Snapshot(_camera, projection, Vector2.zero, 11, 20.016);
            Assert.That(Begin(11), Is.True);
            _publisher.Publish(_target, _depth, _motion);
            Assert.That(_consumer.LastRequest.ResetHistory, Is.False);
            Assert.That(_consumer.LastRequest.FrameTimeMilliseconds, Is.EqualTo(16).Within(.001));
            Assert.That(_consumer.LastRequest.PreviousViewProjection, Is.EqualTo(first.Camera.ViewProjection));
        }

        [Test]
        public void StaleDuplicateAndAbortedBorrowsCannotPublishOldPixels()
        {
            Register(); Snapshot(10);
            Assert.That(Begin(9), Is.False);
            Assert.That(Begin(10), Is.True);
            _publisher.Abort(); _publisher.Abort();
            _publisher.Publish(_target, _depth, _motion);
            Assert.That(_consumer.Ends, Is.EqualTo(1));
            Assert.That(_consumer.Captures, Is.Zero);
            Assert.That(Begin(10), Is.False);
            Snapshot(11);
            Assert.That(Begin(11), Is.True);
            _publisher.Publish(_target, _depth, _motion);
            Snapshot(11);
            Assert.That(Begin(11), Is.False, "A second camera render in one Unity real frame is not another FG input");
        }

        [Test]
        public void DeclineAndMissingTaaTargetLeaveNoOpenBorrowOrFalseSuccess()
        {
            Register(); _consumer.Decline = true; Snapshot(10);
            Assert.That(Begin(10), Is.False);
            Assert.That(_consumer.Ends, Is.Zero);
            _consumer.Decline = false; _consumer.Target = null; Snapshot(11);
            Assert.That(Begin(11), Is.False);
            Assert.That(_consumer.Ends, Is.EqualTo(1));
            Assert.That(_consumer.Captured, Is.False);
            Assert.That(_registration.Active, Is.True, "A declined slot is not an SDK failure");
        }

        [TestCase("alias")] [TestCase("format")] [TestCase("extent")] [TestCase("uncreated")]
        public void IncompatibleTaaStorageIsRejectedBeforeFinalPass(string kind)
        {
            Register();
            RenderTexture invalid = kind == "alias" ? _color :
                Texture(kind == "extent" ? 16 : 32, 16, kind == "format" ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGBHalf);
            try
            {
                if (kind == "uncreated") invalid.Release();
                _consumer.Target = invalid; Snapshot(10);
                Assert.That(Begin(10), Is.False);
                Assert.That(_consumer.Captures, Is.Zero);
                Assert.That(_consumer.Ends, Is.EqualTo(1));
            }
            finally { if (invalid != _color) Object.DestroyImmediate(invalid); }
        }

        [Test]
        public void CaptureRequiresTheExactResolvedTextureAndCurrentInputExtents()
        {
            Register(); Snapshot(10);
            Assert.That(Begin(10), Is.True);
            _publisher.Publish(_color, _depth, _motion);
            Assert.That(_consumer.Captures, Is.Zero, "Equal dimensions do not make history/source the final result");
            Snapshot(11);
            Assert.That(Begin(11), Is.True);
            _publisher.Publish(_target, Texture2D.blackTexture, _motion);
            Assert.That(_consumer.Captures, Is.Zero);
            Assert.That(_consumer.Ends, Is.EqualTo(2));
        }

        [Test]
        public void ResetGapResizeAndNewProducerInvalidateTemporalContinuity()
        {
            Register(); Capture(10);
            int firstProducer = _consumer.LastRequest.Token.ProducerId;
            int firstGeneration = _consumer.LastRequest.Token.Generation;
            _publisher.Reset(HistoryResetReason.CameraCut); Capture(11);
            Assert.That(_consumer.LastRequest.ResetReason.HasFlag(HistoryResetReason.CameraCut), Is.True);
            Capture(13);
            Assert.That(_consumer.LastRequest.ResetReason.HasFlag(HistoryResetReason.InvalidInput), Is.True);
            Snapshot(14);
            SceneOutputFrame noClaim = default;
            Assert.That(_publisher.TryBegin(_color, 16, 8, false, 1, Vector2.one, false, false,
                in noClaim, out _, 14), Is.True);
            Assert.That(_consumer.LastRequest.Token.Generation, Is.GreaterThan(firstGeneration));
            Assert.That(_consumer.LastRequest.ResetHistory, Is.True);
            _publisher.Abort();
            _publisher.Configure(_camera, BackendSelection.NvidiaDlaa);
            Capture(15);
            Assert.That(_consumer.LastRequest.Token.ProducerId, Is.Not.EqualTo(firstProducer));
            Assert.That(_consumer.LastRequest.ResetReason.HasFlag(HistoryResetReason.FirstFrame), Is.True);
        }

        [TestCase("begin")] [TestCase("capture")] [TestCase("end")]
        public void ConsumerExceptionDisablesOnlyCaptureAndClosesAcceptedBorrowOnce(string stage)
        {
            Register();
            _consumer.ThrowBegin = stage == "begin"; _consumer.ThrowCapture = stage == "capture"; _consumer.ThrowEnd = stage == "end";
            Snapshot(10);
            bool began = Begin(10);
            if (began) Assert.DoesNotThrow(() => _publisher.Publish(_target, _depth, _motion));
            Assert.That(_registration.Active, Is.False);
            Assert.That(_registration.FailureReason, Does.Contain(stage));
            Assert.That(ResolvedFrameCapture.HasConsumer, Is.False);
            _publisher.Abort();
            Assert.That(_consumer.Ends, Is.EqualTo(stage == "begin" ? 0 : 1));
        }

        [Test]
        public void TeardownIsIdempotentAndRegistrationReplacementStartsFresh()
        {
            Register(); Capture(10);
            var old = _consumer;
            _registration.Dispose();
            _consumer = new Consumer { Target = _target }; Register();
            Snapshot(11);
            Assert.That(old.Stops, Is.EqualTo(1));
            Assert.That(Begin(11), Is.True);
            _publisher.Publish(_target, _depth, _motion);
            Assert.That(_consumer.LastRequest.ResetHistory, Is.True);
            _consumer.ThrowStop = true;
            Assert.DoesNotThrow(() => _publisher.Deactivate());
            _publisher.Deactivate();
            Assert.That(_consumer.Stops, Is.EqualTo(1));
            Assert.That(_registration.Active, Is.False);
        }

        [TestCase(true)] [TestCase(false)]
        public void ReconfigurationInsideConsumerCannotCommitOldFrameIntoNewProducer(bool duringBegin)
        {
            Register(); Snapshot(10);
            Action replace = () => _publisher.Configure(_camera, BackendSelection.NvidiaDlaa);
            if (duringBegin) _consumer.OnBegin = replace; else _consumer.OnCapture = replace;
            bool began = Begin(10);
            if (began) _publisher.Publish(_target, _depth, _motion);
            int oldProducer = _consumer.LastRequest.Token.ProducerId;
            Assert.That(_consumer.Ends, Is.EqualTo(1));
            Assert.That(_consumer.Captured, Is.False);
            _consumer.OnBegin = null; _consumer.OnCapture = null;
            Capture(11);
            Assert.That(_consumer.LastRequest.Token.ProducerId, Is.Not.EqualTo(oldProducer));
            Assert.That(_consumer.LastRequest.ResetHistory, Is.True);
            Assert.That(_consumer.LastRequest.ResetReason.HasFlag(HistoryResetReason.FirstFrame), Is.True);
            Assert.That(_consumer.Ends, Is.EqualTo(2));
        }

        [Test]
        public void ResetRequestedByConsumerIsPreservedForTheNextFrame()
        {
            Register();
            _consumer.OnCapture = () => _publisher.Reset(HistoryResetReason.Manual);
            Capture(10); _consumer.OnCapture = null; Capture(11);
            Assert.That(_consumer.LastRequest.ResetReason.HasFlag(HistoryResetReason.Manual), Is.True);
        }

        [Test]
        public void SuperResolutionRequiresMatchingAcceptedTokenAndBorrowsVendorOutputWithoutTarget()
        {
            Register(); _consumer.Target = null; Snapshot(10);
            SceneOutputFrame noClaim = default;
            Assert.That(_publisher.TryBegin(_color, 32, 16, false, 1, Vector2.one, false, true,
                in noClaim, out _, 10), Is.False);
            Snapshot(11);
            var frame = new SceneOutputFrame(42, EntityId.ToULong(_camera.GetEntityId()), 4, 11, 7, 32, 16, 32, 16);
            Assert.That(_publisher.TryBegin(_color, 32, 16, true, 1, Vector2.one, false, true,
                in frame, out _, 11), Is.True);
            _publisher.Publish(_color, _depth, _motion);
            Assert.That(_consumer.LastBorrow.ResolvedColor, Is.SameAs(_color));
            Assert.That(_consumer.LastRequest.AcceptedSceneOutput.Matches(in frame), Is.True);
            Assert.That(_consumer.Captured, Is.True);
        }

        [TestCase(0f, false, false)] [TestCase(.7f, false, false)]
        [TestCase(.7f, true, false)] [TestCase(.7f, false, true)]
        public void ActualTaaFinalPassPreservesOutputAndNeverBorrowsHistoryDepth(float sharpness, bool captureThrows, bool debug)
        {
            // Exercise the real Render method with controlled existing resources.
            // This avoids Addressables/camera discovery while testing the actual
            // history-vs-final and callback failure seam, rather than a fake FG.
            var owned = new List<Object>();
            RenderTexture Make(int w, int h, RenderTextureFormat format)
            { var item = Texture(w, h, format); owned.Add(item); return item; }
            var historyA = Make(32, 16, RenderTextureFormat.ARGBHalf);
            var historyB = Make(32, 16, RenderTextureFormat.ARGBHalf);
            var depthA = Make(32, 16, RenderTextureFormat.RFloat);
            var depthB = Make(32, 16, RenderTextureFormat.RFloat);
            var finalTarget = Make(32, 16, RenderTextureFormat.ARGBHalf);
            var destination = Make(32, 16, RenderTextureFormat.ARGBHalf);
            var sanitized = Make(32, 16, RenderTextureFormat.RGHalf);
            var corruption = Make(1, 1, RenderTextureFormat.RGHalf);
            var pattern = new Texture2D(32, 16, TextureFormat.RGBAFloat, false, true);
            owned.Add(pattern);
            var pixels = new Color[32 * 16];
            for (int y = 0; y < 16; ++y)
                for (int x = 0; x < 32; ++x)
                    pixels[y * 32 + x] = new Color(.2f + ((x * 13 + y * 7) % 23) / 40f, .4f, .7f, .8f);
            pattern.SetPixels(pixels); pattern.Apply();
            var taaShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/CustomTaa.shader");
            var motionShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/MotionVectorSanitizer.shader");
            Assert.That(taaShader != null && motionShader != null, Is.True);
            var material = new Material(taaShader); owned.Add(material);
            var motionMaterial = new Material(motionShader); owned.Add(motionMaterial);
            var sanitizer = new MotionVectorSanitizer(null, null);
            Set(sanitizer, "_shader", motionShader); Set(sanitizer, "_material", motionMaterial);
            Set(sanitizer, "_sanitizedMotion", sanitized); Set(sanitizer, "_frameCorruption", corruption);
            Set(sanitizer, "_resourceWidth", 32); Set(sanitizer, "_resourceHeight", 16);
            string failure = null;
            var backend = new CustomTaaBackend(null, null, new BackendPerformanceProfiler(), sanitizer, reason => failure = reason);
            Set(backend, "_material", material); Set(backend, "_active", true);
            Set(backend, "_historyColorA", historyA); Set(backend, "_historyColorB", historyB);
            Set(backend, "_historyDepthA", depthA); Set(backend, "_historyDepthB", depthB);
            Set(backend, "_resourceWidth", 32); Set(backend, "_resourceHeight", 16);
            Set(backend, "_resourceFormat", _color.format); Set(backend, "_resourceSrgb", _color.sRGB);
            var config = new CustomTaaConfig(.75f, 8, .93f, .1f, 8, 256, .01f, .75f, 1.25f, 2,
                sharpness, .25f, debug ? CustomTaaDebugView.HistoryWeight : CustomTaaDebugView.FinalResolve);
            backend.ApplyConfig(in config);
            var capture = (ResolvedFrameCapture)typeof(CustomTaaBackend).GetField("_resolvedCapture",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(backend);
            Texture oldDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            Texture oldMotion = Shader.GetGlobalTexture("_CameraMotionVectorsTexture");
            Vector4 oldZ = Shader.GetGlobalVector("_ZBufferParams");
            try
            {
                Graphics.Blit(pattern, _color); Graphics.Blit(Texture2D.whiteTexture, _depth);
                Graphics.Blit(Texture2D.blackTexture, _motion);
                Shader.SetGlobalTexture("_CameraDepthTexture", _depth);
                Shader.SetGlobalTexture("_CameraMotionVectorsTexture", _motion);
                Shader.SetGlobalVector("_ZBufferParams", new Vector4(1, 1, 0, 0));
                backend.Render(_color, _target); // The normal unobserved output.
                Assert.That(failure, Is.Null);
                Color[] expected = Read(_target);
                Set(backend, "_historyReadA", true); Set(backend, "_historyValid", false);
                Set(backend, "_matrixHistoryValid", false);
                _consumer.Target = finalTarget; _consumer.ThrowCapture = captureThrows; Register();
                capture.Configure(_camera, BackendSelection.CustomTaa);
                capture.Snapshot(_camera, _camera.projectionMatrix, Vector2.zero);
                backend.Render(_color, destination);
                Assert.That(failure, Is.Null, "Observer failure must not fail AA");
                Assert.That(MaxDifference(expected, Read(destination)), Is.LessThan(.0011f));
                Assert.That(_consumer.Captures, Is.EqualTo(debug ? 0 : 1));
                if (!debug)
                {
                    Assert.That(_consumer.LastBorrow.ResolvedColor, Is.SameAs(finalTarget));
                    Assert.That(_consumer.LastBorrow.RawDeviceDepth, Is.SameAs(_depth));
                    Assert.That(_consumer.LastBorrow.RawDeviceDepth, Is.Not.SameAs(depthA));
                    Assert.That(_consumer.LastBorrow.RawDeviceDepth, Is.Not.SameAs(depthB));
                    Assert.That(MaxDifference(expected, Read(finalTarget)), Is.LessThan(.0011f));
                    if (sharpness > 0)
                        Assert.That(MaxDifference(Read(historyB), Read(finalTarget)), Is.GreaterThan(.001),
                            "Sharpened final output must differ from unsharpened temporal history in this fixture");
                }
            }
            finally
            {
                capture.Deactivate();
                Shader.SetGlobalTexture("_CameraDepthTexture", oldDepth);
                Shader.SetGlobalTexture("_CameraMotionVectorsTexture", oldMotion);
                Shader.SetGlobalVector("_ZBufferParams", oldZ);
                foreach (Object item in owned) Object.DestroyImmediate(item);
            }
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static Color[] Read(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); texture.Apply();
                return texture.GetPixels();
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(texture); }
        }
        private static float MaxDifference(Color[] first, Color[] second)
        {
            float maximum = 0;
            for (int i = 0; i < first.Length; ++i)
                for (int channel = 0; channel < 4; ++channel)
                    maximum = Mathf.Max(maximum, Mathf.Abs(first[i][channel] - second[i][channel]));
            return maximum;
        }

        private void Register() { Assert.That(ResolvedFrameCapture.TryRegister(_consumer, out _registration), Is.True); }
        private void Snapshot(int frame) => _publisher.Snapshot(_camera, _camera.projectionMatrix, Vector2.zero, frame, frame * .016);
        private bool Begin(int frame, bool reset = false)
        {
            SceneOutputFrame noClaim = default;
            return _publisher.TryBegin(_color, 32, 16, reset, 1.25f, new Vector2(-1, 1), true, false, in noClaim, out _, frame);
        }
        private void Capture(int frame) { Snapshot(frame); Assert.That(Begin(frame), Is.True); _publisher.Publish(_target, _depth, _motion); }
        private static RenderTexture Texture(int width, int height, RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
            Assert.That(texture.Create(), Is.True);
            return texture;
        }
    }
}
