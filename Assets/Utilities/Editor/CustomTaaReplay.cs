using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Utilities.Editor
{
    // Optional captured-input experiment, invoked explicitly by Run-TaaReplay.ps1.
    // The crop boundary is artificial; analysis excludes a 32-pixel border.
    public static class CustomTaaReplay
    {
        [Serializable] public sealed class Plan { public string input, output; public Arm[] arms; }
        [Serializable] public sealed class Arm { public string name, shader; public Setting[] settings; }
        [Serializable] public sealed class Setting { public string name; public float value; }
        [Serializable] private sealed class Metadata { public Vector4 z; public int depthFilter, motionFilter; }
        [Serializable] private sealed class Completion { public int framesPerArm=128, arms; public string gpu, unity; }

        public static void Run()
        {
            string planPath=Environment.GetEnvironmentVariable("RBAA_REPLAY_PLAN");
            if(string.IsNullOrEmpty(planPath)) throw new InvalidOperationException("Set RBAA_REPLAY_PLAN to run captured-input experiments.");
            var plan=JsonUtility.FromJson<Plan>(File.ReadAllText(planPath));
            var metadata=JsonUtility.FromJson<Metadata>(File.ReadAllText(Path.Combine(plan.input,"replay.json")));
            var frames=File.ReadAllLines(Path.Combine(plan.input,"frames.csv"));
            if(frames.Length!=129) throw new InvalidDataException("Expected 128 consecutive frames");
            int firstFrame=int.Parse(frames[1].Split(',')[1],CultureInfo.InvariantCulture);
            for(int f=0;f<128;f++) {
                var row=frames[f+1].Split(',');
                if(int.Parse(row[0])!=f || int.Parse(row[1])!=firstFrame+f || row[3]!="Custom TAA" || row[6]!="True")
                    throw new InvalidDataException("Replay requires consecutive Custom TAA frames without resets or fallback");
            }
            Vector4 priorZ=Shader.GetGlobalVector("_ZBufferParams");
            RenderTexture prior=RenderTexture.active;
            try {
                Shader.SetGlobalVector("_ZBufferParams",metadata.z);
                foreach(string region in new[]{"structure","terrain"}) {
                    int size=region=="structure"?512:256;
                    var source=Texture(size,TextureFormat.RGBAHalf,FilterMode.Bilinear);
                    var depth=Texture(size,TextureFormat.RGBAFloat,(FilterMode)metadata.depthFilter);
                    var motion=Texture(size,TextureFormat.RGBAFloat,(FilterMode)metadata.motionFilter);
                    var initial=Texture(size,TextureFormat.RGBAHalf,FilterMode.Bilinear);
                    var initialDepth=Texture(size,TextureFormat.RGBAFloat,FilterMode.Bilinear);
                    var read=Texture(size,TextureFormat.RGBAHalf,FilterMode.Bilinear);
                    var history=Target(size,RenderTextureFormat.ARGBHalf);
                    var next=Target(size,RenderTextureFormat.ARGBHalf);
                    var historyDepth=Target(size,RenderTextureFormat.RFloat);
                    var nextDepth=Target(size,RenderTextureFormat.RFloat);
                    var output=Target(size,RenderTextureFormat.ARGBHalf);
                    try {
                        var inputs=new byte[128][]; var depths=new byte[128][]; var motions=new byte[128][];
                        for(int f=0;f<128;f++) {
                            inputs[f]=Bytes(plan.input,f,"input",region,"16");
                            depths[f]=Bytes(plan.input,f,"depth",region,"32");
                            motions[f]=Bytes(plan.input,f,"motion",region,"32");
                            var values=new float[size*size*4]; Buffer.BlockCopy(motions[f],0,values,0,motions[f].Length);
                            for(int i=0;i<values.Length;i+=4) {
                                float mx=values[i]*2560, my=values[i+1]*1440;
                                if(float.IsNaN(mx) || float.IsNaN(my) || mx*mx+my*my>=256*256)
                                    throw new InvalidDataException("Crop replay cannot reproduce matrix fallback for invalid/extreme motion");
                                values[i]=mx/size; values[i+1]=my/size;
                            }
                            Buffer.BlockCopy(values,0,motions[f],0,motions[f].Length);
                        }
                        Load(initial,Bytes(plan.input,0,"history",region,"16"));
                        Load(initialDepth,Bytes(plan.input,0,"history-depth",region,"32"));
                        foreach(var arm in plan.arms) {
                            var shader=AssetDatabase.LoadAssetAtPath<Shader>(arm.shader);
                            if(shader==null || !shader.isSupported) throw new InvalidDataException("Missing/unsupported shader "+arm.shader);
                            var material=new Material(shader);
                            try {
                                Graphics.Blit(initial,history); Graphics.Blit(initialDepth,historyDepth);
                                material.SetTexture("_CameraDepthTexture",depth); material.SetTexture("_ReduxBetterAAMotionVectors",motion);
                                material.SetVector("_SourceDimensions",new Vector4(size,size,1f/size,1f/size));
                                material.SetFloat("_HistoryValid",1); material.SetFloat("_MatrixHistoryValid",0);
                                material.SetFloat("_StationaryHistory",.99f); material.SetFloat("_MovingHistory",.1f);
                                material.SetFloat("_MotionResponsePixels",8); material.SetFloat("_MaximumMotionPixels",256);
                                material.SetFloat("_DepthThreshold",.01f); material.SetFloat("_DepthEdgeStability",.75f);
                                material.SetFloat("_VarianceGamma",1.25f); material.SetFloat("_ReactiveScale",2);
                                material.SetFloat("_NoDepthHistory",.25f); material.SetFloat("_Sharpening",.24f);
                                if(arm.settings!=null)foreach(var setting in arm.settings)material.SetFloat(setting.name,setting.value);
                                string directory=Path.Combine(plan.output,arm.name); Directory.CreateDirectory(directory);
                                for(int f=0;f<128;f++) {
                                    Load(source,inputs[f]); Load(depth,depths[f]); Load(motion,motions[f]);
                                    string[] frame=frames[f+1].Split(',');
                                    material.SetVector("_Jitter",new Vector2(float.Parse(frame[4],CultureInfo.InvariantCulture)*2560/size,
                                        float.Parse(frame[5],CultureInfo.InvariantCulture)*1440/size));
                                    material.SetTexture("_HistoryTex",history); material.SetTexture("_HistoryDepthTex",historyDepth);
                                    Graphics.Blit(source,next,material,0); Graphics.Blit(source,nextDepth,material,1);
                                    Graphics.Blit(next,output,material,2);
                                    RenderTexture.active=output; read.ReadPixels(new Rect(0,0,size,size),0,0,false);
                                    File.WriteAllBytes(Path.Combine(directory,f.ToString("D3")+"-output-"+region+".rgba16f"),read.GetRawTextureData());
                                    var swap=history; history=next; next=swap;
                                    swap=historyDepth; historyDepth=nextDepth; nextDepth=swap;
                                }
                                Debug.Log("Replay complete: "+arm.name+" / "+region);
                            } finally { Object.DestroyImmediate(material); }
                        }
                    } finally {
                        foreach(Object item in new Object[]{source,depth,motion,initial,initialDepth,read,history,next,historyDepth,nextDepth,output})
                            Object.DestroyImmediate(item);
                    }
                }
                Directory.CreateDirectory(plan.output); File.WriteAllText(Path.Combine(plan.output,"plan.json"),JsonUtility.ToJson(plan,true));
                File.WriteAllText(Path.Combine(plan.output,"complete.json"),JsonUtility.ToJson(new Completion {
                    arms=plan.arms.Length,gpu=SystemInfo.graphicsDeviceName,unity=Application.unityVersion
                },true));
            } finally { Shader.SetGlobalVector("_ZBufferParams",priorZ); RenderTexture.active=prior; }
        }
        private static byte[] Bytes(string path,int frame,string kind,string region,string bits) =>
            File.ReadAllBytes(Path.Combine(path,frame.ToString("D3")+"-"+kind+"-"+region+".rgba"+bits+"f"));
        private static void Load(Texture2D texture,byte[] bytes) {
            int stride=texture.format==TextureFormat.RGBAHalf?8:16;
            if(bytes.Length!=texture.width*texture.height*stride) throw new InvalidDataException("Unexpected raw texture length");
            texture.LoadRawTextureData(bytes); texture.Apply(false,false);
        }
        private static Texture2D Texture(int size,TextureFormat format,FilterMode filter) =>
            new Texture2D(size,size,format,false,true){filterMode=filter,wrapMode=TextureWrapMode.Clamp};
        private static RenderTexture Target(int size,RenderTextureFormat format) {
            var rt=new RenderTexture(size,size,0,format,RenderTextureReadWrite.Linear){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            rt.Create();return rt;
        }
    }
}
