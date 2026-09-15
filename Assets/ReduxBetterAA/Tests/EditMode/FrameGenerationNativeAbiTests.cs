using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationNativeAbiTests
    {
        [Test]
        public void NativeWireSizesAndOffsetsMatchThePack8Header()
        {
            Assert.That(IntPtr.Size,Is.EqualTo(8));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.Config>(),Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.RowMatrix>(),Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.Camera>(),Is.EqualTo(304));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.Capture>(),Is.EqualTo(368));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.TicketStatus>(),Is.EqualTo(40));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.Status>(),Is.EqualTo(280));
            Assert.That((int)Marshal.OffsetOf<FrameGenerationNative.Capture>("View"),Is.EqualTo(64));
            Assert.That((int)Marshal.OffsetOf<FrameGenerationNative.Status>("SourceWindow"),Is.EqualTo(112));
        }
        [Test]
        public void AdditiveAmdStatusPreservesTheInlineLegacyReasonAndNumericOffsets()
        {
            Assert.That(Marshal.SizeOf<FrameGenerationNative.ConfigV2>(),Is.EqualTo(64));
            Assert.That((int)Marshal.OffsetOf<FrameGenerationNative.ConfigV2>("RenderWidth"),Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<FrameGenerationNative.StatusV2>(),Is.EqualTo(904));
            var memory=Marshal.AllocHGlobal(904);
            try
            {
                for(int offset=0;offset<904;offset+=4)Marshal.WriteInt32(memory,offset,0);
                Marshal.WriteInt32(memory,0,904); Marshal.WriteInt32(memory,4,2);
                Marshal.WriteInt32(memory,8,792); Marshal.WriteInt32(memory,12,1);
                Marshal.WriteInt32(memory,288,unchecked((int)0x44434241));
                Marshal.WriteInt32(memory,800,4); Marshal.WriteInt32(memory,808,4);
                Marshal.WriteInt64(memory,824,0x1122334455667788);
                Marshal.WriteInt64(memory,864,9001); Marshal.WriteInt32(memory,900,1);
                var status=Marshal.PtrToStructure<FrameGenerationNative.StatusV2>(memory);
                Assert.That(status.Base.Size,Is.EqualTo(792)); Assert.That(status.Base.Version,Is.EqualTo(1));
                Assert.That(status.AmdBestMask,Is.EqualTo(4)); Assert.That(status.AmdBestFamily,Is.EqualTo(4));
                Assert.That(status.AmdBestVersion,Is.EqualTo(0x1122334455667788ul));
                Assert.That(status.AmdNativePresentCountDelta,Is.EqualTo(9001));
                Assert.That(status.AmdNativePresentCountValid,Is.EqualTo(1));
                Assert.That(Marshal.PtrToStringAnsi(IntPtr.Add(memory,288),4),Is.EqualTo("ABCD"));
            }
            finally {Marshal.FreeHGlobal(memory);}
        }
        [Test]
        public void MatrixWireStorageIsRowIndexedWithoutChangingUnityMultiplicationConvention()
        {
            var m=new Matrix4x4();
            for (int row=0;row<4;row++) for (int col=0;col<4;col++) m[row,col]=row*10+col;
            var wire=new FrameGenerationNative.RowMatrix(m);
            Assert.That(wire.M01,Is.EqualTo(1)); Assert.That(wire.M10,Is.EqualTo(10));
            Assert.That(wire.M23,Is.EqualTo(23)); Assert.That(wire.M32,Is.EqualTo(32));
        }
        [Test]
        public void RenderTargetProjectionNormalizesToTheSameNativeClipAndRasterJitter()
        {
            var projection=Matrix4x4.Perspective(60,16f/9,0.5f,10000);
            var screen=GL.GetGPUProjectionMatrix(projection,false);
            var texture=GL.GetGPUProjectionMatrix(projection,true);
            var canonical=FrameGenerationNative.Camera.NativeClip(texture,true);
            var point=new Vector4(2,3,-20,1);
            var actual=canonical*point; var expected=screen*point;
            Assert.That((actual-expected).magnitude,Is.LessThan(0.00001f));
            var go=new GameObject("FG raster jitter contract");
            try
            {
                var camera=go.AddComponent<UnityEngine.Camera>();camera.enabled=false;
                var jitter=new Vector2(0.25f,-0.375f);
                var view=new ResolvedFrameCamera(camera,projection,jitter,1);
                ResolvedFrameToken token=default;SceneOutputFrame noSr=default;
                var request=new ResolvedFrameRequest(in token,BackendSelection.CustomTaa,in view,view.ViewProjection,
                    new RenderTextureDescriptor(64,32),64,32,in noSr,HistoryResetReason.None,false,false,false,1,16,Vector2.one);
                var wire=new FrameGenerationNative.Camera(in request);
                var jittered=projection;jittered.m02+=2*jitter.x/64;jittered.m12+=2*jitter.y/32;
                var raster=GL.GetGPUProjectionMatrix(jittered,false)*point;
                Assert.That((raster.x/raster.w-expected.x/expected.w)*32,Is.EqualTo(wire.JitterX).Within(0.00001));
                Assert.That(-(raster.y/raster.w-expected.y/expected.w)*16,Is.EqualTo(wire.JitterY).Within(0.00001));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        [Test]
        public void BoundOwnerCannotQueueTheFirstEofWithDefaultOrLostEventMetadata()
        {
            FrameGenerationNative.Status status=default;
            Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(true,77,in status),Is.False,
                "A successful owner tick alone must not queue the default event0 and strand its packet");
            status=new FrameGenerationNative.Status { Size=792,Version=1,Loaded=1,Renderer=2,
                CaptureEventId=20,EndOfFrameEventId=21 };
            Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(true,77,in status),Is.True);
            Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(false,77,in status),Is.False);
            Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(true,0,in status),Is.False);
            status=default; // ReadStatus clears its out parameter on a failed read.
            Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(true,78,in status),Is.False,
                "An earlier successful owner bind cannot authorize default IDs after a read failure");
        }
        [Test]
        public void RegisteredEventPairRequiresTheLoadedExactAbiAndSignedNativeIds()
        {
            var valid=new FrameGenerationNative.Status { Size=792,Version=1,Loaded=1,Renderer=2,
                CaptureEventId=20,EndOfFrameEventId=21 };
            var status=valid;status.Loaded=0;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            status=valid;status.Size=280;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            status=valid;status.Version=2;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            status=valid;status.Renderer=18;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            status=valid;status.EndOfFrameEventId=20;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            status=valid;status.EndOfFrameEventId=22;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            status=valid;status.CaptureEventId=int.MaxValue;status.EndOfFrameEventId=(uint)int.MaxValue+1;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.False);
            // ReserveEventIDRange may legitimately allocate the first pair at0.
            status=valid;status.CaptureEventId=0;status.EndOfFrameEventId=1;
            Assert.That(FrameGenerationRuntime.HasRegisteredNativeEvents(in status),Is.True);
        }
        [Test]
        public void PendingWindowRetirementKeepsEofEnabledWithoutAcceptingWrongThread()
        {
            var draining=new FrameGenerationNative.Status { Size=792,Version=1,Loaded=1,Renderer=2,
                CaptureEventId=20,EndOfFrameEventId=21,State=4 };
            Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(
                FrameGenerationRuntime.NativeOwnerTickSucceeded(1),78,in draining),Is.True,
                "A pending owner tick must keep the Off/retirement EOF pump alive");
            Assert.That(FrameGenerationRuntime.NativeOwnerTickSucceeded(0),Is.True);
            foreach(uint result in new uint[] {2,3,4,5,6,7,8,9,10,uint.MaxValue})
                Assert.That(FrameGenerationRuntime.CanQueueNativeEndOfFrame(
                    FrameGenerationRuntime.NativeOwnerTickSucceeded(result),78,in draining),Is.False);
        }
        [Test]
        public void PhysicalOutputMayDifferInSizeButNotInViewportAspect()
        {
            Assert.That(FrameGenerationRuntime.SameAspect(1920,1080,2560,1440),Is.True);
            Assert.That(FrameGenerationRuntime.SameAspect(1920,1080,2560,1600),Is.False);
            Assert.That(FrameGenerationRuntime.SameAspect(0,1080,2560,1440),Is.False);
        }
        [TestCase(1,1)]
        [TestCase(-1,-1)]
        [TestCase(1,-1)]
        public void StoredMotionConvertsIntoPreviousMinusCurrentForEveryAaSign(float signX,float signY)
        {
            ResolvedFrameToken token=default; ResolvedFrameCamera view=default; SceneOutputFrame noSr=default;
            var request=new ResolvedFrameRequest(in token,BackendSelection.CustomTaa,in view,Matrix4x4.identity,
                new RenderTextureDescriptor(64,32),64,32,in noSr,HistoryResetReason.None,
                false,false,false,1,16,new Vector2(signX,signY));
            var wire=new FrameGenerationNative.Camera(in request);
            // A feature moved right 2px and down 3px: the sanitizer's base value
            // is current-previous. Both TAA and vendor sign settings must map
            // its stored output back to the same previous sample location.
            Assert.That((2f/64)*signX*wire.MotionScaleX,Is.EqualTo(-2f/64));
            Assert.That((-3f/32)*signY*wire.MotionScaleY,Is.EqualTo(-3f/32));
        }
    }
}
