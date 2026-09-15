#pragma once
#include "StreamlineProviderState.h"
#include <sl.h>

namespace rba_sl {
inline sl::float4x4 ToStreamline(const Matrix& columnVectorMatrix) {
    sl::float4x4 result{};
    // SL uses row vectors. Transpose Unity's column-vector transform, without
    // confusing that mathematical conversion with the arrays' storage order.
    for (uint32_t r = 0; r < 4; ++r) for (uint32_t c = 0; c < 4; ++c)
        (&result[r].x)[c] = static_cast<float>(columnVectorMatrix[c * 4 + r]);
    return result;
}
inline sl::Constants MakeConstants(const RbaSlFgCamera& camera, bool forceReset) {
    Matrix p{}, v{}, vp{}, previous{}, inverseV{}, inverseVp{}, inverseP{}, previousToCurrent{};
    MatrixRead(camera.projection, p); MatrixRead(camera.worldToView, v);
    MatrixRead(camera.viewProjection, vp); MatrixRead(camera.previousViewProjection, previous);
    Invert(v, inverseV); Invert(vp, inverseVp);
    const Matrix currentToPrevious = Multiply(previous, inverseVp);
    Invert(currentToPrevious, previousToCurrent);
    // Scale view-space distances together: P's z translation, position and
    // near/far. The clip-to-clip transform remains independent of world units.
    p[11] *= camera.viewSpaceToMeters; Invert(p, inverseP);
    sl::Constants result{};
    result.cameraViewToClip = ToStreamline(p); result.clipToCameraView = ToStreamline(inverseP);
    result.clipToPrevClip = ToStreamline(currentToPrevious); result.prevClipToClip = ToStreamline(previousToCurrent);
    result.cameraPos = {static_cast<float>(inverseV[3] * camera.viewSpaceToMeters),
        static_cast<float>(inverseV[7] * camera.viewSpaceToMeters), static_cast<float>(inverseV[11] * camera.viewSpaceToMeters)};
    result.cameraRight = {static_cast<float>(inverseV[0]), static_cast<float>(inverseV[4]), static_cast<float>(inverseV[8])};
    result.cameraUp = {static_cast<float>(inverseV[1]), static_cast<float>(inverseV[5]), static_cast<float>(inverseV[9])};
    result.cameraFwd = {-static_cast<float>(inverseV[2]), -static_cast<float>(inverseV[6]), -static_cast<float>(inverseV[10])};
    result.cameraNear = camera.nearPlane * camera.viewSpaceToMeters; result.cameraFar = camera.farPlane * camera.viewSpaceToMeters;
    result.cameraFOV = camera.verticalFovRadians; result.cameraAspectRatio = camera.aspectRatio;
    result.jitterOffset = {camera.jitterRenderPixelsX, camera.jitterRenderPixelsY};
    result.mvecScale = {camera.motionScaleX, camera.motionScaleY}; result.cameraPinholeOffset = {0, 0};
    result.depthInverted = camera.reversedDepth ? sl::eTrue : sl::eFalse;
    result.cameraMotionIncluded = sl::eTrue; result.motionVectors3D = sl::eFalse;
    result.reset = forceReset || camera.resetHistory ? sl::eTrue : sl::eFalse;
    result.orthographicProjection = sl::eFalse; result.motionVectorsDilated = sl::eFalse; result.motionVectorsJittered = sl::eFalse;
    return result;
}
}
