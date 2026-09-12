using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    internal static class TemporalTextures
    {
        public static bool Matches(Texture texture, int width, int height)
        {
            if (texture == null || texture.width != width || texture.height != height ||
                texture.dimension != TextureDimension.Tex2D)
                return false;
            var target = texture as RenderTexture;
            return target == null || (target.IsCreated() && target.volumeDepth == 1);
        }

        public static bool IsCreated(RenderTexture texture) => texture != null && texture.IsCreated();

        public static RenderTextureDescriptor PersistentColorDescriptor(RenderTextureDescriptor source)
        {
            source.depthBufferBits = 0;
            source.msaaSamples = 1;
            source.bindMS = false;
            source.enableRandomWrite = false;
            source.useMipMap = false;
            source.autoGenerateMips = false;
            source.useDynamicScale = false;
            source.memoryless = RenderTextureMemoryless.None;
            return source;
        }

        public static RenderTextureDescriptor VendorOutputDescriptor(RenderTextureDescriptor source)
        {
            source = PersistentColorDescriptor(source);
            source.graphicsFormat = GraphicsFormatUtility.GetLinearFormat(source.graphicsFormat);
            // Both Unity vendor plugins require a persistent linear UAV output.
            source.enableRandomWrite = true;
            return source;
        }

        public static void Release(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
            }
            texture = null;
        }

        public static bool IsHdr(RenderTextureFormat format) =>
            format == RenderTextureFormat.ARGBHalf || format == RenderTextureFormat.ARGBFloat ||
            format == RenderTextureFormat.RGB111110Float || format == RenderTextureFormat.DefaultHDR;

        public static int EstimateColorBytes(RenderTextureFormat format)
        {
            switch (format)
            {
                case RenderTextureFormat.ARGBFloat:
                    return 16;
                case RenderTextureFormat.ARGBHalf:
                case RenderTextureFormat.RGFloat:
                    return 8;
                default:
                    return 4;
            }
        }
    }
}
