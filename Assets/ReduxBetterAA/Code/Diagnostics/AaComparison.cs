using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using KSP.Rendering;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.ResourceManagement.AsyncOperations;
using Logger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Diagnostics
{
    // Replay the real scene cameras after game LateUpdate, then present before UI.
    // Deliberately diagnostic: no cloned game behaviours, persistent settings, or
    // temporal filters chained onto another arm's image.
    [DefaultExecutionOrder(32000)]
    internal sealed class AaComparison : MonoBehaviour
    {
        internal const string ShaderAddress = "Assets/ReduxBetterAA/Shaders/AaComparison.shader";
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Sources = typeof(RenderScalePresenter).GetField("_sourceCameras", Private);
        private static readonly FieldInfo PresenterCamera = typeof(RenderScalePresenter).GetField("_presentCamera", Private);
        private readonly int[] _selection = { 5, 6 };
        private readonly int[] _scale = { 200, 200 };
        private readonly bool[] _open = new bool[2];
        private readonly Arm[] _arms = new Arm[2];
        private readonly HistoryResetTracker _reset = new HistoryResetTracker();
        private Logger _logger;
        private TemporalCoordinator _normal;
        private TemporalCameraSet _scene;
        private Camera[] _sources;
        private Camera _oldPresenter, _present;
        private bool[] _enabled;
        private bool _presentEnabled, _frameClaimed;
        private Material _material;
        private AsyncOperationHandle<Shader> _shader;
        private int _width, _height;
        private string _gameState, _failure;
        private uint _frame;
        internal string Status = "Choose each half, then start the live comparison.";
        internal bool Busy { get; private set; }
        internal bool Running => Busy && _present != null;
        internal float Split = .5f;
        internal uint RenderedFrames => _frame;
        internal uint HistoryResets { get; private set; }
        internal RenderTexture Output(int side) => _arms[side]?.Output;
        internal object Backend(int side) => _arms[side]?.Backend;

        internal void Initialize(Logger logger) { _logger = logger; }

        internal void DrawControls()
        {
            GUILayout.Label("Live A/B · full scene on each side · UI stays native");
            for (int i = 0; i < 2; i++)
            {
                _selection[i] = (int)DebugMenu.ModeDropdown(i == 0 ? "Left" : "Right", (BackendSelection)_selection[i], ref _open[i]);
                if (_selection[i] == 8)
                {
                    GUILayout.Label("Supersampling: " + _scale[i] + "% per dimension");
                    _scale[i] = Mathf.RoundToInt(GUILayout.HorizontalSlider(_scale[i], 100, MaximumScale(Screen.width, Screen.height, SystemInfo.maxTextureSize)) / 25) * 25;
                }
            }
            GUILayout.Label("Divider: " + (Split * 100).ToString("0") + "%");
            Split = GUILayout.HorizontalSlider(Split, 0, 1);
            if (GUILayout.Button(Busy ? "Apply selections / restart" : "Start live comparison", GUILayout.Height(30)))
                StartComparison(_selection[0], _selection[1], _scale[0], _scale[1]);
            if (Busy && GUILayout.Button("Stop comparison")) Stop();
            GUILayout.Label(Status);
            GUILayout.Label("Move the camera normally. Close F10 to inspect the view. This mode renders twice and is only for visual quality; settings are applied on Start / Apply.");
        }

        internal static int MaximumScale(int width, int height, int textureLimit) =>
            Mathf.Clamp(textureLimit * 100 / Mathf.Max(1, Mathf.Max(width, height)) / 25 * 25, 100, 400);

        internal void StartComparison(int left, int right, int leftScale = 200, int rightScale = 200)
        {
            Stop();
            if (left < 0 || left > (int)BackendSelection.Supersampling || right < 0 || right > (int)BackendSelection.Supersampling) { Status = "Invalid comparison mode."; return; }
            _selection[0] = (int)UserSettingsPolicy.NormalizeBackend((BackendSelection)left);
            _selection[1] = (int)UserSettingsPolicy.NormalizeBackend((BackendSelection)right);
            _scale[0] = leftScale; _scale[1] = rightScale;
            Busy = true;
            StartCoroutine(GuardStart());
        }

        private IEnumerator GuardStart()
        {
            IEnumerator start = StartLive();
            while (Busy)
            {
                object step = null;
                bool next = false;
                string failure = null;
                try { next = start.MoveNext(); if (next) step = start.Current; }
                catch (Exception e) { failure = e.Message; }
                if (failure != null) { Fail(failure); yield break; }
                if (!next) yield break;
                yield return step;
            }
        }

        private IEnumerator StartLive()
        {
            // Start outside OnGUI and after outstanding normal render callbacks.
            yield return null;
            _normal = TemporalCoordinator.Current;
            _scene = TemporalCameraDiscovery.Discover();
            if (_normal == null || _scene.ResolveCamera == null || _scene.ResolveLayer == null) { Fail("No active scene camera / post-process layer."); yield break; }
            var presenter = Resources.FindObjectsOfTypeAll<RenderScalePresenter>().FirstOrDefault(p =>
                Sources?.GetValue(p) is Camera[] cameras && cameras.Contains(_scene.ResolveCamera));
            if (presenter == null) { Fail("This scene has no verified render-scale camera stack."); yield break; }
            _sources = ((Camera[])Sources.GetValue(presenter)).Where(c => c != null).OrderBy(c => c.depth).ToArray();
            _oldPresenter = (Camera)PresenterCamera.GetValue(presenter);
            _enabled = new bool[_sources.Length];
            _width = Screen.width; _height = Screen.height;
            _gameState = TemporalCameraDiscovery.ReadGameState();
            _normal.SuspendForComparison(true);
            _normal.ComparisonReset += ResetHistory;
            // Native AA arms always receive native color/depth/motion. Only the
            // supersampling arm uses the larger target; no up/downsampled AA input.
            _scene.RenderScalePercent = 100;
            _shader = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
            for (int i = 0; i < 2; i++)
                _arms[i] = new Arm(_selection[i], _selection[i] == 8 ? Mathf.Clamp(_scale[i], 100, MaximumScale(_width, _height, SystemInfo.maxTextureSize)) : 100, _logger, _normal, reason => _failure = reason);
            float deadline = Time.realtimeSinceStartup + 15;
            while (!_shader.IsDone || !_arms.All(a => a.Ready))
            {
                if (Time.realtimeSinceStartup > deadline) { Fail("AA resources did not load."); yield break; }
                yield return null;
            }
            if (_shader.Status != AsyncOperationStatus.Succeeded || _shader.Result == null) { Fail("Comparison shader unavailable."); yield break; }
            for (int i = 0; i < 2; i++)
            {
                if (!_arms[i].Configure(_scene, _width, _height, out string reason)) { Fail(DebugMenu.ModeName((BackendSelection)_selection[i]) + ": " + reason); yield break; }
            }
            _material = new Material(_shader.Result) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetTexture("_RightTex", _arms[1].Output);
            var go = new GameObject("AA comparison presentation") { hideFlags = HideFlags.HideAndDontSave };
            _present = go.AddComponent<Camera>();
            _present.CopyFrom(_scene.ResolveCamera);
            _present.targetTexture = null;
            _present.depth = _sources.Max(c => c.depth) + .005f;
            _present.clearFlags = CameraClearFlags.Nothing;
            _present.cullingMask = 0;
            _present.depthTextureMode = DepthTextureMode.None;
            _present.allowMSAA = false;
            _present.eventMask = 0;
            go.AddComponent<AaComparisonPresent>().Owner = this;
            Status = "Live: " + _arms[0].Label + " | " + _arms[1].Label;
            _logger.LogInfo("[ReduxBetterAA/Compare] " + Status + "; " + _width + "x" + _height);
            StartCoroutine(RestoreAtFrameEnd());
        }

        private void LateUpdate()
        {
            if (!Running) return;
            if (_failure != null) { Fail(_failure); return; }
            if (_scene.ResolveCamera == null || Screen.width != _width || Screen.height != _height ||
                TemporalCameraDiscovery.ReadGameState() != _gameState) { Stop(); Status = "Comparison stopped after scene or resolution change."; return; }
            try
            {
                RestoreCameraFlags();
                ResetHistory(_reset.Evaluate(_scene.ResolveCamera, 100, false));
                for (int i = 0; i < _sources.Length; i++)
                {
                    if (_sources[i] == null) throw new InvalidOperationException("Scene camera removed.");
                    _enabled[i] = _sources[i].enabled;
                }
                _presentEnabled = _oldPresenter != null && _oldPresenter.enabled;
                _frameClaimed = true;
                foreach (Camera camera in _sources) camera.enabled = false;
                if (_oldPresenter != null) _oldPresenter.enabled = false;
                foreach (Arm arm in _arms)
                {
                    arm.Select(_scene, true);
                    arm.Backend.Tick(_frame);
                    for (int i = 0; i < _sources.Length; i++)
                    {
                        Camera camera = _sources[i];
                        if (!_enabled[i] || !camera.gameObject.activeInHierarchy) continue;
                        RenderTexture target = camera.targetTexture;
                        try { camera.targetTexture = arm.Output; camera.Render(); }
                        finally { camera.targetTexture = target; }
                    }
                    arm.Select(_scene, false);
                }
                _frame++;
            }
            catch (Exception e) { Fail(e.Message); }
        }

        private IEnumerator RestoreAtFrameEnd()
        {
            var end = new WaitForEndOfFrame();
            while (Busy) { yield return end; RestoreCameraFlags(); }
        }

        private void RestoreCameraFlags()
        {
            if (!_frameClaimed) return;
            for (int i = 0; i < _sources.Length; i++) if (_sources[i] != null) _sources[i].enabled = _enabled[i];
            if (_oldPresenter != null) _oldPresenter.enabled = _presentEnabled;
            _frameClaimed = false;
        }

        internal void Present(RenderTexture destination)
        {
            _material.SetFloat("_Split", Split);
            _material.SetFloat("_OutputWidth", _width);
            Graphics.Blit(_arms[0].Output, destination, _material);
        }

        private void ResetHistory(HistoryResetReason reason)
        {
            if (reason == HistoryResetReason.None) return;
            HistoryResets++;
            foreach (Arm arm in _arms) arm?.Backend.ResetHistory(reason);
        }

        private void Fail(string reason) { Stop(); Status = "Comparison stopped: " + reason; _logger?.LogWarning("[ReduxBetterAA/Compare] " + Status); }

        internal void Stop()
        {
            StopAllCoroutines();
            RestoreCameraFlags();
            if (_present != null) { _present.enabled = false; Destroy(_present.gameObject); }
            _present = null;
            // Reverse claim order restores the original PPv2/depth state.
            for (int i = 1; i >= 0; i--) { _arms[i]?.Dispose(); _arms[i] = null; }
            if (_material != null) Destroy(_material);
            _material = null;
            if (_shader.IsValid()) Addressables.Release(_shader);
            _shader = default;
            if (_normal != null) { _normal.ComparisonReset -= ResetHistory; _normal.SuspendForComparison(false); }
            _normal = null; _scene = null; _failure = null; _frame = 0; HistoryResets = 0; _reset.Clear();
            Busy = false; Status = "Comparison stopped. Normal AA restored.";
        }

        private void OnDestroy() { Stop(); }

        private sealed class Arm : IDisposable
        {
            internal readonly ITemporalBackend Backend;
            private readonly MotionVectorSanitizer _motion;
            private readonly DepthDisocclusionMask _depth;
            private readonly int _mode, _scale;
            internal RenderTexture Output;
            internal string Label => DebugMenu.ModeName((BackendSelection)_mode) + " (" + _scale + "%, " + Output.width + "x" + Output.height + ")";
            internal bool Ready => _motion.Ready && _depth.Ready && (!(Backend is CustomTaaBackend custom) || custom.ShaderReady);

            internal Arm(int mode, int scale, Logger logger, TemporalCoordinator normal, Action<string> failure)
            {
                _mode = mode; _scale = scale;
                _motion = new MotionVectorSanitizer(logger, () => { });
                _depth = new DepthDisocclusionMask(logger, () => { });
                _motion.Initialize(); _depth.Initialize();
                var profiler = new BackendPerformanceProfiler();
                switch (mode)
                {
                    case 1: case 2: case 3:
                        Backend = new Ppv2SpatialAaBackend(DebugMenu.ModeName((BackendSelection)mode), mode == 3 ? PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing : PostProcessLayer.Antialiasing.FastApproximateAntialiasing, mode == 1); break;
                    case 5:
                        var taa = new CustomTaaBackend(logger, () => { }, profiler, _motion, failure);
                        taa.ApplyConfig(normal.CustomConfig); taa.Initialize(); Backend = taa; break;
                    case 6:
                        var dlaa = new NvidiaDlaaBackend(logger, failure, profiler, _motion, _depth);
                        dlaa.ApplyConfig(normal.DlaaConfig); dlaa.Initialize(); Backend = dlaa; break;
                    case 7:
                        var fsr = new AmdFsr2Backend(logger, failure, profiler, _motion, _depth);
                        fsr.ApplyConfig(normal.Fsr2Config); fsr.Initialize(); Backend = fsr; break;
                    default: Backend = new DisabledBackend(); break;
                }
                SetRendering(false);
            }

            internal bool Configure(TemporalCameraSet scene, int width, int height, out string reason)
            {
                if (!Backend.Configure(scene, out reason)) return false;
                Output = new RenderTexture(Mathf.CeilToInt(width * _scale / 100f), Mathf.CeilToInt(height * _scale / 100f), 24, RenderTextureFormat.DefaultHDR)
                { name = "AA comparison " + DebugMenu.ModeName((BackendSelection)_mode), filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                if (!Output.Create()) { reason = "Render target allocation failed."; return false; }
                Select(scene, false);
                return true;
            }

            internal void Select(TemporalCameraSet scene, bool enabled)
            {
                SetRendering(enabled);
                if (!enabled) return;
                scene.ResolveLayer.antialiasingMode = _mode == 3 ? PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing :
                    _mode == 1 || _mode == 2 ? PostProcessLayer.Antialiasing.FastApproximateAntialiasing : PostProcessLayer.Antialiasing.None;
                if (_mode == 1 || _mode == 2) scene.ResolveLayer.fastApproximateAntialiasing.fastMode = _mode == 1;
                if (scene.SharedJitterLayer != null && scene.SharedJitterLayer != scene.ResolveLayer)
                    scene.SharedJitterLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            }

            private void SetRendering(bool enabled)
            {
                if (Backend is CustomTaaBackend taa) taa.RenderEnabled = enabled;
                if (Backend is NvidiaDlaaBackend dlaa) dlaa.RenderEnabled = enabled;
                if (Backend is AmdFsr2Backend fsr) fsr.RenderEnabled = enabled;
            }

            public void Dispose()
            {
                Backend.Dispose(); _motion.Dispose(); _depth.Dispose();
                if (Output != null) { Output.Release(); Destroy(Output); Output = null; }
            }
        }
    }

    internal sealed class AaComparisonPresent : MonoBehaviour
    {
        internal AaComparison Owner;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Owner != null && Owner.Running) Owner.Present(destination);
            else Graphics.Blit(source, destination);
        }
    }
}
