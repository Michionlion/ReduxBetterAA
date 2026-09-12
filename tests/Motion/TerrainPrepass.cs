using System;
using System.Reflection;
using HarmonyLib;
using KSP.Rendering.Planets;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAAVisualTests
{
    public sealed partial class MotionTestMod
    {
        private static readonly FieldInfo ActiveBackendField=typeof(TemporalCoordinator).GetField("_activeBackend",Any);
        private object _pqsBackend;
        private FieldInfo _pqsIndexField,_pqsConfigField;
        private FieldInfo _pqsSpreadField,_pqsLengthField;
        private int _pqsFrame=-1;
        private Matrix4x4 _pqsRaster;
        private Vector2 _pqsJitter;
        private int _pqsCalls;
        private int _pqsDepthFrame=-1,_pqsDecalFrame=-1;
        private Matrix4x4 _pqsDepthRaster,_pqsDecalRaster;
        private struct PqsScope {public Camera camera;public Matrix4x4 original,applied;public bool owns;}
        private void InitializeTerrainPrepass()
        {
            foreach(string name in new[]{"RenderPrepass","DrawPqsDepthNow","DrawPQSDeferredDecalSurfacePass"})
                _patch.Patch(typeof(PQSRenderer).GetMethod(name,Any),
                    prefix:new HarmonyMethod(typeof(MotionTestMod),nameof(PqsBefore)) {priority=Priority.Last},
                    finalizer:new HarmonyMethod(typeof(MotionTestMod),nameof(PqsFinally)));
        }
        private Vector2 UpcomingPqsJitter()
        {
            var backend=ActiveBackendField.GetValue(TemporalCoordinator.Current);
            if(backend==null) return Vector2.zero;
            if(!ReferenceEquals(_pqsBackend,backend)) {
                _pqsBackend=backend;
                _pqsIndexField=backend.GetType().GetField("_frameIndex",Any);
                _pqsConfigField=backend.GetType().GetField("_config",Any);
                if(_pqsConfigField!=null) {
                    _pqsSpreadField=_pqsConfigField.FieldType.GetField("JitterSpread");
                    _pqsLengthField=_pqsConfigField.FieldType.GetField("SequenceLength");
                }
            }
            if(_pqsIndexField==null || _pqsSpreadField==null || _pqsLengthField==null) return Vector2.zero;
            var config=_pqsConfigField.GetValue(backend);
            return SharedJitterSequence.GetCustomOffset((uint)_pqsIndexField.GetValue(backend),(float)_pqsSpreadField.GetValue(config),(int)_pqsLengthField.GetValue(config));
        }
        private static void PqsBefore(PQSRenderer __instance,MethodBase __originalMethod,out PqsScope __state)
        {
            __state=default(PqsScope);
            var s=_instance;var camera=__instance.SourceCamera;
            if(s==null || camera==null) return;
            // Timing windows contain the shipped path, without reflective audit
            // metadata allocation or test-only projection candidates.
            if(s._terrainProfiling) return;
            bool prepass=__originalMethod.Name=="RenderPrepass";
            bool depth=__originalMethod.Name=="DrawPqsDepthNow";
            bool decal=__originalMethod.Name=="DrawPQSDeferredDecalSurfacePass";
            bool change=s._terrainCandidate=="pqs-all-buffers" || (prepass && s._terrainCandidate=="pqs-prepass") ||
                (depth && (s._terrainCandidate=="pqs-depth" || s._terrainCandidate=="pqs-depth-decal")) ||
                (decal && (s._terrainCandidate=="pqs-decal" || s._terrainCandidate=="pqs-depth-decal"));
            if(change) {
                TerrainProjection p;
                bool applied=s._terrainProjections.TryGetValue(camera,out p) && p.frame==Time.frameCount && GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)==p.raster;
                if(!applied) {
                    var jitter=s.UpcomingPqsJitter();
                    __state.camera=camera;__state.original=camera.projectionMatrix;
                    camera.projectionMatrix=camera.orthographic?RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera,jitter):RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera,jitter);
                    __state.applied=camera.projectionMatrix;__state.owns=true;
                }
            }
            if(prepass) {s._pqsFrame=Time.frameCount;s._pqsRaster=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);s._pqsJitter=s.UpcomingPqsJitter();s._pqsCalls++;}
            if(depth) {s._pqsDepthFrame=Time.frameCount;s._pqsDepthRaster=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);}
            if(decal) {s._pqsDecalFrame=Time.frameCount;s._pqsDecalRaster=GL.GetGPUProjectionMatrix(camera.projectionMatrix,true);}
        }
        private static Exception PqsFinally(Exception __exception,PqsScope __state)
        {
            if(__state.owns && __state.camera!=null && __state.camera.projectionMatrix==__state.applied) __state.camera.projectionMatrix=__state.original;
            return __exception;
        }
    }
}
