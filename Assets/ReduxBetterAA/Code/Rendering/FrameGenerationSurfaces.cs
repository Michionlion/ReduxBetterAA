using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    // A managed source lease, distinct from the native interop slot. The caller
    // must acknowledge the native copy fence before recycling or destroying it.
    internal sealed class FrameGenerationSurfaces
    {
        private static readonly List<FrameGenerationSurfaces> Quarantined = new List<FrameGenerationSurfaces>();
        internal RenderTexture TaaFinal { get; private set; }
        internal RenderTexture Hudless { get; private set; }
        internal RenderTexture Depth { get; private set; }
        internal RenderTexture Motion { get; private set; }
        internal bool BorrowedByNative { get; private set; }
        internal IntPtr HudlessPointer { get; private set; }
        internal IntPtr DepthPointer { get; private set; }
        internal IntPtr MotionPointer { get; private set; }

        internal bool Ensure(in ResolvedFrameRequest request, int physicalWidth, int physicalHeight)
        {
            if (BorrowedByNative) return false;
            if (physicalWidth < 1 || physicalHeight < 1 || request.RenderWidth < 1 || request.RenderHeight < 1)
                return false;
            EnsureTexture(ref _hudless, physicalWidth, physicalHeight, GraphicsFormat.R8G8B8A8_UNorm, "FG HUD-less SDR");
            EnsureTexture(ref _depth, request.RenderWidth, request.RenderHeight, GraphicsFormat.R32_SFloat, "FG raw device depth");
            EnsureTexture(ref _motion, request.RenderWidth, request.RenderHeight, GraphicsFormat.R16G16_SFloat, "FG normalized motion");
            if (request.NeedsFinalColorTarget)
            {
                var desc = request.ColorDescriptor;
                desc.depthBufferBits = 0; desc.depthStencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1; desc.useMipMap = false; desc.autoGenerateMips = false;
                desc.enableRandomWrite = false; desc.useDynamicScale = false; desc.bindMS = false;
                if (_taaFinal == null || !_taaFinal.IsCreated() || _taaFinal.descriptor.width != desc.width ||
                    _taaFinal.descriptor.height != desc.height || _taaFinal.graphicsFormat != desc.graphicsFormat)
                {
                    Release(ref _taaFinal);
                    _taaFinal = new RenderTexture(desc) { name = "FG TAA final resolve", hideFlags = HideFlags.HideAndDontSave };
                    if (!_taaFinal.Create()) throw new InvalidOperationException("Could not allocate FG TAA resolve");
                }
            }
            // GetNativeTexturePtr can synchronize Unity's renderer; obtain it
            // only when an allocation changes, never for every captured frame.
            if (!ReferenceEquals(Hudless, _hudless)) HudlessPointer = _hudless.GetNativeTexturePtr();
            if (!ReferenceEquals(Depth, _depth)) DepthPointer = _depth.GetNativeTexturePtr();
            if (!ReferenceEquals(Motion, _motion)) MotionPointer = _motion.GetNativeTexturePtr();
            TaaFinal = _taaFinal; Hudless = _hudless; Depth = _depth; Motion = _motion;
            return true;
        }

        private RenderTexture _taaFinal, _hudless, _depth, _motion;

        internal void Capture(in BorrowedResolvedFrame frame, Material material, Texture protectedMotion = null)
        {
            CaptureColorAndDepth(in frame, material);
            CaptureMotion(protectedMotion != null ? protectedMotion : frame.SanitizedMotion, material);
        }

        internal void CaptureColorAndDepth(in BorrowedResolvedFrame frame, Material material)
        {
            if (BorrowedByNative || material == null || frame.RawDeviceDepth == null || frame.SanitizedMotion == null)
                throw new InvalidOperationException("FG sources are missing or their previous copy is still live");
            bool previousSrgbWrite = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false; // Pass 0 explicitly encodes into UNORM.
                material.SetFloat("_EncodeSrgb", frame.Request.LinearColorSpace ? 1 : 0);
                // Unity's resolved render textures and depth/motion use bottom-
                // left UVs. The native D3D backbuffer and SDK resources use top-
                // left rows. Convert all three inputs together at this boundary.
                material.SetFloat("_NativeFlipY",1);
                Graphics.Blit(frame.ResolvedColor, Hudless, material, 0);
                Graphics.Blit(frame.RawDeviceDepth, Depth, material, 1);
            }
            finally { GL.sRGBWrite = previousSrgbWrite; }
        }

        internal void CaptureMotion(Texture protectedMotion, Material material)
        {
            if (BorrowedByNative || material == null || protectedMotion == null)
                throw new InvalidOperationException("FG motion input is missing or its previous copy is still live");
            bool previousSrgbWrite = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false;
                material.SetFloat("_NativeFlipY", 1);
                Graphics.Blit(protectedMotion, Motion, material, 2);
            }
            finally { GL.sRGBWrite = previousSrgbWrite; }
        }

        internal void BeginNativeBorrow()
        {
            if (BorrowedByNative) throw new InvalidOperationException("FG sources are already borrowed");
            BorrowedByNative = true;
        }
        // Only a native source-copy acknowledgment may call this. A consumed CPU
        // event, Submit return, scene transition or elapsed time is insufficient.
        internal void AcknowledgeSourceRetirement() { BorrowedByNative = false; }

        internal bool Release()
        {
            if (BorrowedByNative) return false;
            Release(ref _taaFinal); Release(ref _hudless); Release(ref _depth); Release(ref _motion);
            TaaFinal = Hudless = Depth = Motion = null;
            HudlessPointer = DepthPointer = MotionPointer = IntPtr.Zero;
            return true;
        }
        internal void Quarantine()
        {
            if (!Quarantined.Contains(this)) Quarantined.Add(this);
        }

        private static void EnsureTexture(ref RenderTexture texture, int width, int height, GraphicsFormat format, string name)
        {
            if (texture != null && texture.IsCreated() && texture.width == width && texture.height == height && texture.graphicsFormat == format)
                return;
            Release(ref texture);
            var desc = new RenderTextureDescriptor(width, height) { graphicsFormat = format,
                depthStencilFormat = GraphicsFormat.None, depthBufferBits = 0, msaaSamples = 1,
                dimension = TextureDimension.Tex2D, volumeDepth = 1, useDynamicScale = false,
                enableRandomWrite = false, useMipMap = false, autoGenerateMips = false };
            texture = new RenderTexture(desc) { name = name, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!texture.Create()) throw new InvalidOperationException("Could not allocate " + name);
        }
        private static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
        }
    }
}
