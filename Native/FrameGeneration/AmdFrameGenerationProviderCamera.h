#pragma once
#include "AmdFrameGenerationProvider.h"
#include <array>
#include <cmath>
#include <algorithm>
#include <limits>

// Matching provider-independent camera convention; no Streamline SDK dependency.
namespace rba_amd_provider {
using Matrix = std::array<double, 16>;
inline bool MatrixRead(const float* input, Matrix& out) {
    for (size_t i = 0; i < out.size(); ++i) { if (!std::isfinite(input[i])) return false; out[i] = input[i]; }
    return true;
}
inline Matrix Multiply(const Matrix& a, const Matrix& b) {
    Matrix result{};
    for (size_t r = 0; r < 4; ++r) for (size_t c = 0; c < 4; ++c)
        for (size_t k = 0; k < 4; ++k) result[r * 4 + c] += a[r * 4 + k] * b[k * 4 + c];
    return result;
}
inline bool Invert(const Matrix& input, Matrix& result) {
    double a[4][8]{};
    for (size_t r = 0; r < 4; ++r) { for (size_t c = 0; c < 4; ++c) a[r][c] = input[r * 4 + c]; a[r][r + 4] = 1; }
    for (size_t c = 0; c < 4; ++c) {
        size_t pivot = c;
        for (size_t r = c + 1; r < 4; ++r) if (std::abs(a[r][c]) > std::abs(a[pivot][c])) pivot = r;
        if (std::abs(a[pivot][c]) < 1e-12) return false;
        for (size_t k = 0; k < 8; ++k) std::swap(a[c][k], a[pivot][k]);
        const double divisor = a[c][c]; for (double& value : a[c]) value /= divisor;
        for (size_t r = 0; r < 4; ++r) if (r != c) { const double factor = a[r][c]; for (size_t k = 0; k < 8; ++k) a[r][k] -= factor * a[c][k]; }
    }
    for (size_t r = 0; r < 4; ++r) for (size_t c = 0; c < 4; ++c) { result[r * 4 + c] = a[r][c + 4]; if (!std::isfinite(result[r * 4 + c])) return false; }
    return true;
}
inline bool Near(double a, double b, double tolerance = 0.002) { return std::abs(a - b) <= tolerance * std::max(1.0, std::max(std::abs(a), std::abs(b))); }
inline bool CameraValid(const RbaAmdProviderCamera& camera) {
    const float scalars[] = {camera.jitterRenderPixelsX, camera.jitterRenderPixelsY, camera.nearPlane, camera.farPlane,
        camera.verticalFovRadians, camera.aspectRatio, camera.viewSpaceToMeters, camera.frameTimeMilliseconds, camera.motionScaleX, camera.motionScaleY};
    for (float value : scalars) if (!std::isfinite(value)) return false;
    if (camera.nearPlane <= 0 || camera.farPlane <= camera.nearPlane || camera.verticalFovRadians <= 0 || camera.verticalFovRadians >= 3.14159265f ||
        camera.aspectRatio <= 0 || camera.viewSpaceToMeters <= 0 || camera.motionScaleX == 0 || camera.motionScaleY == 0 ||
        camera.reversedDepth > 1 || camera.resetHistory > 1 || camera.frameTimeMilliseconds < 0 || (!camera.resetHistory && camera.frameTimeMilliseconds == 0)) return false;
    Matrix p{}, v{}, vp{}, previous{}, inverse{}, inverseV{};
    if (!MatrixRead(camera.projection, p) || !MatrixRead(camera.worldToView, v) || !MatrixRead(camera.viewProjection, vp) ||
        !MatrixRead(camera.previousViewProjection, previous) || !Invert(p, inverse) || !Invert(v, inverseV) || !Invert(vp, inverse) || !Invert(previous, inverse)) return false;
    // Standard Unity perspective shape, preserving off-center XY and Y flip.
    for (size_t index : {size_t(1), size_t(3), size_t(4), size_t(7), size_t(8), size_t(9), size_t(12), size_t(13), size_t(15)})
        if (!Near(p[index], 0, 1e-5)) return false;
    if (!Near(p[14], -1, 1e-5) || p[0] <= 0 || std::abs(p[5]) < 1e-8 || !Near(std::abs(p[5]), 1.0 / std::tan(camera.verticalFovRadians * 0.5)) ||
        !Near(std::abs(p[5] / p[0]), camera.aspectRatio)) return false;
    const auto depth = [&](double z) { return (p[10] * z + p[11]) / (-z); };
    if (!Near(depth(-camera.nearPlane), camera.reversedDepth ? 1 : 0) || !Near(depth(-camera.farPlane), camera.reversedDepth ? 0 : 1)) return false;
    for (size_t c = 0; c < 4; ++c) if (!Near(v[12 + c], c == 3 ? 1 : 0, 1e-5)) return false;
    for (size_t r = 0; r < 3; ++r) for (size_t s = 0; s < 3; ++s) {
        double dot = 0; for (size_t c = 0; c < 3; ++c) dot += v[r * 4 + c] * v[s * 4 + c];
        if (!Near(dot, r == s ? 1 : 0)) return false;
    }
    const Matrix product = Multiply(p, v);
    for (size_t i = 0; i < 16; ++i) if (!Near(product[i], vp[i])) return false;
    const double maximum = std::numeric_limits<float>::max();
    for (size_t i : {size_t(3), size_t(7), size_t(11)}) if (std::abs(inverseV[i] * camera.viewSpaceToMeters) > maximum) return false;
    if (std::abs(p[11] * camera.viewSpaceToMeters) > maximum || camera.nearPlane * camera.viewSpaceToMeters <= 0) return false;
    return std::isfinite(camera.farPlane * camera.viewSpaceToMeters);
}
}
