using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    internal enum FrameGenerationColorDomain { Unknown, SceneLinearHdr, DisplayLinear, DisplaySrgb }
    internal enum FrameGenerationMotionUnits { Unknown, NormalizedUv, RenderPixels, DisplayPixels }

    // A frame reference does not own the underlying GPU allocation. The future
    // presentation provider must retain it until its final consumer has finished.
    internal readonly struct FrameGenerationBuffer
    {
        internal readonly RenderTexture Texture;
        internal readonly SceneOutputFrame Frame;
        internal readonly int Width, Height;
        internal readonly ulong InstanceId;
        internal readonly GraphicsFormat Format, DepthStencilFormat;

        internal FrameGenerationBuffer(RenderTexture texture, in SceneOutputFrame frame)
        {
            Texture = texture;
            Frame = frame;
            Width = texture == null ? 0 : texture.width;
            Height = texture == null ? 0 : texture.height;
            InstanceId = texture == null ? 0UL : EntityId.ToULong(texture.GetEntityId());
            Format = texture == null ? GraphicsFormat.None : texture.graphicsFormat;
            DepthStencilFormat = texture == null ? GraphicsFormat.None : texture.descriptor.depthStencilFormat;
        }

        internal bool StorageIsCurrent => Texture != null && Texture.IsCreated() &&
            EntityId.ToULong(Texture.GetEntityId()) == InstanceId && Texture.width == Width && Texture.height == Height &&
            Texture.graphicsFormat == Format && Texture.descriptor.depthStencilFormat == DepthStencilFormat &&
            Texture.dimension == TextureDimension.Tex2D &&
            Texture.volumeDepth == 1 && Texture.antiAliasing == 1 && !Texture.useDynamicScale;

        internal bool HasExtent(int width, int height) => Width == width && Height == height;
    }

    // Each field has a defined domain; vectors are not assumed to cover geometry
    // merely because their texture dimensions match the output color.
    internal readonly struct FrameGenerationView
    {
        internal readonly FrameGenerationColorDomain ColorDomain;
        internal readonly FrameGenerationMotionUnits MotionUnits;
        internal readonly Vector2 JitterRenderPixels, MotionComponentSigns;
        internal readonly float PreExposure, Exposure, FrameTimeMilliseconds, NearPlane, FarPlane;
        internal readonly bool ReversedDepth;
        internal readonly Matrix4x4 CurrentViewProjection, PreviousViewProjection;

        internal FrameGenerationView(FrameGenerationColorDomain colorDomain,
            FrameGenerationMotionUnits motionUnits, Vector2 jitterRenderPixels,
            Vector2 motionComponentSigns, float preExposure, float exposure,
            float frameTimeMilliseconds, float nearPlane, float farPlane, bool reversedDepth,
            Matrix4x4 currentViewProjection, Matrix4x4 previousViewProjection)
        {
            ColorDomain = colorDomain; MotionUnits = motionUnits;
            JitterRenderPixels = jitterRenderPixels; MotionComponentSigns = motionComponentSigns;
            PreExposure = preExposure; Exposure = exposure; FrameTimeMilliseconds = frameTimeMilliseconds;
            NearPlane = nearPlane; FarPlane = farPlane; ReversedDepth = reversedDepth;
            CurrentViewProjection = currentViewProjection; PreviousViewProjection = previousViewProjection;
        }
    }

    internal readonly struct FrameGenerationComposition
    {
        internal readonly bool SceneIsHudless, UiIsSeparable, UiUsesPremultipliedAlpha;
        internal readonly bool DepthCoversScene, MotionCoversScene;
        internal readonly bool CloseVesselPresent, CloseVesselColorComplete;
        // Complete close depth must be merged into the scene depth convention and
        // projection, not a second camera's raw device depth copied over it.
        internal readonly bool CloseVesselDepthComplete, CloseVesselMotionComplete, CloseVesselCoverageComplete;

        internal FrameGenerationComposition(bool sceneIsHudless, bool uiIsSeparable,
            bool uiUsesPremultipliedAlpha, bool depthCoversScene, bool motionCoversScene,
            bool closeVesselPresent = false, bool closeVesselColorComplete = false,
            bool closeVesselDepthComplete = false, bool closeVesselMotionComplete = false,
            bool closeVesselCoverageComplete = false)
        {
            SceneIsHudless = sceneIsHudless; UiIsSeparable = uiIsSeparable;
            UiUsesPremultipliedAlpha = uiUsesPremultipliedAlpha;
            DepthCoversScene = depthCoversScene; MotionCoversScene = motionCoversScene;
            CloseVesselPresent = closeVesselPresent; CloseVesselColorComplete = closeVesselColorComplete;
            CloseVesselDepthComplete = closeVesselDepthComplete; CloseVesselMotionComplete = closeVesselMotionComplete;
            CloseVesselCoverageComplete = closeVesselCoverageComplete;
        }
    }

    // A completed REAL frame, after reconstruction and any native close-vessel
    // composition, before UI. UI is a separate display-sized RGBA surface.
    // Availability, native handles, synchronization and Present remain outside
    // this managed contract; no current adapter supplies a dispatchable FG frame.
    internal readonly struct FrameGenerationFrame
    {
        internal readonly SceneOutputFrame Token;
        internal readonly FrameGenerationBuffer SceneColor, Depth, Motion, UiColor, CloseVesselCoverage;
        internal readonly FrameGenerationView View;
        internal readonly FrameGenerationComposition Composition;
        // Synchronization means queue/fence dependencies are established; it does
        // not require waiting for the GPU on the main thread.
        internal readonly bool ResetHistory, GpuInputsRetainedUntilCompletion, ProducerSynchronizationReady;

        internal FrameGenerationFrame(in SceneOutputFrame token, in FrameGenerationBuffer sceneColor,
            in FrameGenerationBuffer depth, in FrameGenerationBuffer motion, in FrameGenerationBuffer uiColor,
            in FrameGenerationBuffer closeVesselCoverage, in FrameGenerationView view,
            in FrameGenerationComposition composition, bool resetHistory,
            bool gpuInputsRetainedUntilCompletion, bool producerSynchronizationReady)
        {
            Token = token; SceneColor = sceneColor; Depth = depth; Motion = motion; UiColor = uiColor;
            CloseVesselCoverage = closeVesselCoverage; View = view; Composition = composition;
            ResetHistory = resetHistory; GpuInputsRetainedUntilCompletion = gpuInputsRetainedUntilCompletion;
            ProducerSynchronizationReady = producerSynchronizationReady;
        }
    }

    // Populated only after an actual provider initializes and queries its SDK.
    // The all-zero/default value is intentionally unavailable. A native Present
    // smoke test or SR support must not be promoted into these capabilities.
    internal readonly struct FrameGenerationCapabilities
    {
        internal readonly string Provider, RuntimeVersion;
        internal readonly bool Available, AcceptsRenderSizedDepthAndMotion, AcceptsNativeCloseVessel;
        internal readonly GraphicsDeviceType GraphicsApi;
        internal readonly FrameGenerationColorDomain ColorDomain;
        internal readonly FrameGenerationMotionUnits MotionUnits;
        // Bit N means multiplier Nx; do not infer intermediate multipliers from
        // a maximum, because a provider may support only a subset.
        internal readonly uint SupportedMultiplierMask;

        internal FrameGenerationCapabilities(string provider, string runtimeVersion, bool available,
            GraphicsDeviceType graphicsApi, FrameGenerationColorDomain colorDomain,
            FrameGenerationMotionUnits motionUnits, uint supportedMultiplierMask,
            bool acceptsRenderSizedDepthAndMotion, bool acceptsNativeCloseVessel)
        {
            Provider = provider; RuntimeVersion = runtimeVersion; Available = available;
            GraphicsApi = graphicsApi; ColorDomain = colorDomain; MotionUnits = motionUnits;
            SupportedMultiplierMask = supportedMultiplierMask;
            AcceptsRenderSizedDepthAndMotion = acceptsRenderSizedDepthAndMotion;
            AcceptsNativeCloseVessel = acceptsNativeCloseVessel;
        }

        internal bool SupportsMultiplier(int multiplier) => multiplier >= 2 && multiplier < 32 &&
            (SupportedMultiplierMask & (1u << multiplier)) != 0;
    }
}
