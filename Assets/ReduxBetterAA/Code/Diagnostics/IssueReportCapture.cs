using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ReduxBetterAA.Diagnostics
{
    // Explicit one-shot lifecycle: arm camera -> input/output -> end-of-frame UI -> zip.
    // No texture enumeration, GPU readback or compression runs while idle.
    internal sealed class IssueReportCapture : IDisposable
    {
        internal const string ShaderAddress = "Assets/ReduxBetterAA/Shaders/IssueBufferCapture.shader";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly MonoBehaviour _host;
        private readonly Func<Camera, Phase1Report> _report;
        private readonly Action<string> _notify;
        private readonly Action<bool> _suspendPanel;
        private readonly DiagnosticLogBuffer _logs = new DiagnosticLogBuffer();
        private AsyncOperationHandle<Shader> _shader;
        private Material _material;
        private Camera _camera;
        private TemporalRenderHook _resolveHook;
        private bool _expectsTemporalInput;
        private IssueOutputCaptureHook _outputHook;
        private IssueReportManifest _manifest;
        private BufferImageWriter _writer;
        private Coroutine _coroutine;
        private Task<string> _archive;
        private string _directory;
        private float _deadline;
        private bool _disposed;

        internal bool Busy => _manifest != null;
        internal int TemporalInputCaptureCount { get; private set; }
        internal string LastZipPath { get; private set; }
        internal string Status { get; private set; } = "No issue report generated this session.";

        public IssueReportCapture(MonoBehaviour host, Func<Camera, Phase1Report> report,
            Action<string> notify, Action<bool> suspendPanel)
        {
            _host = host;
            _report = report;
            _notify = notify;
            _suspendPanel = suspendPanel;
            _shader = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
        }

        public bool Request()
        {
            if (_disposed || Busy)
                return false;
            try
            {
                string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                _directory = Path.Combine(Phase1ProbeService.GetReportDirectory(), "reports", "betteraa-" + id);
                Directory.CreateDirectory(_directory);
                _camera = TemporalCoordinator.Current?.ResolveCamera;
                _manifest = new IssueReportManifest
                {
                    id = id, capturedUtc = DateTime.UtcNow.ToString("O"),
                    camera = _camera == null ? "Unavailable" : _camera.name,
                    status = "capturing"
                };
                if (_material == null && _shader.IsDone &&
                    _shader.Status == AsyncOperationStatus.Succeeded && _shader.Result.isSupported)
                    _material = new Material(_shader.Result) { hideFlags = HideFlags.HideAndDontSave };
                _writer = new BufferImageWriter(_directory, _material, _manifest);
                WriteCapabilityReport();
                if (_camera != null && _camera.isActiveAndEnabled)
                {
                    // Destroy is deferred; a disabled hook from a previous mode can
                    // still be the first component until the end of this frame.
                    _resolveHook = null;
                    foreach (var hook in _camera.GetComponents<TemporalRenderHook>())
                        if (hook.enabled && hook.Owner != null && hook.Owner.Active)
                            _resolveHook = hook;
                    _expectsTemporalInput = _resolveHook != null;
                    _manifest.inputStage = _expectsTemporalInput ? "before-temporal-resolve" : "after-ppv2";
                    if (_expectsTemporalInput)
                        _resolveHook.CaptureInput = CaptureInput;
                    _outputHook = _camera.gameObject.AddComponent<IssueOutputCaptureHook>();
                    _outputHook.hideFlags = HideFlags.HideAndDontSave;
                    _outputHook.Owner = this;
                }
                else
                    _manifest.errors.Add("No active resolve camera; renderer buffers are unavailable.");
                SetStatus("Capturing issue report. The game may pause briefly while buffers are read.");
                _suspendPanel(true);
                _deadline = Time.realtimeSinceStartup + 10f;
                _coroutine = _host.StartCoroutine(FinishCapture());
                return true;
            }
            catch (Exception exception)
            {
                Detach();
                _suspendPanel(false);
                _manifest = null;
                SetStatus("Issue report could not start: " + exception.GetType().Name);
                return false;
            }
        }

        internal void CaptureInput(RenderTexture source)
        {
            if (!Busy || _manifest.inputFrame >= 0)
                return;
            try
            {
                _manifest.inputFrame = Time.frameCount;
                if (_expectsTemporalInput) TemporalInputCaptureCount++;
                _writer.Capture("scene-input", source);
                // Do not enable missing flags just to obtain a nicer diagnostic.
                // A global texture without a request on this camera can belong to another camera.
                bool depth = (_camera.depthTextureMode & DepthTextureMode.Depth) != 0;
                bool motion = (_camera.depthTextureMode & DepthTextureMode.MotionVectors) != 0;
                _writer.Capture("depth-device", depth ? Shader.GetGlobalTexture("_CameraDepthTexture") : null,
                    1, "Resolve camera did not request depth; global ownership is unproven");
                _writer.Capture("motion-raw", motion ? Shader.GetGlobalTexture("_CameraMotionVectorsTexture") : null,
                    2, "Resolve camera did not request motion vectors; global ownership is unproven");
            }
            catch (Exception exception) { RecordFailure("Input capture", exception); }
        }

        internal void CaptureOutput(RenderTexture source)
        {
            if (!Busy || _manifest.outputFrame >= 0)
                return;
            try
            {
                if (!_expectsTemporalInput)
                    CaptureInput(source);
                _manifest.outputFrame = Time.frameCount;
                _writer.Capture("scene-output", source);
                TemporalCoordinator coordinator = TemporalCoordinator.Current;
                if (coordinator != null)
                {
                    foreach (object owner in coordinator.CaptureBufferOwners())
                        CaptureOwnedTextures(owner, owner.GetType().Name);
                }
                Component cloud = _camera.GetComponent("VolumeCloudRenderer");
                if (cloud != null)
                    CaptureOwnedTextures(cloud, "cloud");
                else
                    _writer.Capture("cloud", null, 0, "Resolve camera has no stock VolumeCloudRenderer");
                WriteCapabilityReport();
            }
            catch (Exception exception) { RecordFailure("Output capture", exception); }
        }

        private void CaptureOwnedTextures(object owner, string prefix)
        {
            // Deliberately bounded to declared RenderTexture fields, including null ones.
            // No graph traversal, vendor API reflection or game-wide texture search.
            foreach (FieldInfo field in owner.GetType().GetFields(Fields))
            {
                if (field.FieldType != typeof(RenderTexture))
                    continue;
                string name = prefix + "-" + field.Name.TrimStart('_');
                try
                {
                    var texture = field.GetValue(owner) as RenderTexture;
                    int preview = name.IndexOf("motion", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                        name.IndexOf("depth", StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 0;
                    _writer.Capture(name, texture, preview);
                    if (prefix == "cloud" && texture != null)
                        _writer.Capture(name + "-alpha", texture, 4);
                }
                catch (Exception exception) { RecordFailure(name, exception); }
            }
        }

        private IEnumerator FinishCapture()
        {
            do
            {
                yield return new WaitForEndOfFrame();
            }
            while (_camera != null && _manifest.outputFrame < 0 && Time.realtimeSinceStartup < _deadline);
            try
            {
                Texture2D screenshot = null;
                try
                {
                    screenshot = ScreenCapture.CaptureScreenshotAsTexture();
                    File.WriteAllBytes(Path.Combine(_directory, "presented.png"), screenshot.EncodeToPNG());
                    _manifest.screenshotFrame = Time.frameCount;
                }
                finally
                {
                    if (screenshot != null)
                        UnityEngine.Object.Destroy(screenshot);
                }
                if (_manifest.inputFrame < 0 || _manifest.outputFrame < 0)
                    _manifest.errors.Add("Camera capture timed out or no resolve camera was available.");
                if (_manifest.inputFrame != _manifest.outputFrame)
                    _manifest.errors.Add("Input and output were not captured in the same frame.");
                if (_manifest.outputFrame != _manifest.screenshotFrame)
                    _manifest.errors.Add("Presented screenshot is from a later frame; inspect frame numbers.");
            }
            catch (Exception exception) { RecordFailure("Presented screenshot", exception); }
            finally
            {
                Detach();
                _suspendPanel(false);
            }
            BeginArchive();
        }

        private void BeginArchive()
        {
            _manifest.status = _manifest.errors.Count == 0 &&
                !_manifest.buffers.Exists(buffer => buffer.status == "failed") ? "complete" : "partial";
            try { File.WriteAllText(Path.Combine(_directory, "betteraa-log.txt"), _logs.Export()); }
            catch (Exception exception) { RecordFailure("Log export", exception); _manifest.status = "partial"; }
            SetStatus("Compressing issue report...");
            string directory = _directory;
            IssueReportManifest manifest = _manifest;
            _archive = Task.Run(() => IssueReportArchive.Create(directory, manifest));
            _coroutine = null;
        }

        public void Tick()
        {
            // End-of-frame coroutines can stall when rendering stops (for example minimized).
            if (_coroutine != null && Time.realtimeSinceStartup >= _deadline)
            {
                _host.StopCoroutine(_coroutine);
                Detach();
                _suspendPanel(false);
                _manifest.errors.Add("Rendering stopped before capture completed; available files retained.");
                BeginArchive();
            }
            if (_archive == null || !_archive.IsCompleted)
                return;
            if (_archive.IsFaulted)
            {
                // Observe the exception and retain the capture directory for recovery.
                SetStatus("ZIP creation failed (" + _archive.Exception.GetBaseException().GetType().Name +
                    "); uncompressed report retained in " + _directory);
            }
            else
            {
                LastZipPath = _archive.Result;
                SetStatus((_manifest.status == "partial" ? "Partial report saved: " : "Report saved: ") + LastZipPath);
                // Keep the unpacked capture for debugging; it is never swept into a later report.
            }
            _archive = null;
            _manifest = null;
            _writer = null;
            _camera = null;
        }

        private void WriteCapabilityReport()
        {
            File.WriteAllText(Path.Combine(_directory, "capabilities.json"),
                JsonConvert.SerializeObject(_report(_camera), Formatting.Indented));
        }

        private void RecordFailure(string operation, Exception exception)
        {
            _manifest.errors.Add(operation + ": " + exception.GetType().Name);
        }

        private void SetStatus(string status)
        {
            Status = status;
            _notify(status);
        }

        private void Detach()
        {
            if (_resolveHook != null) _resolveHook.CaptureInput = null;
            if (_outputHook != null) { _outputHook.Owner = null; _outputHook.enabled = false; UnityEngine.Object.Destroy(_outputHook); }
            _resolveHook = null;
            _outputHook = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _logs.Dispose();
            if (_coroutine != null)
                _host.StopCoroutine(_coroutine);
            Detach();
            _suspendPanel(false);
            if (_manifest != null && _archive == null)
            {
                _manifest.status = "partial";
                _manifest.errors.Add("Mod unloaded before capture completed.");
                string directory = _directory;
                IssueReportManifest manifest = _manifest;
                _archive = Task.Run(() => IssueReportArchive.Create(directory, manifest));
            }
            if (_archive != null)
                _archive.ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            if (_material != null)
                UnityEngine.Object.Destroy(_material);
            Addressables.Release(_shader);
            // An already-started archive owns only files and its manifest, never Unity objects.
        }
    }
}
