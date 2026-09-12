using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using KSP.Game;
using MoonSharp.Interpreter;
using ReduxBetterAA;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;
using ReduxTestHarness;
using SpaceWarp2.API.Mods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAAVisualTests
{
    // Developer-only observation. No physics setters, fixed-rate capture, or
    // synchronous ReadPixels. All file IO occurs after the observation window.
    public sealed partial class MotionTestMod : MonoBehaviourMod
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static MotionTestMod _instance;
        private ReduxBetterAAMod _mod;
        private IDisposable _registration;
        private Harmony _patch;
        private Camera _camera;
        private MotionVectorSanitizer _motion;
        private RenderTexture _output;
        private readonly RenderTexture[] _small = new RenderTexture[7];
        private static readonly string[] Kinds = {"raw", "depth", "input", "consumed", "output", "input-detail", "output-detail"};
        private Record[] _records;
        private byte[][][] _data;
        private string _root, _label, _error;
        private int _index, _count, _pending, _lastFrame = -1;
        private bool _recording, _written;
        private bool _captureColors, _captureDetail;
        private int _priorFps, _priorVsync;
        private static bool _zeroJitter;
        private string _candidate="none";
        private PropertySheetFactory _sheets;
        private PropertySheet _filter;
        private CommandBuffer _filterCommands;
        private static readonly FieldInfo ResetFrameField=typeof(TemporalCoordinator).GetField("_lastResetUnityFrame",Any);
        private static readonly int HistoryTextureId=Shader.PropertyToID("_To");
        private static readonly int HistoryWeightId=Shader.PropertyToID("_Interp");
        private RenderTexture _motionHistory, _filteredMotion;
        private bool _filterHistory;
        private Texture _lastConsumed;
        private readonly System.Collections.Generic.Dictionary<Camera,CameraSmoothState> _cameraStates=new System.Collections.Generic.Dictionary<Camera,CameraSmoothState>();
        private sealed class CameraSmoothState {
            public Vector3 previous,current,original,rendered;
            public Matrix4x4 renderedView;
            public double fixedTime;
            public int frame;
            public bool applied;
        }
        private struct CaptureState {
            public RenderTexture originalDestination;
            public bool observed, redirected, terrain;
        }

        [Serializable] public sealed class Record
        {
            public int index, frame, width, height, resetFrame, resetReason;
            public double realtime, time, fixedTime, surfaceSpeed, altitude;
            public float delta, fixedDelta, timeScale, near, far, aspect, fov;
            public Vector3 position;
            public Vector3 vesselViewport;
            public Quaternion rotation;
            public Matrix4x4 view, projection, gpuProjection, previousViewProjection;
            public Vector4 zParams;
            public Vector2 jitter;
            public string backend;
        }
        [Serializable] public sealed class Manifest
        {
            public int schema = 1, count, pending, vectorWidth = 64, vectorHeight = 36, colorWidth = 320, colorHeight = 180;
            public string unity, gpu, driver, api, backend, label, error, candidate;
            public bool zeroJitter, colorCapture, detailCapture;
            public int detailWidth=1024, detailHeight=256, detailX, detailY;
            public Record[] frames;
        }

        public override void OnInitialized()
        {
            _root = Environment.GetEnvironmentVariable("RBAA_MOTION_OUTPUT");
            // The optional adapter must be inert in a manually launched player.
            if (string.IsNullOrEmpty(_root)) return;
            _instance = this;
            _mod = UnityEngine.Object.FindAnyObjectByType<ReduxBetterAAMod>();
            Directory.CreateDirectory(_root);
            _priorFps = Application.targetFrameRate; _priorVsync = QualitySettings.vSyncCount;
            _patch = new Harmony("ReduxBetterAA.MotionTests");
            _patch.Patch(typeof(TemporalRenderHook).GetMethod("Render", Any),
                prefix: new HarmonyMethod(typeof(MotionTestMod), nameof(Before)),
                postfix: new HarmonyMethod(typeof(MotionTestMod), nameof(After)));
            _patch.Patch(typeof(SharedJitterSequence).GetMethod("GetCustomOffset"),
                postfix: new HarmonyMethod(typeof(MotionTestMod), nameof(Jitter)));
            _patch.Patch(typeof(CameraProjectionState).GetMethod("Apply"),prefix:new HarmonyMethod(typeof(MotionTestMod),nameof(SmoothCamera)));
            _patch.Patch(typeof(MotionVectorSanitizer).GetMethod("TrySanitize"),postfix:new HarmonyMethod(typeof(MotionTestMod),nameof(FilterMotion)));
            Camera.onPostRender+=RestoreCamera;
            InitializeTerrain();
            _registration = TestApiRegistry.Register("ReduxBetterAA.Motion", (script, api) => {
                Bind(api, "set", (c,a) => {
                    if(_recording || _pending!=0) throw new InvalidOperationException("Cannot change backend during a capture");
                    Entry("_modeEntry").Value = a[0].String;
                    Entry("_sharpnessEntry").Value = 0.24f;
                    Entry("_dlaaPresetEntry").Value = "K";
                    return DynValue.Nil;
                });
                Bind(api, "selected", (c,a) => DynValue.NewString(TemporalCoordinator.Current.SelectedBackend));
                Bind(api, "audit", (c,a) => { WriteTerrainAudit(a[0].String); return DynValue.Nil; });
                BindTerrain(api);
                Bind(api, "speed", (c,a) => DynValue.NewNumber(GameManager.Instance.Game.ViewController.GetActiveSimVessel(true).SrfSpeedMagnitude));
                Bind(api, "altitude", (c,a) => {var v=GameManager.Instance.Game.ViewController.GetActiveSimVessel(true);return DynValue.NewNumber(v==null?-1:v.AltitudeFromSeaLevel);});
                Bind(api, "candidate", (c,a) => {
                    if(_recording || _pending!=0) throw new InvalidOperationException("Cannot change candidate during a capture");
                    string candidate=a[0].String;
                    if(candidate!="none" && candidate!="zero-jitter" && candidate!="half-jitter" && candidate!="motion-ema" && candidate!="camera-interpolate")
                        throw new ArgumentException("Unknown motion candidate");
                    RestoreAllCameras(); _cameraStates.Clear(); _filterHistory=false;
                    _candidate=candidate; _zeroJitter=_candidate=="zero-jitter";
                    if(_candidate=="motion-ema" && _filter==null) {
                        var shader=Shader.Find("Hidden/PostProcessing/Texture2DLerp");
                        if(shader==null) foreach(var resources in Resources.FindObjectsOfTypeAll<PostProcessResources>()) {
                            if(resources.shaders.texture2dLerp!=null) {shader=resources.shaders.texture2dLerp;break;}
                        }
                        if(shader==null || !shader.isSupported) throw new InvalidOperationException("Player texture-lerp shader unavailable");
                        _sheets=new PropertySheetFactory(); _filter=_sheets.Get(shader);
                        _filterCommands=new CommandBuffer {name="Motion experiment EMA"};
                    }
                    TemporalCoordinator.Current.RequestHistoryReset(); return DynValue.Nil;
                });
                Bind(api, "fps", (c,a) => { QualitySettings.vSyncCount=0; Application.targetFrameRate=(int)a[0].Number; return DynValue.Nil; });
                Bind(api, "zero_jitter", (c,a) => { _zeroJitter=a[0].Boolean; TemporalCoordinator.Current.RequestHistoryReset(); return DynValue.Nil; });
                Bind(api, "start", (c,a) => { Begin(a[0].String, (int)a[1].Number,a.Count<3 || a[2].Boolean,a.Count>3 && a[3].Boolean); return DynValue.Nil; });
                Bind(api, "clock", (c,a) => DynValue.NewNumber(Time.realtimeSinceStartupAsDouble));
                Bind(api, "ready", (c,a) => {
                    if (_error != null) throw new InvalidOperationException(_error);
                    if (!_recording && _pending == 0 && !_written) Write();
                    return DynValue.NewBoolean(!_recording && _pending == 0 && _written);
                });
                Bind(api, "options", (c,a) => DynValue.NewString(Environment.GetEnvironmentVariable("RBAA_MOTION_CASE") ?? "baseline"));
            });
        }
        private IConfigEntry Entry(string name) => (IConfigEntry)typeof(ReduxBetterAAMod).GetField(name,Any).GetValue(_mod);
        private static void Bind(Table api,string name,Func<ScriptExecutionContext,CallbackArguments,DynValue> call)
            => api.Set(name, TestApiRegistry.Callback("BetterAA.Motion."+name,call));
        private static void Jitter(ref Vector2 __result) { if(_zeroJitter) __result=Vector2.zero; else if(_instance!=null && _instance._candidate=="half-jitter") __result*=.5f; }

        // Deliberately limited control: smooth ONLY the rendering cameras. This
        // leaves physics and vessel transforms untouched, so vessel-relative
        // judder must be measured before this could ever be a production option.
        private static void SmoothCamera(Camera camera)
        {
            var s=_instance;
            if(s==null || s._candidate!="camera-interpolate" || Time.timeScale==0) return;
            CameraSmoothState state;
            Vector3 original=camera.transform.position;
            if(!s._cameraStates.TryGetValue(camera,out state)) {
                state=new CameraSmoothState {previous=original,current=original,fixedTime=Time.fixedTimeAsDouble,frame=-1};
                s._cameraStates.Add(camera,state);
            }
            if(state.frame==Time.frameCount) return;
            if(state.applied) camera.transform.position=state.original;
            if(state.fixedTime!=Time.fixedTimeAsDouble) {
                state.previous=state.current; state.current=original; state.fixedTime=Time.fixedTimeAsDouble;
                if(Vector3.Distance(state.previous,state.current)>64) state.previous=state.current;
            }
            float alpha=Mathf.Clamp01((float)((Time.timeAsDouble-Time.fixedTimeAsDouble)/Time.fixedDeltaTime));
            state.original=original; state.frame=Time.frameCount; state.applied=true;
            camera.transform.position=Vector3.Lerp(state.previous,state.current,alpha);
            state.rendered=camera.transform.position; state.renderedView=camera.worldToCameraMatrix;
        }
        private void RestoreCamera(Camera camera)
        {
            CameraSmoothState state;
            if(_cameraStates.TryGetValue(camera,out state) && state.applied) {
                camera.transform.position=state.original; state.applied=false;
            }
        }
        private void RestoreAllCameras() {foreach(var pair in _cameraStates) if(pair.Key!=null) RestoreCamera(pair.Key);}
        private static void FilterMotion(bool __result,ref Texture sanitized)
        {
            var s=_instance; if(s==null || !__result || sanitized==null) return;
            s._lastConsumed=sanitized;
            if(s._candidate!="motion-ema") return;
            if(s._motionHistory==null || s._motionHistory.width!=sanitized.width || s._motionHistory.height!=sanitized.height) {
                if(s._motionHistory!=null) {s._motionHistory.Release();Destroy(s._motionHistory);s._filteredMotion.Release();Destroy(s._filteredMotion);}
                s._motionHistory=new RenderTexture(sanitized.width,sanitized.height,0,RenderTextureFormat.RGFloat,RenderTextureReadWrite.Linear);
                s._filteredMotion=new RenderTexture(sanitized.width,sanitized.height,0,RenderTextureFormat.RGFloat,RenderTextureReadWrite.Linear);
                s._motionHistory.Create();s._filteredMotion.Create();s._filterHistory=false;
            }
            bool reset=(int)ResetFrameField.GetValue(TemporalCoordinator.Current)==Time.frameCount;
            if(reset) s._filterHistory=false;
            s._filter.properties.SetTexture(HistoryTextureId,s._motionHistory);s._filter.properties.SetFloat(HistoryWeightId,.5f);
            if(s._filterHistory) {
                s._filterCommands.Clear();
                s._filterCommands.BlitFullscreenTriangle(sanitized,s._filteredMotion,s._filter,0);
                Graphics.ExecuteCommandBuffer(s._filterCommands);
            }
            else Graphics.Blit(sanitized,s._filteredMotion);
            var swap=s._motionHistory;s._motionHistory=s._filteredMotion;s._filteredMotion=swap;
            // A reset frame has no valid prior pose. Do not carry its possibly
            // rebased vectors into the next frame's filter history either.
            sanitized=s._motionHistory;s._lastConsumed=sanitized;s._filterHistory=!reset;
        }

        private void Begin(string label,int count,bool captureColors,bool captureDetail)
        {
            if (_recording || _pending != 0) throw new InvalidOperationException("Previous capture still active");
            if (!SystemInfo.supportsAsyncGPUReadback) throw new InvalidOperationException("Async readback unavailable");
            if (string.IsNullOrEmpty(label) || label=="." || label==".." || label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || count < 8 || count > 600)
                throw new ArgumentException("Invalid capture label/count");
            if(Directory.Exists(Path.Combine(_root,label))) throw new InvalidOperationException("Capture label already exists; evidence is immutable");
            if(captureDetail && (!captureColors || count>240)) throw new ArgumentException("Detail capture requires color and at most 240 frames");
            _camera=TemporalCameraDiscovery.Discover().ResolveCamera;
            if (_camera == null) throw new InvalidOperationException("Resolve camera unavailable");
            _motion=(MotionVectorSanitizer)typeof(TemporalCoordinator).GetField("_motionVectorSanitizer",Any).GetValue(TemporalCoordinator.Current);
            Release();
            _captureColors=captureColors; _captureDetail=captureDetail;
            for(int k=0;k<Kinds.Length;k++) {
                bool color=k==2 || k>=4;
                if(color && !_captureColors) continue;
                if(k>=5 && !_captureDetail) continue;
                _small[k]=new RenderTexture(k>=5?1024:(color?320:64),k>=5?256:(color?180:36),0,color?RenderTextureFormat.ARGBHalf:RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                _small[k].filterMode=FilterMode.Point;
                if (!_small[k].Create()) throw new InvalidOperationException("Readback target failed");
            }
            _records=new Record[count]; _data=new byte[count][][];
            for(int i=0;i<count;i++) _data[i]=new byte[Kinds.Length][];
            _label=label; _count=count; _index=0; _lastFrame=-1; _error=null; _written=false; _recording=true;
        }
        private static void Before(TemporalRenderHook __instance,RenderTexture source,ref RenderTexture destination,out CaptureState __state)
        {
            __state=new CaptureState {originalDestination=destination};
            var s=_instance;
            if(s!=null && s._terrainRecording) {
                s.TerrainBefore(__instance,source,ref destination,ref __state);
                return;
            }
            if (s==null || !s._recording || __instance.GetComponent<Camera>()!=s._camera) return;
            try {
                if (s._lastFrame>=0 && Time.frameCount!=s._lastFrame+1) throw new InvalidOperationException("Nonconsecutive resolve frames");
                if (s._pending>80) throw new InvalidOperationException("Readback queue exceeded bounded capacity");
                if(s._captureColors && s._output==null) {
                    var d=source.descriptor; d.depthBufferBits=0; d.msaaSamples=1;
                    s._output=new RenderTexture(d); if(!s._output.Create()) throw new InvalidOperationException("Output allocation failed");
                }
                __state.observed=true;
                if(s._captureColors) { destination=s._output; __state.redirected=true; }
                var cam=s._camera; var coordinator=TemporalCoordinator.Current;
                var vessel=GameManager.Instance.Game.ViewController.GetActiveSimVessel(true);
                var behavior=GameManager.Instance.Game.ViewController.GetBehaviorIfLoaded(vessel);
                var r=new Record { index=s._index,frame=Time.frameCount,width=source.width,height=source.height,
                    realtime=Time.realtimeSinceStartupAsDouble,time=Time.timeAsDouble,fixedTime=Time.fixedTimeAsDouble,
                    surfaceSpeed=vessel.SrfSpeedMagnitude,altitude=vessel.AltitudeFromSeaLevel,
                    vesselViewport=behavior==null?Vector3.zero:cam.WorldToViewportPoint(behavior.transform.position),
                    delta=Time.unscaledDeltaTime,fixedDelta=Time.fixedDeltaTime,timeScale=Time.timeScale,
                    position=cam.transform.position,rotation=cam.transform.rotation,view=cam.worldToCameraMatrix,
                    projection=cam.nonJitteredProjectionMatrix,gpuProjection=GL.GetGPUProjectionMatrix(cam.nonJitteredProjectionMatrix,cam.targetTexture!=null),
                    previousViewProjection=cam.previousViewProjectionMatrix,near=cam.nearClipPlane,far=cam.farClipPlane,aspect=cam.aspect,fov=cam.fieldOfView,
                    zParams=Shader.GetGlobalVector("_ZBufferParams"),backend=coordinator.SelectedBackend,
                    resetFrame=(int)typeof(TemporalCoordinator).GetField("_lastResetUnityFrame",Any).GetValue(coordinator),
                    resetReason=(int)(HistoryResetReason)typeof(TemporalCoordinator).GetField("_lastResetReason",Any).GetValue(coordinator) };
                // onPostRender precedes this image effect and restores the
                // experiment camera. Audit the pose actually used for raster.
                CameraSmoothState rendered;
                if(s._candidate=="camera-interpolate" && s._cameraStates.TryGetValue(cam,out rendered) && rendered.frame==Time.frameCount) {
                    r.position=rendered.rendered; r.view=rendered.renderedView;
                    if(behavior!=null) {
                        var clip=(r.projection*r.view).MultiplyPoint(behavior.transform.position);
                        r.vesselViewport=new Vector3(clip.x*.5f+.5f,clip.y*.5f+.5f,clip.z);
                    }
                }
                s._records[s._index]=r;
                s.Sample(Shader.GetGlobalTexture("_CameraMotionVectorsTexture"),0);
                s.Sample(Shader.GetGlobalTexture("_CameraDepthTexture"),1);
                if(s._captureColors) s.Sample(source,2);
                if(s._captureDetail) s.Sample(source,5);
                s._lastFrame=Time.frameCount;
            } catch(Exception e) { s._error=e.ToString(); s._recording=false; }
        }
        private static void After(TemporalRenderHook __instance,RenderTexture source,RenderTexture destination,CaptureState __state)
        {
            var s=_instance;
            if(s!=null && __state.terrain) {s.TerrainAfter(destination,__state);return;}
            if(s==null || !__state.observed) return;
            try {
                if(s._recording) {
                    s._records[s._index].jitter=s._motion.MatrixSnapshot.CurrentJitterNormalized;
                    s.Sample(s._lastConsumed??s._motion.SanitizedTexture,3);
                    if(s._captureColors) s.Sample(destination,4);
                    if(s._captureDetail) s.Sample(destination,6);
                    if(++s._index==s._count) s._recording=false;
                }
            } catch(Exception e) { s._error=e.ToString(); s._recording=false; }
            finally { if(__state.redirected) Graphics.Blit(destination,__state.originalDestination); }
        }
        private void Sample(Texture texture,int kind)
        {
            if(texture==null) throw new InvalidOperationException("Missing "+Kinds[kind]);
            if(kind>=5) {
                int x=(texture.width-1024)/2,y=(texture.height-256)/2;
                if(x<0 || y<0) throw new InvalidOperationException("Scene too small for native detail capture");
                Graphics.Blit(texture,_small[kind],new Vector2(1024f/texture.width,256f/texture.height),new Vector2((float)x/texture.width,(float)y/texture.height));
            } else Graphics.Blit(texture,_small[kind]);
            int index=_index;
            _pending++;
            AsyncGPUReadback.Request(_small[kind],0,request=>{
                try {
                    if(request.hasError) { _error="Async readback failed"; _recording=false; }
                    else _data[index][kind]=request.GetData<byte>().ToArray();
                } catch(Exception e) { _error=e.ToString(); _recording=false; }
                finally { _pending--; }
            });
        }
        private void Write()
        {
            string dir=Path.Combine(_root,_label); Directory.CreateDirectory(dir);
            for(int k=0;k<Kinds.Length;k++) {
                if(!_captureColors && (k==2 || k>=4)) continue;
                if(k>=5 && !_captureDetail) continue;
                using(var stream=File.Create(Path.Combine(dir,Kinds[k]+(k==2 || k>=4 ? ".rgba16f" : ".rgba32f")))) {
                for(int i=0;i<_count;i++) {
                    if(_data[i][k]==null) throw new InvalidOperationException("Missing frame data");
                    stream.Write(_data[i][k],0,_data[i][k].Length);
                }
                }
            }
            var manifest=new Manifest {count=_count,pending=_pending,unity=Application.unityVersion,
                gpu=SystemInfo.graphicsDeviceName,driver=SystemInfo.graphicsDeviceVersion,api=SystemInfo.graphicsDeviceType.ToString(),
                backend=_records[0].backend,label=_label,error=_error,zeroJitter=_zeroJitter,frames=_records,candidate=_candidate,colorCapture=_captureColors,
                detailCapture=_captureDetail,detailX=(_records[0].width-1024)/2,detailY=(_records[0].height-256)/2};
            File.WriteAllText(Path.Combine(dir,"capture.json"),JsonUtility.ToJson(manifest,true));
            using(var writer=new StreamWriter(Path.Combine(dir,"frames.jsonl"))) {
                for(int i=0;i<_count;i++) {
                    string json=JsonUtility.ToJson(_records[i]);
                    if(!json.Contains("\"frame\"")) throw new InvalidOperationException("Frame metadata serialization failed");
                    writer.WriteLine(json);
                }
            }
            _written=true; _data=null;
        }
        private void Release()
        {
            if(_output!=null) { _output.Release(); Destroy(_output); _output=null; }
            for(int k=0;k<_small.Length;k++) if(_small[k]!=null) { _small[k].Release(); Destroy(_small[k]); _small[k]=null; }
        }
        private void OnDestroy()
        {
            if(_instance!=this) return;
            _recording=false;
            _terrainRecording=false;
            AsyncGPUReadback.WaitAllRequests();
            DisposeTerrain();
            _registration?.Dispose(); _patch?.UnpatchAll("ReduxBetterAA.MotionTests");
            Camera.onPostRender-=RestoreCamera; RestoreAllCameras();
            if(_motionHistory!=null) {_motionHistory.Release();Destroy(_motionHistory);}
            if(_filteredMotion!=null) {_filteredMotion.Release();Destroy(_filteredMotion);}
            _filterCommands?.Release(); _sheets?.Release();
            Application.targetFrameRate=_priorFps; QualitySettings.vSyncCount=_priorVsync;
            Release(); _instance=null;
        }
    }
}
