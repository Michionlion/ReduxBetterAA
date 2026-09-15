using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationSurfaceTests
    {
        [Test]
        public void SourceCopiesEncodeSdrAndHoldStorageUntilExplicitNativeRetirement()
        {
            var go=new GameObject("FG surface input test");
            var camera=go.AddComponent<Camera>(); camera.enabled=false;
            var color=new RenderTexture(16,8,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
            color.Create();
            var surfaces=new FrameGenerationSurfaces();
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/FrameGenerationInputs.shader");
            var material=new Material(shader);
            var old=RenderTexture.active;
            try
            {
                RenderTexture.active=color; GL.Clear(false,true,new Color(0.25f,0.5f,0.75f,1));
                var token=new ResolvedFrameToken(1,1,1,1,1);
                var view=new ResolvedFrameCamera(camera,camera.projectionMatrix,Vector2.zero,1);
                SceneOutputFrame noSr=default;
                var request=new ResolvedFrameRequest(in token,BackendSelection.CustomTaa,in view,
                    view.ViewProjection,color.descriptor,16,8,in noSr,HistoryResetReason.FirstFrame,
                    true,true,false,1,0,Vector2.one);
                Assert.That(surfaces.Ensure(in request,32,16),Is.True);
                Assert.That(surfaces.TaaFinal.graphicsFormat,Is.EqualTo(color.graphicsFormat));
                Assert.That(surfaces.Hudless.graphicsFormat,Is.EqualTo(GraphicsFormat.R8G8B8A8_UNorm));
                var frame=new BorrowedResolvedFrame(in request,color,Texture2D.whiteTexture,Texture2D.grayTexture);
                surfaces.Capture(in frame,material);
                Color output=Pixel(surfaces.Hudless);
                float expected=request.LinearColorSpace ? Mathf.LinearToGammaSpace(0.25f) : 0.25f;
                Assert.That(output.r,Is.EqualTo(expected).Within(0.006),
                    "Source="+Pixel(color)+"; HUDless="+output+"; depth="+Pixel(surfaces.Depth)+"; motion="+Pixel(surfaces.Motion));
                Assert.That(Pixel(surfaces.Depth).r,Is.EqualTo(1).Within(0.001));
                var previous=surfaces.Hudless;
                surfaces.BeginNativeBorrow();
                Assert.That(surfaces.Ensure(in request,64,32),Is.False,"Native pixels cannot be overwritten by resize");
                Assert.That(surfaces.Release(),Is.False);
                Assert.That(surfaces.Hudless,Is.SameAs(previous));
                Assert.That(previous.IsCreated(),Is.True);
                surfaces.AcknowledgeSourceRetirement();
                Assert.That(surfaces.Release(),Is.True);
            }
            finally
            {
                RenderTexture.active=old;
                surfaces.AcknowledgeSourceRetirement(); surfaces.Release();
                Object.DestroyImmediate(material); Object.DestroyImmediate(color); Object.DestroyImmediate(go);
            }
        }
        [Test]
        public void NativeTransportReversesColorDepthAndMotionRowsTogether()
        {
            var go=new GameObject("FG asymmetric input contract");
            var camera=go.AddComponent<Camera>();camera.enabled=false;
            var color=new RenderTexture(8,4,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);color.Create();
            var pattern=new Texture2D(8,4,TextureFormat.RGBAFloat,false,true) {filterMode=FilterMode.Point};
            var surfaces=new FrameGenerationSurfaces();
            var material=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/ReduxBetterAA/Shaders/FrameGenerationInputs.shader"));
            try
            {
                for(int y=0;y<4;y++)for(int x=0;x<8;x++)pattern.SetPixel(x,y,new Color(0.1f+y*0.2f,0.05f+x*0.1f,0.25f,1));
                pattern.Apply();Graphics.Blit(pattern,color);
                var token=new ResolvedFrameToken(1,1,1,1,1);
                var view=new ResolvedFrameCamera(camera,camera.projectionMatrix,Vector2.zero,1);SceneOutputFrame noSr=default;
                var request=new ResolvedFrameRequest(in token,BackendSelection.CustomTaa,in view,view.ViewProjection,
                    color.descriptor,8,4,in noSr,HistoryResetReason.FirstFrame,true,false,false,1,0,Vector2.one);
                Assert.That(surfaces.Ensure(in request,8,4),Is.True);
                var frame=new BorrowedResolvedFrame(in request,color,pattern,pattern);
                // Match production staging: depth is finalized before motion
                // protection consumes it; the final motion copy comes last.
                surfaces.CaptureColorAndDepth(in frame,material);
                surfaces.CaptureMotion(pattern,material);
                for(int y=0;y<4;y++)
                {
                    float colorValue=Pixel(color,3,3-y).r;
                    float expected=request.LinearColorSpace ? Mathf.LinearToGammaSpace(colorValue) : colorValue;
                    Assert.That(Pixel(surfaces.Hudless,3,y).r,Is.EqualTo(expected).Within(0.006));
                    Assert.That(Pixel(surfaces.Depth,3,y).r,Is.EqualTo(pattern.GetPixel(3,3-y).r).Within(0.001));
                    var motion=Pixel(surfaces.Motion,3,y);
                    Assert.That(motion.r,Is.EqualTo(pattern.GetPixel(3,3-y).r).Within(0.001));
                    Assert.That(motion.g,Is.EqualTo(pattern.GetPixel(3,3-y).g).Within(0.001));
                }
            }
            finally
            {
                surfaces.Release();Object.DestroyImmediate(material);Object.DestroyImmediate(pattern);
                Object.DestroyImmediate(color);Object.DestroyImmediate(go);
            }
        }
        private static Color Pixel(RenderTexture source,int x=-1,int y=-1)
        {
            var old=RenderTexture.active;
            var readback=new Texture2D(source.width,source.height,TextureFormat.RGBAFloat,false,true);
            try
            {
                RenderTexture.active=source;
                readback.ReadPixels(new Rect(0,0,source.width,source.height),0,0); readback.Apply();
                return readback.GetPixel(x < 0 ? source.width/2 : x,y < 0 ? source.height/2 : y);
            }
            finally { RenderTexture.active=old; Object.DestroyImmediate(readback); }
        }
    }
}
