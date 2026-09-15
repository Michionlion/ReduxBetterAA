#pragma once
#include <cstdint>
#include <cstdio>
#include <stdexcept>
#include <vector>

// Synthetic validation only: a fixed, opaque, high-contrast checker HUD over a
// moving scene. This deliberately does not model arbitrary game overlays.
namespace synthetic_hud {
inline bool Contains(uint32_t x, uint32_t y, uint32_t width, uint32_t height) {
    return x >= width / 3 && x < width * 2 / 3 && y >= height / 3 && y < height / 2;
}
inline void Add(std::vector<uint32_t>& pixels, uint32_t width, uint32_t height) {
    for (uint32_t y = 0; y < height; ++y) for (uint32_t x = 0; x < width; ++x)
        if (Contains(x, y, width, height)) pixels[y * width + x] = ((x / 8 + y / 8) & 1) ? 0xffffffffu : 0xff000000u;
}
inline void Validate(const std::vector<uint32_t>& output, const std::vector<uint32_t>& finalColor,
    uint32_t width, uint32_t height) {
    uint64_t error = 0, samples = 0, exact = 0;
    for (uint32_t y = 0; y < height; ++y) for (uint32_t x = 0; x < width; ++x) {
        if (!Contains(x, y, width, height)) continue;
        const size_t index = static_cast<size_t>(y) * width + x;
        ++samples; if ((output[index] & 0xffffffu) == (finalColor[index] & 0xffffffu)) ++exact;
        for (uint32_t shift = 0; shift < 24; shift += 8) {
            const int a = (output[index] >> shift) & 255u, b = (finalColor[index] >> shift) & 255u;
            error += static_cast<uint64_t>(a > b ? a - b : b - a);
        }
    }
    const double mae = static_cast<double>(error) / (samples * 3);
    std::printf("Synthetic static HUD: exact RGB=%llu/%llu, MAE=%f/255.\n",
        static_cast<unsigned long long>(exact), static_cast<unsigned long long>(samples), mae);
    if (!samples || mae > 1 || exact * 100 < samples * 99)
        throw std::runtime_error("HUD-less compatibility input did not preserve the synthetic static high-contrast HUD");
}
}
