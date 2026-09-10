using System;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class BetaDiagnosticsTests
    {
        [Test]
        public void CloudGuardResumesAfterExactly120SettledObservations()
        {
            var guard = new CloudTemporalGuard(enableSuspension: true);
            Assert.That(guard.Observe(1280, 720, false, 2560, 1440), Is.False);
            Assert.That(guard.Observe(640, 360, true, 2560, 1440), Is.True);
            for (int frame = 1; frame < 120; frame++)
            {
                Assert.That(guard.Observe(640, 360, false, 2560, 1440), Is.False);
                Assert.That(guard.BypassActive, Is.True);
            }
            Assert.That(guard.Observe(640, 360, false, 2560, 1440), Is.True);
            Assert.That(guard.BypassActive, Is.False);
            Assert.That(guard.SettleFramesRemaining, Is.Zero);
            Assert.That(guard.ResizeCount, Is.EqualTo(1));
        }

        [Test]
        public void CloudGuardRestartsOnTusAndClearsAtFullResolution()
        {
            var guard = new CloudTemporalGuard(enableSuspension: true);
            guard.Observe(640, 360, true, 2560, 1440);
            guard.Observe(640, 360, false, 2560, 1440);
            guard.Observe(640, 360, true, 2560, 1440);
            Assert.That(guard.SettleFramesRemaining, Is.EqualTo(120));
            Assert.That(guard.Observe(1280, 720, false, 2560, 1440), Is.True);
            Assert.That(guard.BypassActive, Is.False);
            guard.Clear();
            guard.Clear();
            Assert.That(guard.ResizeCount, Is.Zero);
            Assert.That(guard.Width, Is.Zero);
        }

        [Test]
        public void CloudGuardIgnoresInvalidDimensionsAndSettlesInitialQuarterResolution()
        {
            var guard = new CloudTemporalGuard(enableSuspension: true);
            Assert.That(guard.Observe(0, -1, true, 2560, 1440), Is.False);
            Assert.That(guard.Width, Is.Zero);
            Assert.That(guard.Observe(640, 360, false, 2560, 1440), Is.True);
            Assert.That(guard.SettleFramesRemaining, Is.EqualTo(120));
        }

        [Test]
        public void DisabledCloudGuardNeverSuspendsDuringPersistentTusOrResize()
        {
            var guard = new CloudTemporalGuard(enableSuspension: false);
            guard.Observe(1280, 720, false, 2560, 1440);
            for (int frame = 0; frame < 1000; frame++)
            {
                Assert.That(guard.Observe(640, 360, true, 2560, 1440), Is.False);
                Assert.That(guard.BypassActive, Is.False);
                Assert.That(guard.SettleFramesRemaining, Is.Zero);
            }
            Assert.That(guard.Observe(640, 360, false, 2560, 1440), Is.False);
            Assert.That(guard.Width, Is.EqualTo(640));
            Assert.That(guard.Height, Is.EqualTo(360));
            Assert.That(guard.ResizeCount, Is.EqualTo(1));
            guard.Clear();
            Assert.That(guard.BypassActive, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProjectionOwnershipRestoresExactStateAndDoesNotDoubleJitter(bool orthographic)
        {
            var gameObject = new GameObject("projection-test");
            var camera = gameObject.AddComponent<Camera>();
            camera.orthographic = orthographic;
            Matrix4x4 original = camera.projectionMatrix;
            Matrix4x4 nonJittered = Matrix4x4.Scale(new Vector3(2, 3, 4));
            camera.nonJitteredProjectionMatrix = nonJittered;
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
            var state = new CameraProjectionState();
            try
            {
                state.Apply(camera, new Vector2(0.25f, -0.25f));
                Matrix4x4 applied = camera.projectionMatrix;
                state.Apply(camera, new Vector2(0.5f, 0.5f));
                Assert.That(camera.projectionMatrix, Is.EqualTo(applied));
                state.Restore();
                state.Restore();
                Assert.That(camera.projectionMatrix, Is.EqualTo(original));
                Assert.That(camera.nonJitteredProjectionMatrix, Is.EqualTo(nonJittered));
                Assert.That(camera.useJitteredProjectionMatrixForTransparentRendering, Is.True);
            }
            finally { state.Restore(); UnityEngine.Object.DestroyImmediate(gameObject); }
        }

        [TestCase("6000.4.1f1", true)]
        [TestCase("6000.5.8f1", true)]
        [TestCase("6000.4.2f1", false)]
        [TestCase(null, false)]
        public void FoliageShaderIsRestrictedToItsExactEngine(string version, bool expected)
        {
            Assert.That(VegetationMotionCompatibility.SupportsUnityVersion(version), Is.EqualTo(expected));
        }

        [Test]
        public void CaptureExportsDistinctTexturesAndRestoresTheRenderTarget()
        {
            string directory = Path.Combine(Path.GetTempPath(), "betteraa-images-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var material = new Material(UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(IssueReportCapture.ShaderAddress));
            var source = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat);
            var preview = new Texture2D(2, 2);
            RenderTexture previous = RenderTexture.active;
            try
            {
                source.Create();
                var manifest = new IssueReportManifest();
                var writer = new BufferImageWriter(directory, material, manifest);
                RenderTexture.active = source;
                GL.Clear(false, true, Color.red);
                writer.Capture("red", source);
                Assert.That(RenderTexture.active, Is.SameAs(source));
                GL.Clear(false, true, Color.green);
                writer.Capture("green", source);
                foreach (var buffer in manifest.buffers)
                    Assert.That(buffer.status, Is.EqualTo("captured"), buffer.error);
                Assert.That(IssueReportArchive.HashFile(Path.Combine(directory, "red.exr")),
                    Is.Not.EqualTo(IssueReportArchive.HashFile(Path.Combine(directory, "green.exr"))));
                preview.LoadImage(File.ReadAllBytes(Path.Combine(directory, "red.png")));
                Assert.That(preview.GetPixel(4, 4).r, Is.GreaterThan(0.99f));
                Assert.That(preview.GetPixel(4, 4).g, Is.LessThan(0.01f));
                preview.LoadImage(File.ReadAllBytes(Path.Combine(directory, "green.png")));
                Assert.That(preview.GetPixel(4, 4).g, Is.GreaterThan(0.99f));
                Assert.That(preview.GetPixel(4, 4).r, Is.LessThan(0.01f));
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(preview);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(material);
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ArchiveContainsOnlyThisReportAndVerifiableHashes()
        {
            string root = Path.Combine(Path.GetTempPath(), "betteraa-test-" + Guid.NewGuid().ToString("N"));
            string capture = Path.Combine(root, "capture");
            Directory.CreateDirectory(capture);
            try
            {
                File.WriteAllText(Path.Combine(root, "unrelated-save.json"), "private");
                File.WriteAllText(Path.Combine(capture, "capabilities.json"), "{}");
                var manifest = new IssueReportManifest { id = "test", status = "partial" };
                manifest.errors.Add("No scene camera");
                string path = IssueReportArchive.Create(capture, manifest);
                using (var stream = File.OpenRead(path))
                using (var archive = new ZipArchive(stream))
                {
                    Assert.That(archive.Entries.Count, Is.EqualTo(2));
                    Assert.That(archive.GetEntry("unrelated-save.json"), Is.Null);
                    using (var reader = new StreamReader(archive.GetEntry("manifest.json").Open()))
                    {
                        string json = reader.ReadToEnd();
                        StringAssert.Contains("\"status\": \"partial\"", json);
                        StringAssert.Contains(IssueReportArchive.HashFile(Path.Combine(capture, "capabilities.json")), json);
                    }
                }
                Assert.That(File.Exists(path + ".partial"), Is.False);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void ArchiveCollisionDoesNotDeleteAnExistingPartialFile()
        {
            string capture = Path.Combine(Path.GetTempPath(), "betteraa-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(capture);
            string partial = capture + ".zip.partial";
            try
            {
                File.WriteAllText(partial, "existing archive");
                Assert.Throws<IOException>(() => IssueReportArchive.Create(capture, new IssueReportManifest()));
                Assert.That(File.ReadAllText(partial), Is.EqualTo("existing archive"));
            }
            finally { Directory.Delete(capture, true); File.Delete(partial); }
        }
    }
}
