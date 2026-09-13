using NUnit.Framework;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class ReduxSceneOutputTests
    {
        private static SceneOutputFrame Frame(int owner = 1, int camera = 2,
            int sequence = 3, int frame = 4, int generation = 5,
            int renderWidth = 1280, int renderHeight = 720,
            int outputWidth = 2560, int outputHeight = 1440) =>
            new SceneOutputFrame(owner, (ulong)camera, sequence, frame, generation,
                renderWidth, renderHeight, outputWidth, outputHeight);

        [Test]
        public void DefaultTokenCannotSubmitEvenAgainstAnotherDefaultToken()
        {
            SceneOutputFrame empty = default;
            Assert.That(empty.Matches(in empty), Is.False);
            var adapter = new ReduxSceneOutput();
            Assert.That(adapter.Submit(in empty, null, out string reason), Is.False);
            Assert.That(reason, Does.Contain("expired frame or graph"));
            adapter.Dispose();
            adapter.Dispose();
        }

        [Test]
        public void DiagnosticReadPreservesRuntimeFailureAndCannotExposeAnUnclaimedOutput()
        {
            using (var adapter = new ReduxSceneOutput())
            {
                SceneOutputFrame expired = default;
                Assert.That(adapter.Submit(in expired, null, out string failure), Is.False);
                Assert.That(adapter.TryGetSubmittedOutput(null, out RenderTexture output, out SceneOutputFrame frame), Is.False);
                Assert.That(output, Is.Null);
                Assert.That(frame.OwnerId, Is.Zero);
                Assert.That(frame.Sequence, Is.Zero);
                Assert.That(adapter.FailureReason, Is.EqualTo(failure));
                Assert.That(adapter.ReacquisitionPending, Is.False);
            }
        }

        [Test]
        public void SameDimensionsDoNotMakeAStaleOrForeignSubmissionCurrent()
        {
            var current = Frame();
            Assert.That(current.Matches(in current), Is.True);
            var expired = new[]
            {
                Frame(owner: 9), Frame(camera: 9), Frame(sequence: 9),
                Frame(frame: 9), Frame(generation: 9)
            };
            foreach (var token in expired)
                Assert.That(token.Matches(in current), Is.False);
        }

        [Test]
        public void ResizeOrRenderExtentChangeInvalidatesTheWholeSubmission()
        {
            var current = Frame();
            var changed = new[]
            {
                Frame(renderWidth: 1279), Frame(renderHeight: 719),
                Frame(outputWidth: 2559), Frame(outputHeight: 1439)
            };
            foreach (var token in changed)
                Assert.That(token.Matches(in current), Is.False);
        }

        [Test]
        public void PresenterRebuildMovesPresentationBeforeBlurWithoutReorderingOtherConsumers()
        {
            var go = new GameObject("scene-output-buffer-order");
            var camera = go.AddComponent<Camera>();
            var blur = new CommandBuffer { name = "Blur Screen for UI" };
            var capture = new CommandBuffer { name = "Other capture" };
            var present = new CommandBuffer { name = "Present Render Scale Target" };
            try
            {
                camera.AddCommandBuffer(CameraEvent.AfterEverything, blur);
                camera.AddCommandBuffer(CameraEvent.AfterEverything, capture);
                camera.AddCommandBuffer(CameraEvent.AfterEverything, present);
                ReduxSceneOutput.PutPresentationFirst(camera, present);
                AssertNames(camera, "Present Render Scale Target", "Blur Screen for UI", "Other capture");
                ReduxSceneOutput.PutPresentationFirst(camera, present);
                AssertNames(camera, "Present Render Scale Target", "Blur Screen for UI", "Other capture");
                camera.RemoveCommandBuffer(CameraEvent.AfterEverything, present);
                camera.AddCommandBuffer(CameraEvent.AfterEverything, present);
                ReduxSceneOutput.PutPresentationFirst(camera, present);
                AssertNames(camera, "Present Render Scale Target", "Blur Screen for UI", "Other capture");
            }
            finally
            {
                camera.RemoveAllCommandBuffers();
                blur.Release(); capture.Release(); present.Release();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MissingPresentationBufferDoesNotAcquireOtherConsumers()
        {
            var go = new GameObject("scene-output-foreign-buffer");
            var camera = go.AddComponent<Camera>();
            var blur = new CommandBuffer { name = "Blur Screen for UI" };
            // Names are diagnostic labels, never ownership identities.
            var foreign = new CommandBuffer { name = "Blur Screen for UI" };
            try
            {
                camera.AddCommandBuffer(CameraEvent.AfterEverything, blur);
                ReduxSceneOutput.PutPresentationFirst(camera, foreign);
                AssertNames(camera, "Blur Screen for UI");
            }
            finally
            {
                camera.RemoveAllCommandBuffers();
                blur.Release(); foreign.Release();
                Object.DestroyImmediate(go);
            }
        }

        private static void AssertNames(Camera camera, params string[] expected)
        {
            var buffers = camera.GetCommandBuffers(CameraEvent.AfterEverything);
            Assert.That(buffers.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++) Assert.That(buffers[i].name, Is.EqualTo(expected[i]));
        }
    }
}
