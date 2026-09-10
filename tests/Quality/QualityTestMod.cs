using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MoonSharp.Interpreter;
using ReduxBetterAA;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using UnityEngine.EventSystems;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;
using ReduxTestHarness;
using SpaceWarp2.API.Mods;
using UnityEngine;

namespace ReduxBetterAAVisualTests
{
    // Test-only assembly, excluded from the production payload. Readbacks and
    // file IO are deliberately confined to quality samples, never timed windows.
    public sealed class QualityTestMod : MonoBehaviourMod
    {
        private IDisposable _registration;
        private ComparisonVideo _video;
        private Harmony _patch;
        private ReduxBetterAAMod _mod;
        private static string _directory, _label, _error;
        private static int _remaining, _index;
        private static RenderTexture _output;
        private static Texture2D _readback;
        private static QualityReferenceHook _reference;
        private static readonly double[] CpuSamples = new double[240], FrameSamples = new double[240];
        private static readonly Func<long> AllocatedBytes = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>),
            typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static));
        private static bool _timing;
        private static int _warmup, _samples;
        private static long _startTicks, _startBytes, _maxBytes, _totalBytes;
        private static readonly string[] Settings = {
            "_modeEntry", "_supersamplingEntry", "_sharpnessEntry", "_taaStabilityEntry", "_dlaaPresetEntry", "_mapViewAaEntry"
        };

        [Serializable] private sealed class Frame
        {
            public string label, requested, selected, gpu, unity, format, input, output;
            public int frame, index, width, height;
            public float jitterX, jitterY, sharpness;
            public bool active;
        }

        public override void OnInitialized()
        {
            _mod = UnityEngine.Object.FindAnyObjectByType<ReduxBetterAAMod>();
            _directory = Environment.GetEnvironmentVariable("RBAA_QUALITY_OUTPUT");
            if (string.IsNullOrEmpty(_directory)) throw new InvalidOperationException("Missing RBAA_QUALITY_OUTPUT");
            Directory.CreateDirectory(_directory);
            _patch = new Harmony("ReduxBetterAA.QualityTests");
            _patch.Patch(typeof(TemporalRenderHook).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic),
                prefix: new HarmonyMethod(typeof(QualityTestMod), nameof(Before)),
                postfix: new HarmonyMethod(typeof(QualityTestMod), nameof(After)));
            _registration = TestApiRegistry.Register("ReduxBetterAA.Quality", (script, api) =>
            {
                Bind(api, "settings", (c, a) => {
                    var t = new Table(script);
                    foreach (string name in Settings) t.Set(name, DynValue.FromObject(script, Entry(name).Value));
                    return DynValue.NewTable(t);
                });
                Bind(api, "set", (c, a) => {
                    foreach (string name in Settings) {
                        DynValue value = a[0].Table.Get(name);
                        if (!value.IsNil()) Entry(name).Value = Convert.ChangeType(value.ToObject(), Entry(name).Value.GetType());
                    }
                    return DynValue.Nil;
                });
                Bind(api, "panel_page", (c, a) => {
                    object visualizer = typeof(Phase1ProbeService).GetField("_visualizer", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Phase1ProbeService.Current);
                    visualizer.GetType().GetField("_page",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(visualizer,(int)a[0].Number);
                    if (a.Count>1) visualizer.GetType().GetField("_modeOpen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(visualizer,a[1].Boolean);
                    Phase1ProbeService.Current.SetPanelVisible(true);
                    return DynValue.Nil;
                });
                Bind(api, "video_start", (c, a) => {
                    if (_video != null) throw new InvalidOperationException("Video already running");
                    _video = gameObject.AddComponent<ComparisonVideo>();
                    _video.Begin(_mod.GetComponent<AaComparison>(), a[0].String);
                    return DynValue.Nil;
                });
                Bind(api, "shimmer_sweep", (c,a) => DynValue.NewBoolean(Environment.GetEnvironmentVariable("RBAA_SHIMMER_SWEEP") == "1"));
                Bind(api, "shimmer_jitter_sweep", (c,a) => DynValue.NewBoolean(Environment.GetEnvironmentVariable("RBAA_SHIMMER_JITTER_SWEEP") == "1"));
                Bind(api, "shimmer_path", (c,a) => DynValue.NewString(Environment.GetEnvironmentVariable("RBAA_SHIMMER_PATH") ?? "pan"));
                Bind(api, "shimmer_start", (c,a) => { gameObject.AddComponent<ShimmerCapture>().Begin(a[0].String); return DynValue.Nil; });
                Bind(api, "shimmer_ready", (c,a) => DynValue.NewBoolean(ShimmerCapture.Current.Done));
                Bind(api, "shimmer_stop", (c,a) => {
                    if (ShimmerCapture.Current != null) Destroy(ShimmerCapture.Current);
                    if (_reference != null) { Destroy(_reference); _reference=null; }
                    return DynValue.Nil;
                });
                Bind(api, "shimmer_config", (c,a) => {
                    var d=CustomTaaConfig.Conservative;
                    TemporalCoordinator.Current.SetCustomConfig(new CustomTaaConfig(a.Count>4?(float)a[4].Number:d.JitterSpread,a.Count>5?(int)a[5].Number:d.SequenceLength,.99f,
                        a.Count>0?(float)a[0].Number:d.MovingHistory,a.Count>3?(float)a[3].Number:d.MotionResponsePixels,d.MaximumMotionPixels,d.DepthThreshold,
                        a.Count>1?(float)a[1].Number:d.DepthEdgeStability,d.VarianceGamma,
                        a.Count>2?(float)a[2].Number:d.ReactiveScale,.24f,d.NoDepthHistory,CustomTaaDebugView.FinalResolve));
                    return DynValue.Nil;
                });
                Bind(api, "video_ready", (c, a) => DynValue.NewBoolean(_video != null && _video.Ready()));
                Bind(api, "video_finish", (c, a) => {
                    string path = _video.Finish(); Destroy(_video); _video = null; return DynValue.NewString(path);
                });
                Bind(api, "video_dispose", (c, a) => { if (_video != null) Destroy(_video); _video = null; return DynValue.Nil; });
                Bind(api, "compare", (c, a) => {
                    var compare = _mod.GetComponent<AaComparison>();
                    compare.StartComparison((int)a[0].Number, (int)a[1].Number,
                        a.Count > 2 ? (int)a[2].Number : 200, a.Count > 3 ? (int)a[3].Number : 200);
                    return DynValue.Nil;
                });
                Bind(api, "compare_stop", (c, a) => { _mod.GetComponent<AaComparison>().Stop(); return DynValue.Nil; });
                Bind(api, "compare_status", (c, a) => {
                    var compare = _mod.GetComponent<AaComparison>();
                    var t = new Table(script);
                    t.Set("running", DynValue.NewBoolean(compare.Running));
                    t.Set("busy", DynValue.NewBoolean(compare.Busy));
                    t.Set("status", DynValue.NewString(compare.Status));
                    t.Set("frames", DynValue.NewNumber(compare.RenderedFrames));
                    t.Set("resets", DynValue.NewNumber(compare.HistoryResets));
                    for (int i=0; i<2; i++) {
                        RenderTexture output = compare.Output(i);
                        t.Set(i == 0 ? "left_width" : "right_width", DynValue.NewNumber(output == null ? 0 : output.width));
                        t.Set(i == 0 ? "left_height" : "right_height", DynValue.NewNumber(output == null ? 0 : output.height));
                        object backend = compare.Backend(i);
                        var motionField = backend?.GetType().GetField("_motionVectorSanitizer",BindingFlags.Instance|BindingFlags.NonPublic);
                        if (motionField != null) {
                            var motionOwner = (MotionVectorSanitizer)motionField.GetValue(backend);
                            var snapshot = motionOwner.MatrixSnapshot;
                            string side = i == 0 ? "left" : "right";
                            t.Set(side+"_history_valid",DynValue.NewBoolean((bool)typeof(MotionVectorSanitizer).GetField("_matrixHistoryValid", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(motionOwner)));
                            if (backend is ReduxBetterAA.Backends.CustomTaaBackend) {
                                var material = (Material)backend.GetType().GetField("_material",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(backend);
                                t.Set(side+"_history_used",DynValue.NewBoolean(material != null && material.GetFloat("_HistoryValid") > .5f));
                            }
                            t.Set(side+"_unity_previous_error",DynValue.NewNumber(snapshot.UnityPreviousVsTrackedPreviousMaxAbs));
                            t.Set(side+"_tracked_motion",DynValue.NewNumber(snapshot.TrackedPreviousVsCurrentMaxAbs));
                        }
                        var context = backend?.GetType().GetProperty("ContextCreated");
                        t.Set(i == 0 ? "left_context" : "right_context", DynValue.NewBoolean(context != null && (bool)context.GetValue(backend)));
                    }
                    t.Set("independent", DynValue.NewBoolean(!ReferenceEquals(compare.Backend(0), compare.Backend(1)) && compare.Output(0) != compare.Output(1)));
                    return DynValue.NewTable(t);
                });
                Bind(api, "panel", (c, a) => {
                    Phase1ProbeService.Current.SetPanelVisible(a[0].Boolean);
                    return DynValue.NewBoolean(EventSystem.current != null && EventSystem.current.enabled);
                });
                Bind(api, "compare_capture", (c, a) => {
                    var compare = _mod.GetComponent<AaComparison>();
                    string label = a[0].String;
                    if (string.IsNullOrEmpty(label) || label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Exception("Invalid label");
                    var t = new Table(script);
                    RenderTexture prior = RenderTexture.active;
                    for(int i=0; i<2; i++) {
                        var small = RenderTexture.GetTemporary(256, 144, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                        var pixels = new Texture2D(256, 144, TextureFormat.RGBAFloat, false, true);
                        var preview = new Texture2D(256, 144, TextureFormat.RGBA32, false, true);
                        try {
                            Graphics.Blit(compare.Output(i), small); RenderTexture.active = small;
                            pixels.ReadPixels(new Rect(0,0,256,144),0,0); pixels.Apply();
                            Color[] colors = pixels.GetPixels(); double sum=0, square=0; int invalid=0;
                            foreach(Color color in colors) {
                                if(float.IsNaN(color.r) || float.IsInfinity(color.r) || float.IsNaN(color.g) || float.IsInfinity(color.g) || float.IsNaN(color.b) || float.IsInfinity(color.b)) invalid++;
                                double v=(color.r+color.g+color.b)/3; sum+=v; square+=v*v;
                            }
                            string side = i == 0 ? "left" : "right";
                            t.Set(side+"_invalid",DynValue.NewNumber(invalid));
                            t.Set(side+"_mean",DynValue.NewNumber(sum/colors.Length));
                            t.Set(side+"_variance",DynValue.NewNumber(square/colors.Length-Math.Pow(sum/colors.Length,2)));
                            object backend = compare.Backend(i);
                            var motionField = backend?.GetType().GetField("_motionVectorSanitizer", BindingFlags.Instance|BindingFlags.NonPublic);
                            if (motionField != null) {
                                Texture motion = ((MotionVectorSanitizer)motionField.GetValue(backend)).SanitizedTexture;
                                Graphics.Blit(motion,small); RenderTexture.active=small;
                                pixels.ReadPixels(new Rect(0,0,256,144),0,0); pixels.Apply();
                                double magnitude=0;
                                foreach(Color m in pixels.GetPixels()) magnitude += Math.Sqrt(m.r*m.r*compare.Output(i).width*compare.Output(i).width+m.g*m.g*compare.Output(i).height*compare.Output(i).height);
                                t.Set(side+"_motion_pixels",DynValue.NewNumber(magnitude/colors.Length));
                            }
                            preview.SetPixels(colors); preview.Apply();
                            File.WriteAllBytes(Path.Combine(_directory,label+"-"+side+".png"), ImageConversion.EncodeToPNG(preview));
                        } finally { RenderTexture.active=prior; RenderTexture.ReleaseTemporary(small); UnityEngine.Object.Destroy(pixels); UnityEngine.Object.Destroy(preview); }
                    }
                    return DynValue.NewTable(t);
                });
                Bind(api, "selected", (c, a) => DynValue.NewString(TemporalCoordinator.Current.SelectedBackend));
                Bind(api, "reference", (c, a) => {
                    if (TemporalCoordinator.Current.SelectedBackend != "Off") throw new InvalidOperationException("Reference requires Off");
                    foreach (Camera camera in Camera.allCameras) if (camera.name == "FlightCameraPhysics_Main") {
                        _reference = camera.gameObject.AddComponent<QualityReferenceHook>();
                        return DynValue.Nil;
                    }
                    throw new InvalidOperationException("Reference camera missing");
                });
                Bind(api, "capture", (c, a) => {
                    if (_remaining != 0) throw new InvalidOperationException("Capture already running");
                    _label = a[0].String;
                    if (string.IsNullOrEmpty(_label) || _label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new InvalidOperationException("Invalid capture label");
                    _index = 0; _error = null; _remaining = Math.Max(1, Math.Min(32, (int)a[1].Number));
                    return DynValue.Nil;
                });
                Bind(api, "ready", (c, a) => {
                    if (_error != null) throw new InvalidOperationException(_error);
                    return DynValue.NewBoolean(_remaining == 0);
                });
                Bind(api, "profile_start", (c, a) => {
                    if (_remaining != 0) throw new InvalidOperationException("Cannot profile during readback");
                    _timing = true; _warmup = 30; _samples = 0; _maxBytes = 0; _totalBytes = 0;
                    TemporalCoordinator.Current.StartPerformanceProfile(TemporalCoordinator.Current.RequestedBackend);
                    return DynValue.Nil;
                });
                Bind(api, "profile_ready", (c, a) => DynValue.NewBoolean(!_timing && !TemporalCoordinator.Current
                    .GetPerformanceProfile(TemporalCoordinator.Current.RequestedBackend).Running));
                Bind(api, "profile", (c, a) => {
                    object p = TemporalCoordinator.Current.GetPerformanceProfile(TemporalCoordinator.Current.RequestedBackend);
                    var t = new Table(script);
                    foreach (FieldInfo f in p.GetType().GetFields()) t.Set(f.Name, DynValue.FromObject(script,
                        f.FieldType.IsEnum ? f.GetValue(p).ToString() : f.GetValue(p)));
                    t.Set("ResolveManagedBytesTotal", DynValue.NewNumber(_totalBytes));
                    t.Set("ResolveManagedBytesMax", DynValue.NewNumber(_maxBytes));
                    t.Set("HookSamples", DynValue.NewNumber(_samples));
                    t.Set("HookCpuP50Ms", DynValue.NewNumber(Percentile(CpuSamples, .50)));
                    t.Set("HookCpuP95Ms", DynValue.NewNumber(Percentile(CpuSamples, .95)));
                    t.Set("HookCpuP99Ms", DynValue.NewNumber(Percentile(CpuSamples, .99)));
                    t.Set("FrameP95Ms", DynValue.NewNumber(Percentile(FrameSamples, .95)));
                    foreach (object owner in TemporalCoordinator.Current.CaptureBufferOwners()) {
                        var memory = owner.GetType().GetProperty("EstimatedMemoryBytes");
                        var active = owner.GetType().GetProperty("Active");
                        if (memory != null && active != null && (bool)active.GetValue(owner))
                            t.Set("BackendOwnedBytes", DynValue.NewNumber((long)memory.GetValue(owner)));
                    }
                    return DynValue.NewTable(t);
                });
            });
        }

        private IConfigEntry Entry(string name) => (IConfigEntry)typeof(ReduxBetterAAMod)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_mod);
        private static void Bind(Table api, string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> call)
            => api.Set(name, TestApiRegistry.Callback("BetterAA.Quality." + name, call));

        internal static void Before(RenderTexture source, ref RenderTexture destination, out RenderTexture __state)
        {
            __state = destination;
            if (ShimmerCapture.Current != null) { ShimmerCapture.Current.Before(source, ref destination); return; }
            if (_timing && _warmup == 0) {
                _startBytes = AllocatedBytes(); _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            if (_remaining == 0) return;
            try {
                if (_output == null || _output.width != source.width || _output.height != source.height) {
                    Release();
                    var d = source.descriptor;
                    d.depthBufferBits = 0; d.msaaSamples = 1; d.memoryless = UnityEngine.RenderTextureMemoryless.None;
                    _output = new RenderTexture(d); _output.Create();
                    _readback = new Texture2D(source.width, source.height, TextureFormat.RGBAHalf, false, true);
                }
                Save(source, "input");
                destination = _output;
            } catch (Exception ex) { _error = ex.ToString(); _remaining = 0; }
        }

        internal static void After(RenderTexture destination, RenderTexture __state)
        {
            if (ShimmerCapture.Current != null && ShimmerCapture.Current.After(destination,__state)) return;
            if (_timing) {
                if (_warmup > 0) _warmup--;
                else {
                    long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks;
                    long bytes = AllocatedBytes() - _startBytes;
                    CpuSamples[_samples] = ticks * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    FrameSamples[_samples] = Time.unscaledDeltaTime * 1000.0;
                    _maxBytes = Math.Max(_maxBytes, bytes); _totalBytes += bytes;
                    if (++_samples == CpuSamples.Length) _timing = false;
                }
            }
            if (destination != _output || _remaining == 0) return;
            try {
                Save(_output, "output");
                var c = TemporalCoordinator.Current;
                Vector2 jitter = Vector2.zero;
                foreach (object owner in c.CaptureBufferOwners()) {
                    var p = owner.GetType().GetProperty("CurrentJitterNormalized");
                    if (p != null && (bool)(owner.GetType().GetProperty("Active")?.GetValue(owner) ?? false))
                        jitter = (Vector2)p.GetValue(owner);
                }
                var f = new Frame { label = _label, index = _index, frame = Time.frameCount,
                    requested = c.RequestedBackend.ToString(), selected = c.SelectedBackend, active = c.Active,
                    width = _output.width, height = _output.height, gpu = SystemInfo.graphicsDeviceName,
                    unity = Application.unityVersion, format = "RGBAHalf little-endian, linear readback, bottom row first",
                    jitterX = jitter.x, jitterY = jitter.y, sharpness = c.CustomConfig.Sharpening,
                    input = Name("input"), output = Name("output") };
                File.WriteAllText(Path.Combine(_directory, _label + "-" + _index.ToString("D3") + ".json"), JsonUtility.ToJson(f, true));
                _index++; _remaining--;
                if (_remaining == 0 && _reference != null) { Destroy(_reference); _reference = null; }
            } catch (Exception ex) { _error = ex.ToString(); _remaining = 0; }
            finally { Graphics.Blit(destination, __state); }
        }

        private static string Name(string kind) => _label + "-" + _index.ToString("D3") + "-" + kind + ".rgba16f";
        private static double Percentile(double[] values, double quantile) {
            var copy = (double[])values.Clone(); Array.Sort(copy);
            return copy[(int)Math.Ceiling(quantile * copy.Length) - 1];
        }
        private static void Save(RenderTexture texture, string kind)
        {
            RenderTexture prior = RenderTexture.active;
            try {
                RenderTexture.active = texture;
                _readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                _readback.Apply(false, false);
                File.WriteAllBytes(Path.Combine(_directory, Name(kind)), _readback.GetRawTextureData());
            } finally { RenderTexture.active = prior; }
        }
        private static void Release()
        {
            if (_output != null) { _output.Release(); Destroy(_output); _output = null; }
            if (_readback != null) { Destroy(_readback); _readback = null; }
        }
        private void OnDestroy() { if (_video != null) Destroy(_video); _registration?.Dispose(); _patch?.UnpatchAll("ReduxBetterAA.QualityTests"); _remaining = 0; if (_reference != null) Destroy(_reference); Release(); }
    }

    // Off has no temporal hook. Attach a test-only late image effect to the same
    // final scene camera for the supersampled spatial reference, before UI.
    public sealed class QualityReferenceHook : MonoBehaviour
    {
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            RenderTexture target = destination, prior;
            QualityTestMod.Before(source, ref target, out prior);
            Graphics.Blit(source, target);
            QualityTestMod.After(target, prior);
        }
    }
}
