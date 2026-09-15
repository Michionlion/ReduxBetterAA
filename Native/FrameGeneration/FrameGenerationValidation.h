#pragma once
#include "AmdFrameGeneration.h"
#include <cmath>
#include <cstddef>

// Shared pre-submission validation: reject metadata before either graphics API
// can start reading caller-owned input textures.
namespace rba_amd_fg {
inline const char* ValidateMetadata(const RbaAmdFgFrame& f, uint32_t maxWidth, uint32_t maxHeight, uint64_t lastFrame) {
    if (f.structSize != sizeof(f) || f.abiVersion != 1) return "Invalid AMD FG frame ABI";
    if (!f.frameId || (lastFrame && f.frameId <= lastFrame)) return "Stale or duplicate AMD FG real-frame ID";
    if (!f.renderWidth || !f.renderHeight || f.renderWidth > maxWidth || f.renderHeight > maxHeight || f.reset > 1 ||
        !std::isfinite(f.frameTimeMilliseconds) || f.frameTimeMilliseconds <= 0 ||
        !std::isfinite(f.cameraNear) || f.cameraNear <= 0 || !std::isfinite(f.cameraFar) || f.cameraFar <= f.cameraNear ||
        !std::isfinite(f.cameraFovRadians) || f.cameraFovRadians <= 0 || f.cameraFovRadians >= 3.141593f ||
        !std::isfinite(f.viewSpaceToMeters) || f.viewSpaceToMeters <= 0 ||
        !std::isfinite(f.jitterX) || !std::isfinite(f.jitterY) || !std::isfinite(f.motionScaleX) || !std::isfinite(f.motionScaleY))
        return "Invalid AMD FG frame metadata";
    for (size_t i = 0; i < 3; ++i) if (!std::isfinite(f.cameraPosition[i]) || !std::isfinite(f.cameraUp[i]) ||
        !std::isfinite(f.cameraRight[i]) || !std::isfinite(f.cameraForward[i])) return "Non-finite AMD FG camera transform";
    const auto dot = [](const float* a, const float* b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; };
    if (std::fabs(dot(f.cameraUp, f.cameraUp) - 1) >= 0.01f || std::fabs(dot(f.cameraRight, f.cameraRight) - 1) >= 0.01f ||
        std::fabs(dot(f.cameraForward, f.cameraForward) - 1) >= 0.01f || std::fabs(dot(f.cameraUp, f.cameraRight)) >= 0.01f ||
        std::fabs(dot(f.cameraUp, f.cameraForward)) >= 0.01f || std::fabs(dot(f.cameraRight, f.cameraForward)) >= 0.01f)
        return "AMD FG camera axes must be normalized and orthogonal";
    return nullptr;
}
}
