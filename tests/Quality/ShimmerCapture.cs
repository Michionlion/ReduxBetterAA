using System;
using System.IO;
using System.Reflection;
using ReduxBetterAA.Rendering;
using ReduxTestHarness;
using UnityEngine;

namespace ReduxBetterAAVisualTests
{
    // Normal production rendering, sampled every frame. No comparison replay.
    [DefaultExecutionOrder(31000)]
    public sealed class ShimmerCapture : MonoBehaviour
    {
        internal static ShimmerCapture Current;
        private Action<double,double,double,double,bool> _orbit;
        private Action _apply;
        private RenderTexture _target, _debug;
        private Texture2D _structure, _terrain;
        private RenderTexture _replayTarget;
        private Texture2D _replayStructure, _replayTerrain;
        private Texture _source;
        private StreamWriter _frames;
        private string _directory, _error;
        private int _index, _start, _poseFrame = -1, _previousFrame = -1;
        private float _priorDelta;
        private bool _recording;
        private bool _reverseDiagonal;
        internal bool Done { get { if (_error != null) throw new InvalidOperationException(_error); return _index == 128; } }
        private Vector2 Pose(int n) {
            if (!_reverseDiagonal) return new Vector2(n < 32 ? 0 : Math.Min(n - 31, 64) * .1f,30);
            float travel=n<32?0:n<64?n-31:n<96?95-n:0;
            return new Vector2(travel*.2f,30+travel*.1f);
        }

        internal void Begin(string label)
        {
            if (Screen.width != 2560 || Screen.height != 1440) throw new InvalidOperationException("Shimmer fixture requires 2560x1440.");
            _directory = Path.Combine(Environment.GetEnvironmentVariable("RBAA_QUALITY_OUTPUT"), label);
            _reverseDiagonal = Environment.GetEnvironmentVariable("RBAA_SHIMMER_PATH") == "reverse-diagonal";
            Directory.CreateDirectory(_directory);
            var harness = UnityEngine.Object.FindAnyObjectByType<ReduxTestHarnessMod>();
            object adapter = typeof(ReduxTestHarnessMod).GetField("_game", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(harness);
            _orbit = (Action<double,double,double,double,bool>)Delegate.CreateDelegate(typeof(Action<double,double,double,double,bool>), adapter, adapter.GetType().GetMethod("SetOrbitCamera"));
            _apply = (Action)Delegate.CreateDelegate(typeof(Action), adapter, adapter.GetType().GetMethod("ApplyCameraOverride"));
            _target = new RenderTexture(2560,1440,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear); _target.Create();
            _debug = new RenderTexture(_target.descriptor); _debug.Create();
            _structure = new Texture2D(512,512,TextureFormat.RGBAHalf,false,true);
            _terrain = new Texture2D(256,256,TextureFormat.RGBAHalf,false,true);
            if (Environment.GetEnvironmentVariable("RBAA_SHIMMER_REPLAY") == "1") {
                _replayTarget = new RenderTexture(2560,1440,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear); _replayTarget.Create();
                _replayStructure = new Texture2D(512,512,TextureFormat.RGBAFloat,false,true);
                _replayTerrain = new Texture2D(256,256,TextureFormat.RGBAFloat,false,true);
            }
            _frames = new StreamWriter(Path.Combine(_directory,"frames.csv"));
            _frames.WriteLine("index,unity_frame,yaw,selected,jitter_x,jitter_y,history_used,pitch");
            _priorDelta = Time.captureDeltaTime; Time.captureDeltaTime = 1f/60;
            _start = Time.frameCount + 1; _recording = true; Current = this;
        }

        private void LateUpdate()
        {
            if (!_recording || Time.frameCount < _start) return;
            Vector2 pose=Pose(_index);
            _orbit(35,pose.x,pose.y,55,true); _apply(); _poseFrame = Time.frameCount;
        }

        internal void Before(RenderTexture source, ref RenderTexture destination)
        {
            if (!_recording || Time.frameCount < _start) return;
            _source = source; destination = _target;
        }

        internal bool After(RenderTexture destination, RenderTexture original)
        {
            if (destination != _target || !_recording) return false;
            try {
                if (_poseFrame != Time.frameCount || (_previousFrame >= 0 && Time.frameCount != _previousFrame + 1))
                    throw new InvalidOperationException("Missing/duplicate frame or camera pose.");
                var coordinator = TemporalCoordinator.Current;
                object custom = null; Vector2 jitter = Vector2.zero; bool historyUsed = true;
                foreach (object owner in coordinator.CaptureBufferOwners()) {
                    var active = owner.GetType().GetProperty("Active");
                    if (active == null || !(bool)active.GetValue(owner)) continue;
                    var j = owner.GetType().GetProperty("CurrentJitterNormalized");
                    if (j != null) jitter = (Vector2)j.GetValue(owner);
                    if (owner.GetType().Name == "CustomTaaBackend") custom = owner;
                }
                Save(_target,"output");
                if (custom != null) {
                    var material = (Material)custom.GetType().GetField("_material",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(custom);
                    historyUsed = material.GetFloat("_HistoryValid") > .5f;
                    if (!historyUsed) throw new InvalidOperationException("History reset during shimmer sequence.");
                    if (_replayTarget != null) {
                        Save((RenderTexture)_source,"input");
                        ReplayTexture(material.GetTexture("_ReduxBetterAAMotionVectors"),"motion");
                        ReplayTexture(Shader.GetGlobalTexture("_CameraDepthTexture"),"depth");
                        if (_index == 0) {
                            Save((RenderTexture)material.GetTexture("_HistoryTex"),"history");
                            ReplayTexture(material.GetTexture("_HistoryDepthTex"),"history-depth");
                            File.WriteAllText(Path.Combine(_directory,"replay.json"),JsonUtility.ToJson(new ReplayMetadata {
                                z=Shader.GetGlobalVector("_ZBufferParams"),
                                depthFilter=(int)Shader.GetGlobalTexture("_CameraDepthTexture").filterMode,
                                motionFilter=(int)material.GetTexture("_ReduxBetterAAMotionVectors").filterMode
                            },true));
                        }
                    }
                    if (_index % 16 == 0 || _index == 127) {
                        Save((RenderTexture)_source,"input");
                        Graphics.Blit(material.GetTexture("_ReduxBetterAAMotionVectors"),_debug); Save(_debug,"motion");
                        foreach (int mode in new[] {4,5,6,9}) {
                            material.SetFloat("_DebugMode",mode);
                            Graphics.Blit(_source,_debug,material,3); Save(_debug,"debug"+mode);
                        }
                        material.SetFloat("_DebugMode",0);
                    }
                }
                Vector2 pose=Pose(_index);
                _frames.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"{0},{1},{2:F6},{3},{4:R},{5:R},{6},{7:F6}",
                    _index,Time.frameCount,pose.x,coordinator.SelectedBackend,jitter.x,jitter.y,historyUsed,pose.y));
                _previousFrame = Time.frameCount;
                if (++_index == 128) { _recording=false; Time.captureDeltaTime=_priorDelta; _frames.Dispose(); _frames=null; }
            } catch (Exception e) { _error=e.ToString(); _recording=false; Time.captureDeltaTime=_priorDelta; }
            finally { Graphics.Blit(destination,original); }
            return true;
        }

