using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    internal static class FrameGenerationGate
    {
        // expectedToken must come from the current scene-output owner, not from
        // a queued frame. This is input eligibility, never proof of GPU/Present
        // correctness or permission to release the submitted resources.
        internal static bool TryValidate(in FrameGenerationFrame frame, in SceneOutputFrame expectedToken,
            in FrameGenerationCapabilities capabilities, GraphicsDeviceType graphicsApi,
            int multiplier, out string reason)
        {
            if (!capabilities.Available || string.IsNullOrWhiteSpace(capabilities.Provider) ||
                string.IsNullOrWhiteSpace(capabilities.RuntimeVersion))
                return Reject("No frame-generation provider has initialized successfully", out reason);
            if ((graphicsApi != GraphicsDeviceType.Direct3D11 && graphicsApi != GraphicsDeviceType.Direct3D12 &&
                 graphicsApi != GraphicsDeviceType.Vulkan) || graphicsApi != capabilities.GraphicsApi ||
                !capabilities.SupportsMultiplier(multiplier))
                return Reject("The provider does not support the requested graphics API or multiplier", out reason);
            if (!frame.Token.Matches(in expectedToken) || expectedToken.RenderWidth <= 0 || expectedToken.RenderHeight <= 0 ||
                expectedToken.OutputWidth <= 0 || expectedToken.OutputHeight <= 0)
                return Reject("The frame belongs to an expired or different scene-output token", out reason);
            if (frame.ResetHistory)
                return Reject("Frame generation must warm up after a history reset", out reason);
            if (!frame.GpuInputsRetainedUntilCompletion || !frame.ProducerSynchronizationReady)
                return Reject("Frame-generation input synchronization or GPU lifetime is incomplete", out reason);
            if (!ValidateView(in frame.View, in capabilities))
                return Reject("Frame-generation color, exposure, motion, jitter or camera conventions are invalid", out reason);
            if (!BufferMatches(in frame.SceneColor, in expectedToken) ||
                !frame.SceneColor.HasExtent(expectedToken.OutputWidth, expectedToken.OutputHeight) ||
                GraphicsFormatUtility.GetColorComponentCount(frame.SceneColor.Format) < 3)
                return Reject("HUD-less scene color must be current and display-sized", out reason);
            if (!BufferMatches(in frame.Depth, in expectedToken) || !BufferMatches(in frame.Motion, in expectedToken) ||
                (GraphicsFormatUtility.GetColorComponentCount(frame.Depth.Format) < 1 &&
                 !GraphicsFormatUtility.IsDepthFormat(frame.Depth.DepthStencilFormat)) ||
                GraphicsFormatUtility.GetColorComponentCount(frame.Motion.Format) < 2)
                return Reject("Depth and motion must have current storage and the same frame token", out reason);
            bool displayInputs = frame.Depth.HasExtent(expectedToken.OutputWidth, expectedToken.OutputHeight) &&
                frame.Motion.HasExtent(expectedToken.OutputWidth, expectedToken.OutputHeight);
            bool renderInputs = frame.Depth.HasExtent(expectedToken.RenderWidth, expectedToken.RenderHeight) &&
                frame.Motion.HasExtent(expectedToken.RenderWidth, expectedToken.RenderHeight);
            if (!displayInputs && !(renderInputs && capabilities.AcceptsRenderSizedDepthAndMotion))
                return Reject("Depth/motion extents do not match a supported input resolution", out reason);
            if (!frame.Composition.DepthCoversScene || !frame.Composition.MotionCoversScene)
                return Reject("Depth and motion do not cover the composed scene", out reason);
            if (!frame.Composition.SceneIsHudless || !frame.Composition.UiIsSeparable ||
                !frame.Composition.UiUsesPremultipliedAlpha || !BufferMatches(in frame.UiColor, in expectedToken) ||
                !frame.UiColor.HasExtent(expectedToken.OutputWidth, expectedToken.OutputHeight) ||
                !GraphicsFormatUtility.HasAlphaChannel(frame.UiColor.Format) ||
                frame.UiColor.Texture == frame.SceneColor.Texture)
                return Reject("Frame generation requires a separate current display-sized premultiplied UI surface", out reason);
            if (frame.Composition.CloseVesselPresent &&
                (!capabilities.AcceptsNativeCloseVessel || !displayInputs ||
                 !frame.Composition.CloseVesselColorComplete || !frame.Composition.CloseVesselDepthComplete ||
                 !frame.Composition.CloseVesselMotionComplete || !frame.Composition.CloseVesselCoverageComplete ||
                 !BufferMatches(in frame.CloseVesselCoverage, in expectedToken) ||
                 !frame.CloseVesselCoverage.HasExtent(expectedToken.OutputWidth, expectedToken.OutputHeight)))
                return Reject("Native close-vessel color, merged depth, motion and coverage are not complete for FG", out reason);
            reason = string.Empty;
            return true;
        }

        private static bool BufferMatches(in FrameGenerationBuffer buffer, in SceneOutputFrame token) =>
            buffer.Frame.Matches(in token) && buffer.StorageIsCurrent;

        internal static bool ValidateView(in FrameGenerationView view, in FrameGenerationCapabilities capabilities)
        {
            if (view.ColorDomain < FrameGenerationColorDomain.SceneLinearHdr ||
                view.ColorDomain > FrameGenerationColorDomain.DisplaySrgb || view.ColorDomain != capabilities.ColorDomain ||
                view.MotionUnits < FrameGenerationMotionUnits.NormalizedUv ||
                view.MotionUnits > FrameGenerationMotionUnits.DisplayPixels || view.MotionUnits != capabilities.MotionUnits ||
                !Positive(view.PreExposure) || !Positive(view.Exposure) || !Positive(view.FrameTimeMilliseconds) ||
                !Positive(view.NearPlane) || !Finite(view.FarPlane) || view.FarPlane <= view.NearPlane ||
                !Positive(view.VerticalFovRadians) || view.VerticalFovRadians >= Mathf.PI ||
                !Positive(view.AspectRatio) || !Positive(view.ViewSpaceToMeters) ||
                view.MatrixConvention != FrameGenerationMatrixConvention.UnityColumnVectorGpuProjection ||
                !Finite(view.JitterRenderPixels.x) || !Finite(view.JitterRenderPixels.y) ||
                Mathf.Abs(view.MotionComponentSigns.x) != 1f || Mathf.Abs(view.MotionComponentSigns.y) != 1f)
                return false;
            if (!Invertible(view.CurrentProjection) || !Invertible(view.CurrentWorldToView) ||
                !Invertible(view.CurrentViewProjection) || !Invertible(view.PreviousViewProjection)) return false;
            var projection = view.CurrentProjection;
            float verticalScale = 1f / Mathf.Tan(view.VerticalFovRadians * .5f);
            if (!Near(projection.m00, verticalScale / view.AspectRatio) ||
                !Near(Mathf.Abs(projection.m11), verticalScale) ||
                !Near(projection.m01, 0f) || !Near(projection.m10, 0f) ||
                !Near(projection.m03, 0f) || !Near(projection.m13, 0f) ||
                !Near(projection.m20, 0f) || !Near(projection.m21, 0f) ||
                !Near(projection.m30, 0f) || !Near(projection.m31, 0f) ||
                !Near(projection.m32, -1f) || !Near(projection.m33, 0f) ||
                !Near(ProjectDepth(projection, view.NearPlane), view.ReversedDepth ? 1f : 0f) ||
                !Near(ProjectDepth(projection, view.FarPlane), view.ReversedDepth ? 0f : 1f)) return false;
            var worldToView = view.CurrentWorldToView;
            var x = new Vector3(worldToView.m00, worldToView.m01, worldToView.m02);
            var y = new Vector3(worldToView.m10, worldToView.m11, worldToView.m12);
            var z = new Vector3(worldToView.m20, worldToView.m21, worldToView.m22);
            if (!Near(worldToView.m30, 0f) || !Near(worldToView.m31, 0f) ||
                !Near(worldToView.m32, 0f) || !Near(worldToView.m33, 1f) ||
                !Near(x.sqrMagnitude, 1f) || !Near(y.sqrMagnitude, 1f) || !Near(z.sqrMagnitude, 1f) ||
                !Near(Vector3.Dot(x, y), 0f) || !Near(Vector3.Dot(y, z), 0f) ||
                !Near(Vector3.Dot(z, x), 0f)) return false;
            var composed = projection * worldToView;
            for (int i = 0; i < 16; i++)
                if (!Near(composed[i], view.CurrentViewProjection[i])) return false;
            return true;
        }

        private static float ProjectDepth(Matrix4x4 projection, float distance) =>
            (-distance * projection.m22 + projection.m23) / distance;

        private static bool Invertible(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; i++)
                if (!Finite(matrix[i])) return false;
            float determinant = matrix.determinant;
            return Finite(determinant) && Mathf.Abs(determinant) > 0f;
        }

        private static bool Near(float actual, float expected) => Finite(actual) && Finite(expected) &&
            Mathf.Abs(actual - expected) <= 0.0001f * Mathf.Max(1f, Mathf.Abs(expected));
        private static bool Positive(float value) => Finite(value) && value > 0f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Reject(string value, out string reason) { reason = value; return false; }
    }
}
