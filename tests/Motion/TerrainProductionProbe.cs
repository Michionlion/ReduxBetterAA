using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using KSP.Rendering.Planets;
using MoonSharp.Interpreter;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAAVisualTests
{
    public sealed partial class MotionTestMod
    {
        [Serializable] public sealed class TerrainProfileFrame {public int frame;public double ms,deltaMs;public long allocated;}
        [Serializable] public sealed class TerrainProfile {public string candidate,backend;public TerrainProfileFrame[] frames;}
        private TerrainProfileFrame[] _terrainProfile;
        private int _terrainProfileIndex;
        private string _terrainProfileLabel;
        private bool _terrainProfiling,_terrainProfileWritten,_terrainProductionAvailable;
        private struct TerrainProfileScope {public bool active;public long start,allocated;}
        private void InitializeTerrainProductionProbe()
        {
            var type=typeof(TemporalCoordinator).Assembly.GetType("ReduxBetterAA.Rendering.AuxiliaryProjectionScope");
            _terrainProductionAvailable=type!=null;
            if(type!=null) foreach(string backend in new[]{"CustomTaaBackend","NvidiaDlaaBackend","AmdFsr2Backend"})
                _patch.Patch(typeof(TemporalCoordinator).Assembly.GetType("ReduxBetterAA.Backends."+backend).GetMethod("TryGetRasterProjection"),
                    prefix:new HarmonyMethod(typeof(MotionTestMod),nameof(TerrainProductionGate)));
            _patch.Patch(typeof(PQSRenderer).GetMethod("DrawPqsDepthNow",Any),
                prefix:new HarmonyMethod(typeof(TerrainProfiler),nameof(TerrainProfiler.Before)) {priority=Priority.First},
                finalizer:new HarmonyMethod(typeof(TerrainProfiler),nameof(TerrainProfiler.After)) {priority=Priority.Last});
        }
        private static bool TerrainProductionGate(ref bool __result)
        {
            if(_instance==null || _instance._terrainCandidate!="production-bypass") return true;
            __result=false;return false;
        }
        private void BindTerrainProductionProbe(Table api)
        {
            Bind(api,"terrain_production",(c,a)=>DynValue.NewBoolean(_terrainProductionAvailable));
            Bind(api,"terrain_profile_start",(c,a)=>{
                int count=(int)a[1].Number;
                string label=a[0].String;
                if(count<60 || count>1200 || _terrainRecording || _terrainPending!=0 || string.IsNullOrEmpty(label) || label.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                    throw new ArgumentException("Invalid profile or readback still active");
                if(File.Exists(Path.Combine(_root,label+"-profile.json"))) throw new InvalidOperationException("Profile already exists");
                _terrainProfile=new TerrainProfileFrame[count];for(int i=0;i<count;i++) _terrainProfile[i]=new TerrainProfileFrame();
                _terrainProfileIndex=0;_terrainProfileLabel=label;_terrainProfileWritten=false;_terrainProfiling=true;return DynValue.Nil;
            });
            Bind(api,"terrain_profile_ready",(c,a)=>{
                if(!_terrainProfiling && !_terrainProfileWritten) {
                    // JsonUtility omits arrays of late-loaded adapter classes
                    // in this player. Serialize each frame as in TerrainCapture.
                    string header=JsonUtility.ToJson(new TerrainProfile {
                        candidate=_terrainCandidate,backend=TemporalCoordinator.Current.SelectedBackend});
                    using(var writer=new StreamWriter(Path.Combine(_root,_terrainProfileLabel+"-profile.json"))) {
                        writer.Write(header.Substring(0,header.Length-1));writer.Write(",\"frames\":[");
                        for(int i=0;i<_terrainProfile.Length;i++) {if(i!=0) writer.Write(',');writer.Write(JsonUtility.ToJson(_terrainProfile[i]));}
                        writer.Write("]}");
                    }
                    _terrainProfileWritten=true;
                }
                return DynValue.NewBoolean(_terrainProfileWritten);
            });
        }
        // Harmony keys __state by the patch declaring type. Keep the profiler
        // separate from the PQS candidate scope on the same original method.
        private static class TerrainProfiler
        {
        public static void Before(out TerrainProfileScope __state)
        {
            __state=default(TerrainProfileScope);var s=_instance;
            if(s==null || !s._terrainProfiling) return;
            __state.active=true;__state.allocated=GC.GetAllocatedBytesForCurrentThread();__state.start=Stopwatch.GetTimestamp();
        }
        public static Exception After(Exception __exception,TerrainProfileScope __state)
        {
            if(!__state.active) return __exception;
            long end=Stopwatch.GetTimestamp();long allocated=GC.GetAllocatedBytesForCurrentThread()-__state.allocated;
            var s=_instance;var p=s._terrainProfile[s._terrainProfileIndex];p.frame=Time.frameCount;p.ms=(end-__state.start)*1000.0/Stopwatch.Frequency;
            p.deltaMs=Time.unscaledDeltaTime*1000;p.allocated=allocated;
            if(++s._terrainProfileIndex==s._terrainProfile.Length) s._terrainProfiling=false;
            return __exception;
        }
        }
    }
}