        [Serializable] private sealed class ReplayMetadata { public Vector4 z; public int depthFilter, motionFilter; }

        private void ReplayTexture(Texture source, string kind)
        {
            Graphics.Blit(source,_replayTarget);
            var prior=RenderTexture.active;
            try {
                RenderTexture.active=_replayTarget;
                _replayStructure.ReadPixels(new Rect(1024,512,512,512),0,0,false);
                _replayTerrain.ReadPixels(new Rect(256,896,256,256),0,0,false);
                string name=_index.ToString("D3")+"-"+kind;
                File.WriteAllBytes(Path.Combine(_directory,name+"-structure.rgba32f"),_replayStructure.GetRawTextureData());
                File.WriteAllBytes(Path.Combine(_directory,name+"-terrain.rgba32f"),_replayTerrain.GetRawTextureData());
            } finally { RenderTexture.active=prior; }
        }

        private void Save(RenderTexture source, string kind)
        {
            RenderTexture prior = RenderTexture.active;
            try {
                RenderTexture.active=source;
                _structure.ReadPixels(new Rect(1024,512,512,512),0,0,false);
                _terrain.ReadPixels(new Rect(256,896,256,256),0,0,false);
                string name = _index.ToString("D3")+"-"+kind;
                File.WriteAllBytes(Path.Combine(_directory,name+"-structure.rgba16f"),_structure.GetRawTextureData());
                File.WriteAllBytes(Path.Combine(_directory,name+"-terrain.rgba16f"),_terrain.GetRawTextureData());
            } finally { RenderTexture.active=prior; }
        }

        private void OnDestroy()
        {
            if (_recording) Time.captureDeltaTime=_priorDelta;
            if (Current==this) Current=null;
            _frames?.Dispose();
            foreach (var rt in new[] {_target,_debug,_replayTarget}) if (rt!=null) { rt.Release(); Destroy(rt); }
            if (_structure!=null) Destroy(_structure); if (_terrain!=null) Destroy(_terrain);
            if (_replayStructure!=null) Destroy(_replayStructure); if (_replayTerrain!=null) Destroy(_replayTerrain);
        }
    }
}
