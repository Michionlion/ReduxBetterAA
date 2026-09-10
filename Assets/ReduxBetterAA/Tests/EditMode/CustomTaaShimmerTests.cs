using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReduxBetterAA.Tests
{
    public sealed class CustomTaaShimmerTests
    {
        const int Size=48;
        Material _old,_candidate,_panBaseline;
        Texture2D _source,_history,_depth,_oldDepth,_motion,_read;
        RenderTexture _output;
        Vector4 _z;
        [SetUp] public void Setup()
        {
            _old=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Tests/EditMode/Fixtures/CustomTaaShimmerBaseline.shader"));
            _candidate=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/CustomTaa.shader"));
            _panBaseline=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Tests/EditMode/Fixtures/CustomTaaPanBaseline.shader"));
            _source=Texture();_history=Texture();_depth=Texture();_oldDepth=Texture();_motion=Texture();_read=Texture();
            _output=new RenderTexture(Size,Size,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);_output.Create();
            _z=Shader.GetGlobalVector("_ZBufferParams");Shader.SetGlobalVector("_ZBufferParams",new Vector4(1,1,0,0));
            Fill(_depth,(x,y)=>Color.white);Fill(_oldDepth,(x,y)=>new Color(.5f,0,0,0));Fill(_motion,(x,y)=>Color.clear);
            foreach(var m in new[]{_old,_candidate,_panBaseline}) {
                m.SetTexture("_HistoryTex",_history);m.SetTexture("_HistoryDepthTex",_oldDepth);
                m.SetTexture("_CameraDepthTexture",_depth);m.SetTexture("_ReduxBetterAAMotionVectors",_motion);
                m.SetVector("_SourceDimensions",new Vector4(Size,Size,1f/Size,1f/Size));
                m.SetFloat("_HistoryValid",1);m.SetFloat("_StationaryHistory",.99f);m.SetFloat("_MovingHistory",.1f);
                m.SetFloat("_MotionResponsePixels",8);m.SetFloat("_MaximumMotionPixels",256);m.SetFloat("_DepthThreshold",.01f);
                m.SetFloat("_DepthEdgeStability",.75f);m.SetFloat("_VarianceGamma",1.25f);m.SetFloat("_ReactiveScale",2);
                m.SetFloat("_NoDepthHistory",.25f);
            }
            _candidate.SetFloat("_MotionResponsePixels",Configuration.CustomTaaConfig.Conservative.MotionResponsePixels);
        }
        [TearDown] public void Cleanup()
        {
            Shader.SetGlobalVector("_ZBufferParams",_z);
            foreach(Object o in new Object[]{_old,_candidate,_panBaseline,_source,_history,_depth,_oldDepth,_motion,_read,_output}) Object.DestroyImmediate(o);
        }

        [TestCase(.25f,0f)] [TestCase(.5f,0f)] [TestCase(1f,0f)]
        [TestCase(0f,.5f)] [TestCase(.5f,.5f)] [TestCase(-.5f,.25f)]
        public void MovingThinFencePreservesDetailAndStability(float horizontal,float vertical)
        {
            var baseline=Sequence(_panBaseline,horizontal,vertical);
            var candidate=Sequence(_candidate,horizontal,vertical);
            TestContext.WriteLine("Moving fence baseline MSE="+baseline.x+" residual variance="+baseline.y+
                " candidate MSE="+candidate.x+" residual variance="+candidate.y);
            Assert.That(candidate.x,Is.LessThanOrEqualTo(baseline.x*1.02f));
            Assert.That(candidate.y,Is.LessThanOrEqualTo(baseline.y*1.02f));
        }

        [Test] public void ThinObjectStopAndReversalDoesNotLeaveATrail()
        {
            // Independent object coverage/depth, including motion reversal and stop.
            // Test outside the current reconstruction footprint, where stale color
            // cannot be justified by spatial filtering of this frame's thin line.
            Fill(_history,(x,y)=>Color.clear);
            float previous=12.4f;
            for(uint f=0;f<80;f++) {
                float position=12.4f+(f<32?0:f<48?(f-31)*.5f:f<64?(63-f)*.5f:0);
                Vector2 jitter=Rendering.SharedJitterSequence.GetCustomOffset(f,.75f,8);
                Fill(_source,(x,y)=>Mathf.Abs(x+.5f+jitter.x-position)<.45f?Color.white:Color.black);
                Fill(_depth,(x,y)=>Mathf.Abs(x+.5f+jitter.x-position)<.45f?Color.white:Color.black);
                Fill(_motion,(x,y)=>new Color((position-previous)/Size,0,0,0));
                _candidate.SetVector("_Jitter",jitter/Size);_candidate.SetFloat("_HistoryValid",f==0?0:1);
                Color[] result=Render(_candidate,0);_history.SetPixels(result);_history.Apply();
                Color[] depth=Render(_candidate,1);_oldDepth.SetPixels(depth);_oldDepth.Apply();
                for(int x=4;x<Size-4;x++) if(Mathf.Abs(x+.5f-position)>2)
                    Assert.That(result[24*Size+x].r,Is.LessThan(.005f),"No trail at frame "+f+", x="+x);
                previous=position;
            }
        }

        [TestCase(0f)] [TestCase(.125f)] [TestCase(.25f)] [TestCase(.5f)] [TestCase(1f)]
        public void SubpixelFenceReducesTemporalReferenceError(float speed)
        {
            var old=Sequence(_old,speed);var candidate=Sequence(_candidate,speed);
            TestContext.WriteLine("Fence baseline MSE="+old.x+" variance="+old.y+" candidate MSE="+candidate.x+" variance="+candidate.y);
            Assert.That(candidate.y,Is.LessThan(old.y*(speed==0?.5f:1.02f)));
            Assert.That(candidate.x,Is.LessThanOrEqualTo(old.x*1.02f));
        }

        [Test] public void UniformEmissionChangeRemainsFullyReactive()
        {
            Fill(_source,(x,y)=>new Color(.9f,.9f,.9f,1));Fill(_history,(x,y)=>new Color(.1f,.1f,.1f,1));
            _candidate.SetFloat("_DebugMode",5);
            Assert.That(Render(_candidate,3)[Size*Size/2].r,Is.GreaterThan(.99f));
        }

        [TestCase(.25f)] [TestCase(1f)] [TestCase(4f)]
        public void PixelScaleUniformEmissionRetainsExistingReactiveResponse(float speed)
        {
            Fill(_source,(x,y)=>new Color(.9f,.9f,.9f,1));
            Fill(_history,(x,y)=>new Color(.1f,.1f,.1f,1));
            Fill(_motion,(x,y)=>new Color(speed/Size,0,0,0));
            _old.SetFloat("_DebugMode",5);_candidate.SetFloat("_DebugMode",5);
            var old=Render(_old,3);var candidate=Render(_candidate,3);
            for(int i=0;i<old.Length;i++) Assert.That(Vector4.Distance(old[i],candidate[i]),Is.LessThan(1e-6f));
        }

        private Vector2 Sequence(Material m,float speed,float vertical=0)
        {
            // A periodic thin fence has an independent 64-sample pixel-box reference.
            double mse=0;int count=0;
            var reference=new double[Size*Size];var total=new double[Size*Size];var square=new double[Size*Size];
            Fill(_motion,(x,y)=>new Color(speed/Size,vertical/Size,0,0));
            Fill(_history,(x,y)=>Color.clear);
            for(uint f=0;f<96;f++) {
                Vector2 jitter=Rendering.SharedJitterSequence.GetCustomOffset(f,.75f,8);
                float shift=f*speed;
                Fill(_source,(x,y)=>{float v=Signal(x+.5f+jitter.x-shift,y+.5f+jitter.y-f*vertical);return new Color(v,v,v,1);});
                m.SetVector("_Jitter",jitter/Size);m.SetFloat("_HistoryValid",f==0?0:1);
                Color[] result=Render(m,0);_history.SetPixels(result);_history.Apply();
                if(f<64)continue;
                Array.Clear(reference,0,reference.Length);
                for(int y0=4;y0<Size-4;y0++)for(int x0=4;x0<Size-4;x0++)
                    for(int y=0;y<8;y++)for(int x=0;x<8;x++)reference[y0*Size+x0]+=Signal(x0+(x+.5f)/8-shift,y0+(y+.5f)/8-f*vertical)/64;
                for(int y=4;y<Size-4;y++)for(int x=4;x<Size-4;x++) {
                    int i=y*Size+x;double error=result[i].r-reference[i];
                    mse+=error*error;total[i]+=error;square[i]+=error*error;
                }
                count++;
            }
            double variance=0;int pixels=(Size-8)*(Size-8);
            for(int i=0;i<total.Length;i++)variance+=Math.Max(0,square[i]/count-Math.Pow(total[i]/count,2));
            return new Vector2((float)(mse/count/pixels),(float)(variance/pixels));
        }
        private static float Signal(float x,float y) => Mathf.Repeat(x+.31f*y,3.7f)<.75f ? .9f : .1f;
        private static Texture2D Texture()=>new Texture2D(Size,Size,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
        private static void Fill(Texture2D t,Func<int,int,Color> f) {var p=new Color[Size*Size];for(int y=0;y<Size;y++)for(int x=0;x<Size;x++)p[y*Size+x]=f(x,y);t.SetPixels(p);t.Apply();}
        private Color[] Render(Material m,int pass) {
            var prior=RenderTexture.active;try{Graphics.Blit(_source,_output,m,pass);RenderTexture.active=_output;_read.ReadPixels(new Rect(0,0,Size,Size),0,0);_read.Apply();return _read.GetPixels();}finally{RenderTexture.active=prior;}
        }
    }
}
