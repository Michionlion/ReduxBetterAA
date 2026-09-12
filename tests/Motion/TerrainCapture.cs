using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using KSP.Game;
using MoonSharp.Interpreter;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAAVisualTests
{
    // Optional developer capture. Bounded native pixels, asynchronous readback,
    // real clocks, and no video encoder or changes to the physics clock.
    public sealed partial class MotionTestMod
    {
        [Serializable] public sealed class TerrainFrame
        {
            public int frame,index,width,height,resetFrame,resetReason,projectionFrame,pqsFrame,pqsCalls,pqsDepthFrame,pqsDecalFrame;
            public double realtime,time,fixedTime,speed,altitude;
            public float delta,fixedDelta,timeScale;
            public string backend;
            public Vector3 position;
            public Quaternion rotation;
            public Matrix4x4 view,gpuProjection,rasterGpuProjection,pqsRaster,pqsDepthRaster,pqsDecalRaster;
            public Vector2 jitter,pqsJitter;
            public bool transparentJitter;
        }
        [Serializable] public sealed class TerrainManifest
        {
            public int schema=1,count,pending,x,y,width=768,height=384,overviewWidth=512,overviewHeight=288;
            public string label,candidate,backend,unity,gpu,api,error,dlaaPreset;
            public float sharpness;
            public bool offPassthrough,depthReversed;
            public string orientation="bottom-row-first; ROI coordinates from bottom left; linear RGBA16F; depth R32F";
        }
        private sealed class TerrainProjection
        {
            public Matrix4x4 raster,originalCulling,appliedCulling;
            public Vector2 jitter;
            public int frame;
            public bool transparent,ownsCulling;
        }
        private readonly Dictionary<Camera,TerrainProjection> _terrainProjections=new Dictionary<Camera,TerrainProjection>();
        private string _terrainCandidate="none",_terrainLabel,_terrainError;
        private bool _terrainRecording,_terrainWritten,_terrainOff;
        private int _terrainIndex,_terrainCount,_terrainPending,_terrainX,_terrainY;
        private Camera _terrainCamera;
        private TemporalRenderHook _terrainOffHook;
        private DepthTextureMode _terrainOriginalDepth,_terrainAppliedDepth;
        private TerrainFrame[] _terrainFrames;
        private byte[][][] _terrainData;
        private RenderTexture _terrainOutput;
        private readonly RenderTexture[] _terrainSmall=new RenderTexture[5];
        private static readonly string[] TerrainKinds={"input","output","depth","overview-input","overview-output"};
        private ShadowQuality _terrainOriginalShadows;
        private bool _terrainOwnsShadows;

        private void InitializeTerrain()
        {
            InitializeTerrainPrepass();
            InitializeTerrainProductionProbe();
            _patch.Patch(typeof(CameraProjectionState).GetMethod("Apply"),
                prefix:new HarmonyMethod(typeof(MotionTestMod),nameof(TerrainProjectionBefore)),
                postfix:new HarmonyMethod(typeof(MotionTestMod),nameof(TerrainProjectionAfter)));
            Camera.onPostRender+=TerrainRestoreCulling;
        }
        private void BindTerrain(Table api)
        {
            Bind(api,"terrain_set",(c,a)=>{Entry("_modeEntry").Value=a[0].String;return DynValue.Nil;});
            BindTerrainProductionProbe(api);
            Bind(api,"terrain_candidate",(c,a)=>{SetTerrainCandidate(a[0].String);return DynValue.Nil;});
            Bind(api,"terrain_start",(c,a)=>{BeginTerrain(a[0].String,(int)a[1].Number,(int)a[2].Number,(int)a[3].Number);return DynValue.Nil;});
            Bind(api,"terrain_ready",(c,a)=>{
                if(_terrainError!=null) throw new InvalidOperationException(_terrainError);
                if(!_terrainRecording && _terrainPending==0 && !_terrainWritten) WriteTerrain();
                return DynValue.NewBoolean(_terrainWritten && _terrainPending==0);
            });
        }
        private void SetTerrainCandidate(string candidate)
        {
            if(_terrainRecording || _terrainPending!=0 || _recording || _pending!=0) throw new InvalidOperationException("Capture in progress");
            if(candidate!="none" && candidate!="transparent-jitter" && candidate!="stable-culling" && candidate!="zero-jitter" && candidate!="shadows-off" && candidate!="pqs-prepass" && candidate!="pqs-all-buffers" && candidate!="pqs-depth" && candidate!="pqs-decal" && candidate!="pqs-depth-decal" && candidate!="production-bypass")
                throw new ArgumentException("Unknown terrain candidate");
            RestoreTerrainShadows();
            _terrainCandidate=candidate;_zeroJitter=candidate=="zero-jitter";
            if(candidate=="shadows-off") {_terrainOriginalShadows=QualitySettings.shadows;QualitySettings.shadows=ShadowQuality.Disable;_terrainOwnsShadows=true;}
            TemporalCoordinator.Current.RequestHistoryReset();
        }
        private static void TerrainProjectionBefore(ref bool jitterTransparentRendering)
        {
            if(_instance!=null && _instance._terrainCandidate=="transparent-jitter") jitterTransparentRendering=true;
        }
        private static void TerrainProjectionAfter(Camera camera,Vector2 jitter)
        {
            var s=_instance;if(s==null) return;
            TerrainProjection p;
            if(!s._terrainProjections.TryGetValue(camera,out p)) {p=new TerrainProjection();s._terrainProjections.Add(camera,p);}
            p.frame=Time.frameCount;p.raster=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);
            p.transparent=camera.useJitteredProjectionMatrixForTransparentRendering;p.jitter=jitter;
            if(s._terrainCandidate=="stable-culling" && !p.ownsCulling) {
                p.originalCulling=camera.cullingMatrix;
                p.appliedCulling=camera.nonJitteredProjectionMatrix*camera.worldToCameraMatrix;
                camera.cullingMatrix=p.appliedCulling;p.ownsCulling=true;
            }
        }
        private void TerrainRestoreCulling(Camera camera)
        {
            TerrainProjection p;
            if(camera!=null && _terrainProjections.TryGetValue(camera,out p) && p.ownsCulling) {
                // All tested flight cameras use their automatic culling matrix.
                // Do not leave an explicit matrix installed after this frame.
                if(camera.cullingMatrix==p.appliedCulling) camera.ResetCullingMatrix();
                p.ownsCulling=false;
            }
        }
        private void BeginTerrain(string label,int count,int x,int y)
        {
            if(_terrainRecording || _terrainPending!=0 || _recording || _pending!=0) throw new InvalidOperationException("Previous capture active");
            if(string.IsNullOrEmpty(label) || label=="." || label==".." || label.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || count<8 || count>240 || x<0 || y<0)
                throw new ArgumentException("Invalid terrain capture");
            if(Directory.Exists(Path.Combine(_root,label))) throw new InvalidOperationException("Evidence label already exists");
            if(!SystemInfo.supportsAsyncGPUReadback) throw new InvalidOperationException("Async readback unavailable");
            ReleaseTerrainTargets();
            _terrainCamera=TemporalCameraDiscovery.Discover().ResolveCamera;
            if(_terrainCamera==null) throw new InvalidOperationException("No scene camera");
            _terrainOff=TemporalCoordinator.Current.SelectedBackend=="Off";
            if(_terrainOff) {
                _terrainOriginalDepth=_terrainCamera.depthTextureMode;
                _terrainAppliedDepth=_terrainOriginalDepth|DepthTextureMode.Depth;
                _terrainCamera.depthTextureMode=_terrainAppliedDepth;
                _terrainOffHook=TemporalRenderHook.Attach(_terrainCamera,null);
            }
            _terrainX=x;_terrainY=y;_terrainLabel=label;_terrainCount=count;
            _terrainFrames=new TerrainFrame[count];_terrainData=new byte[count][][];
            for(int i=0;i<count;i++) _terrainData[i]=new byte[5][];
            for(int k=0;k<5;k++) {
                _terrainSmall[k]=new RenderTexture(k<3?768:512,k<3?384:288,0,k==2?RenderTextureFormat.RFloat:RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
                _terrainSmall[k].filterMode=FilterMode.Point;
                if(!_terrainSmall[k].Create()) throw new InvalidOperationException("Terrain target creation failed");
            }
            _terrainIndex=0;_terrainError=null;_terrainWritten=false;_terrainRecording=true;
        }
        private void TerrainBefore(TemporalRenderHook hook,RenderTexture source,ref RenderTexture destination,ref CaptureState state)
        {
            if(hook.GetComponent<Camera>()!=_terrainCamera) return;
            try {
                if(_terrainIndex>0 && _terrainFrames[_terrainIndex-1].frame+1!=Time.frameCount) throw new InvalidOperationException("Missing/duplicate terrain frame");
                if(_terrainPending>50) throw new InvalidOperationException("Terrain queue capacity exceeded");
                if(source.width<_terrainX+768 || source.height<_terrainY+384) throw new InvalidOperationException("ROI outside native scene");
                if(_terrainOutput==null) {var d=source.descriptor;d.depthBufferBits=0;d.msaaSamples=1;_terrainOutput=new RenderTexture(d);if(!_terrainOutput.Create()) throw new InvalidOperationException("Output allocation failed");}
                state.terrain=true;state.redirected=true;destination=_terrainOutput;
                var cam=_terrainCamera;var coordinator=TemporalCoordinator.Current;
                var v=GameManager.Instance.Game.ViewController.GetActiveSimVessel(true);
                TerrainProjection p;_terrainProjections.TryGetValue(cam,out p);
                var frame=new TerrainFrame {frame=Time.frameCount,index=_terrainIndex,width=source.width,height=source.height,
                    realtime=Time.realtimeSinceStartupAsDouble,time=Time.timeAsDouble,fixedTime=Time.fixedTimeAsDouble,delta=Time.unscaledDeltaTime,
                    fixedDelta=Time.fixedDeltaTime,timeScale=Time.timeScale,speed=v.SrfSpeedMagnitude,altitude=v.AltitudeFromSeaLevel,
                    position=cam.transform.position,rotation=cam.transform.rotation,view=cam.worldToCameraMatrix,
                    gpuProjection=GL.GetGPUProjectionMatrix(cam.nonJitteredProjectionMatrix,true),
                    rasterGpuProjection=_terrainOff?GL.GetGPUProjectionMatrix(cam.projectionMatrix,true):p.raster,
                    projectionFrame=_terrainOff?Time.frameCount:p.frame,transparentJitter=!_terrainOff && p.transparent,
                    jitter=_terrainOff?Vector2.zero:p.jitter,backend=coordinator.SelectedBackend,
                    pqsFrame=_pqsFrame,pqsRaster=_pqsRaster,pqsJitter=_pqsJitter,pqsCalls=_pqsCalls,
                    pqsDepthFrame=_pqsDepthFrame,pqsDepthRaster=_pqsDepthRaster,pqsDecalFrame=_pqsDecalFrame,pqsDecalRaster=_pqsDecalRaster,
                    resetFrame=(int)ResetFrameField.GetValue(coordinator),
                    resetReason=(int)(HistoryResetReason)typeof(TemporalCoordinator).GetField("_lastResetReason",Any).GetValue(coordinator)};
                if(frame.projectionFrame!=frame.frame) throw new InvalidOperationException("Stale raster projection");
                var depth=Shader.GetGlobalTexture("_CameraDepthTexture");
                if(depth==null || depth.width!=source.width || depth.height!=source.height) throw new InvalidOperationException("Depth does not match scene dimensions");
                _terrainFrames[_terrainIndex]=frame;
                SampleTerrain(source,0);SampleTerrain(depth,2);SampleTerrain(source,3);
            } catch(Exception e) {_terrainError=e.ToString();_terrainRecording=false;}
        }
        private void TerrainAfter(RenderTexture destination,CaptureState state)
        {
            try {
                if(_terrainRecording) {SampleTerrain(destination,1);SampleTerrain(destination,4);if(++_terrainIndex==_terrainCount) _terrainRecording=false;}
            } catch(Exception e) {_terrainError=e.ToString();_terrainRecording=false;}
            finally {if(state.redirected) Graphics.Blit(destination,state.originalDestination);}
        }
        private void SampleTerrain(Texture texture,int kind)
        {
            if(kind<3) Graphics.Blit(texture,_terrainSmall[kind],new Vector2(768f/texture.width,384f/texture.height),new Vector2((float)_terrainX/texture.width,(float)_terrainY/texture.height));
            else Graphics.Blit(texture,_terrainSmall[kind]);
            int index=_terrainIndex;_terrainPending++;
            AsyncGPUReadback.Request(_terrainSmall[kind],0,r=>{
                try {if(r.hasError) {_terrainError="Terrain readback failed";_terrainRecording=false;} else _terrainData[index][kind]=r.GetData<byte>().ToArray();}
                catch(Exception e) {_terrainError=e.ToString();_terrainRecording=false;}
                finally {_terrainPending--;}
            });
        }
        private void WriteTerrain()
        {
            if(_terrainFrames==null || _terrainIndex!=_terrainCount) throw new InvalidOperationException("Incomplete terrain capture");
            string dir=Path.Combine(_root,_terrainLabel);Directory.CreateDirectory(dir);
            for(int k=0;k<5;k++) using(var stream=File.Create(Path.Combine(dir,TerrainKinds[k]+(k==2?".r32f":".rgba16f"))))
                for(int i=0;i<_terrainCount;i++) {var data=_terrainData[i][k];if(data==null) throw new InvalidOperationException("Missing terrain bytes");stream.Write(data,0,data.Length);}
            var manifest=new TerrainManifest {count=_terrainCount,pending=_terrainPending,x=_terrainX,y=_terrainY,label=_terrainLabel,
                candidate=_terrainCandidate,backend=_terrainFrames[0].backend,unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,
                api=SystemInfo.graphicsDeviceType.ToString(),error=_terrainError,offPassthrough=_terrainOff,depthReversed=SystemInfo.usesReversedZBuffer};
            manifest.dlaaPreset=(string)Entry("_dlaaPresetEntry").Value;manifest.sharpness=Convert.ToSingle(Entry("_sharpnessEntry").Value);
            File.WriteAllText(Path.Combine(dir,"terrain.json"),JsonUtility.ToJson(manifest,true));
            using(var writer=new StreamWriter(Path.Combine(dir,"frames.jsonl"))) foreach(var frame in _terrainFrames) writer.WriteLine(JsonUtility.ToJson(frame));
            _terrainWritten=true;_terrainData=null;ReleaseTerrainTargets();
        }
        private void ReleaseTerrainTargets()
        {
            if(_terrainOffHook!=null) {
                TemporalRenderHook.Detach(ref _terrainOffHook);
                if(_terrainCamera!=null && _terrainCamera.depthTextureMode==_terrainAppliedDepth) _terrainCamera.depthTextureMode=_terrainOriginalDepth;
            }
            if(_terrainOutput!=null) {_terrainOutput.Release();Destroy(_terrainOutput);_terrainOutput=null;}
            for(int k=0;k<5;k++) if(_terrainSmall[k]!=null) {_terrainSmall[k].Release();Destroy(_terrainSmall[k]);_terrainSmall[k]=null;}
        }
        private void RestoreTerrainShadows()
        {
            if(_terrainOwnsShadows && QualitySettings.shadows==ShadowQuality.Disable) QualitySettings.shadows=_terrainOriginalShadows;
            _terrainOwnsShadows=false;
        }
        private void DisposeTerrain()
        {
            Camera.onPostRender-=TerrainRestoreCulling;
            foreach(var item in _terrainProjections) TerrainRestoreCulling(item.Key);
            RestoreTerrainShadows();ReleaseTerrainTargets();
        }
    }
}
