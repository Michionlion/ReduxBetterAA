using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class BetaDiagnosticsTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void IssueMetadataIsReplacedAfterTheCapturedResolveAndDoesNotRelabelAnOldMatrix(bool freshMatrix)
        {
            string directory = Path.Combine(Path.GetTempPath(), "betteraa-provenance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var gameObject = new GameObject("issue-provenance-camera");
            TemporalCoordinator previousCoordinator = TemporalCoordinator.Current;
            try
            {
                TemporalCoordinator.Current = null;
                var camera = gameObject.AddComponent<Camera>();
                camera.depthTextureMode = DepthTextureMode.None;
                var manifest = new IssueReportManifest();
                // This test drives the actual input -> resolve -> output callbacks,
                // without starting Addressables, a coroutine or GPU readbacks.
                var capture = (IssueReportCapture)FormatterServices.GetUninitializedObject(typeof(IssueReportCapture));
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string name, object value) => typeof(IssueReportCapture).GetField(name, fields).SetValue(capture, value);
                int sampledFrame = Time.frameCount - 1;
                int matrixFrame = sampledFrame;
                string backend = "Off";
                int reportCalls = 0;
                Set("_camera", camera);
                Set("_manifest", manifest);
                Set("_directory", directory);
                Set("_expectsTemporalInput", true);
                Set("_writer", new BufferImageWriter(directory, null, manifest));
                Set("_report", new Func<Camera, Phase1Report>(observed =>
                {
                    Assert.That(observed, Is.SameAs(camera));
                    reportCalls++;
                    return new Phase1Report
                    {
                        frame = sampledFrame,
                        temporal = new TemporalBackendRecord
                        {
                            selectedBackend = backend,
                            motionMatrix = new MotionMatrixRecord { frame = matrixFrame }
                        }
                    };
                }));
                typeof(IssueReportCapture).GetMethod("WriteCapabilityReport", fields)
                    .Invoke(capture, new object[] { "request-before-camera-render" });
                Assert.That(manifest.capabilitiesStage, Is.EqualTo("request-before-camera-render"));
                Assert.That(manifest.capabilitiesMatchOutput, Is.False);
                Assert.That(manifest.motionMatrixMatchesOutput, Is.False);
                string file = Path.Combine(directory, "capabilities.json");
                Assert.That(JsonUtility.FromJson<Phase1Report>(File.ReadAllText(file)).temporal.selectedBackend, Is.EqualTo("Off"));

                var hook = gameObject.AddComponent<TemporalRenderHook>();
                hook.CaptureInput = capture.CaptureInput;
                hook.Owner = new MetadataResolve(() =>
                {
                    Assert.That(manifest.inputFrame, Is.EqualTo(Time.frameCount));
                    sampledFrame = Time.frameCount;
                    matrixFrame = freshMatrix ? sampledFrame : sampledFrame - 1;
                    backend = "NvidiaDlaa";
                    capture.CaptureOutput(null);
                });
                hook.Render(null, null);

                Phase1Report resolved = JsonUtility.FromJson<Phase1Report>(File.ReadAllText(file));
                Assert.That(reportCalls, Is.EqualTo(2));
                Assert.That(resolved.frame, Is.EqualTo(manifest.outputFrame));
                Assert.That(resolved.captureStage, Is.EqualTo("after-temporal-resolve"));
                Assert.That(resolved.temporal.selectedBackend, Is.EqualTo("NvidiaDlaa"));
                Assert.That(resolved.temporal.motionMatrix.frame, Is.EqualTo(matrixFrame));
                Assert.That(manifest.capabilitiesMatchOutput, Is.True);
                Assert.That(manifest.motionMatrixMatchesOutput, Is.EqualTo(freshMatrix));
                Assert.That(manifest.motionMatrixFrame, Is.EqualTo(matrixFrame));
                Assert.That(manifest.errors, Is.Empty);
            }
            finally
            {
                TemporalCoordinator.Current = previousCoordinator;
                UnityEngine.Object.DestroyImmediate(gameObject);
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void CapturedInputDimensionsRemainDistinctFromAStaleCpuGlobalVector()
        {
            var texture = new Texture2D(32, 16, TextureFormat.RGBA32, false);
            try
            {
                var global = new Vector4(1f / 512, -1f / 512, 512, 512);
                InputBindingRecord record = IssueReportCapture.CaptureBinding("_CameraDepthTexture", texture, true, global, 123);
                Assert.That(record.frame, Is.EqualTo(123));
                Assert.That(record.width, Is.EqualTo(32));
                Assert.That(record.height, Is.EqualTo(16));
                Assert.That(record.cpuGlobalTexelSize, Is.EqualTo(new[] { global.x, global.y, 512f, 512f }));
                Assert.That(record.cpuGlobalDimensionsMatchTexture, Is.False);
                StringAssert.Contains("separate observation", record.provenance);
                InputBindingRecord missing = IssueReportCapture.CaptureBinding("_CameraDepthTexture", null, false, global, 124);
                Assert.That(missing.width, Is.Zero);
                Assert.That(missing.cpuGlobalDimensionsMatchTexture, Is.False);
                Assert.That(missing.requestedByCamera, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        private sealed class MetadataResolve : ISceneResolve
        {
            private readonly Action _render;
            public MetadataResolve(Action render) { _render = render; }
            public bool Active => true;
            public void Render(RenderTexture source, RenderTexture destination) => _render();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MotionSignReportUsesTheCaptureCameraIndependentlyOfTheSelectedDebugCamera(bool cameraAvailable)
        {
            var selectedObject = new GameObject("selected-debug-camera");
            var captureObject = new GameObject("issue-capture-camera");
            var visualizer = new BufferVisualizer(null);
            try
            {
                var selected = selectedObject.AddComponent<Camera>();
                var capture = captureObject.AddComponent<Camera>();
                capture.forceIntoRenderTexture = true;
                capture.nonJitteredProjectionMatrix = Matrix4x4.Scale(new Vector3(2f, 3f, 1f));
                visualizer.SetCandidates(new[] { selected });

                MotionSignDiagnosticRecord report = visualizer.CaptureMotionSignDiagnostic(
                    cameraAvailable ? capture : null);

                Assert.That(visualizer.SelectedCameraForDiagnostics, Is.SameAs(selected));
                Assert.That(report.cameraAvailable, Is.EqualTo(cameraAvailable));
                Assert.That(report.selectedCamera, Is.EqualTo(cameraAvailable ? capture.name : "NoCamera"));
                Assert.That(report.automaticReferenceUsesRenderTextureProjection, Is.EqualTo(cameraAvailable));
                Assert.That(report.cameraProjectionYScale, Is.EqualTo(cameraAvailable ? 3f : 0f));
                Assert.That(report.motionTextureTexelSize, Has.Length.EqualTo(4));
            }
            finally
            {
                visualizer.Dispose();
                UnityEngine.Object.DestroyImmediate(captureObject);
                UnityEngine.Object.DestroyImmediate(selectedObject);
            }
        }

        [TestCase(false, DepthTextureMode.None)]
        [TestCase(true, DepthTextureMode.None)]
        [TestCase(true, DepthTextureMode.MotionVectors)]
        [TestCase(true, DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors)]
        public void ClosingDebugViewRestoresOnlyDepthFlagsItStillOwns(bool changed, DepthTextureMode replacement)
        {
            var gameObject = new GameObject("diagnostic-depth-owner");
            var visualizer = new BufferVisualizer(null);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo materialField = typeof(BufferVisualizer).GetField("_material", fields);
            void DisposeVisualizer()
            {
                UnityEngine.Object.DestroyImmediate(materialField.GetValue(visualizer) as Material);
                materialField.SetValue(visualizer, null);
                visualizer.Dispose();
            }
            try
            {
                var camera = gameObject.AddComponent<Camera>();
                camera.depthTextureMode = DepthTextureMode.DepthNormals;
                typeof(BufferVisualizer).GetField("_shader", fields).SetValue(visualizer,
                    UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                        "Assets/ReduxBetterAA/Shaders/Phase1BufferDebug.shader"));
                typeof(BufferVisualizer).GetField("_view", fields).SetValue(visualizer, BufferDebugView.LinearDepth);
                visualizer.SetCandidates(new[] { camera });
                Assert.That(camera.depthTextureMode, Is.EqualTo(DepthTextureMode.Depth | DepthTextureMode.DepthNormals));
                Assert.That(camera.commandBufferCount, Is.GreaterThan(0));

                if (changed) camera.depthTextureMode = replacement;
                DepthTextureMode expected = changed ? camera.depthTextureMode : DepthTextureMode.DepthNormals;
                DisposeVisualizer();
                DisposeVisualizer();

                Assert.That(camera.depthTextureMode, Is.EqualTo(expected));
                Assert.That(camera.commandBufferCount, Is.Zero);
            }
            finally
            {
                DisposeVisualizer();
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
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
        [TestCase("6000.6.0f1", true)]
        [TestCase("6000.6.1f1", false)]
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
