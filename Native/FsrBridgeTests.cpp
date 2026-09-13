#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "FsrBridge.h"
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
void Expect(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
void Check(HRESULT code, const char* message) {
    if (FAILED(code)) { char detail[256]; std::snprintf(detail, sizeof(detail), "%s (0x%08lx)", message, static_cast<unsigned long>(code)); throw std::runtime_error(detail); }
}
using RenderEvent = void(__stdcall*)(int, void*);
struct NativeContext {
    void* handle = nullptr;
    RenderEvent render = reinterpret_cast<RenderEvent>(RbaFsrGetRenderEventFunc());
    ~NativeContext() { if (handle) { render(RbaFsrDestroy, handle); RbaFsrRelease(handle); } }
};
ComPtr<ID3D11Texture2D> Texture(ID3D11Device* device, uint32_t width, uint32_t height, DXGI_FORMAT format, const void* data, uint32_t pitch) {
    D3D11_TEXTURE2D_DESC description{};
    description.Width = width; description.Height = height; description.MipLevels = 1; description.ArraySize = 1;
    description.Format = format; description.SampleDesc.Count = 1; description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    D3D11_SUBRESOURCE_DATA initial{}; initial.pSysMem = data; initial.SysMemPitch = pitch;
    ComPtr<ID3D11Texture2D> texture;
    Check(device->CreateTexture2D(&description, data ? &initial : nullptr, &texture), "Create test texture");
    return texture;
}
float HalfToFloat(uint16_t h) {
    const int exponent = (h >> 10) & 31;
    const int mantissa = h & 1023;
    if (exponent == 31) return mantissa ? NAN : ((h & 0x8000) ? -INFINITY : INFINITY);
    const float value = exponent ? std::ldexp(1.0f + mantissa / 1024.0f, exponent - 15) : std::ldexp(mantissa / 1024.0f, -14);
    return (h & 0x8000) ? -value : value;
}
uint16_t FloatToHalf(float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    const uint16_t sign = static_cast<uint16_t>((bits >> 16) & 0x8000);
    const int exponent = static_cast<int>((bits >> 23) & 255) - 127 + 15;
    const uint32_t mantissa = bits & 0x7fffff;
    if (exponent >= 31) return static_cast<uint16_t>(sign | 0x7c00);
    if (exponent <= 0) return exponent < -10 ? sign :
        static_cast<uint16_t>(sign | ((mantissa | 0x800000) >> (14 - exponent)));
    return static_cast<uint16_t>(sign | ((exponent << 10) + ((mantissa + 0x1000) >> 13)));
}

struct GradientCase {
    const char* name;
    uint32_t width, height, outputWidth, outputHeight;
    bool masks;
};

// Objective transport checks: every output pixel is written and finite, both
// gradient axes keep their orientation, and the unchanged blue channel survives.
// This is not an image-quality score or a substitute for raster/camera tests.
void CheckGradient(ID3D11Device* device, ID3D11DeviceContext* immediate, ID3D11Texture2D* output) {
    D3D11_TEXTURE2D_DESC description{}; output->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> readback;
    Check(device->CreateTexture2D(&description, nullptr, &readback), "Create gradient readback");
    immediate->CopyResource(readback.Get(), output);
    D3D11_MAPPED_SUBRESOURCE mapping{};
    Check(immediate->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapping), "Read gradient output");
    std::array<double, 4> bandTotals{};
    std::array<uint64_t, 4> bandCounts{};
    uint64_t invalid = 0;
    for (uint32_t y = 0; y < description.Height; ++y) {
        const auto* row = reinterpret_cast<const uint16_t*>(static_cast<const unsigned char*>(mapping.pData) + y * mapping.RowPitch);
        for (uint32_t x = 0; x < description.Width; ++x) {
            const float red = HalfToFloat(row[x * 4]), green = HalfToFloat(row[x * 4 + 1]), blue = HalfToFloat(row[x * 4 + 2]);
            if (!std::isfinite(red) || !std::isfinite(green) || !std::isfinite(blue) ||
                red < -.05f || red > 1.05f || green < -.05f || green > 1.05f || std::abs(blue - .75f) > .07f) ++invalid;
            if (x >= description.Width / 8 && x < description.Width * 3 / 8) { bandTotals[0] += red; ++bandCounts[0]; }
            if (x >= description.Width * 5 / 8 && x < description.Width * 7 / 8) { bandTotals[1] += red; ++bandCounts[1]; }
            if (y >= description.Height / 8 && y < description.Height * 3 / 8) { bandTotals[2] += green; ++bandCounts[2]; }
            if (y >= description.Height * 5 / 8 && y < description.Height * 7 / 8) { bandTotals[3] += green; ++bandCounts[3]; }
        }
    }
    immediate->Unmap(readback.Get(), 0);
    Expect(invalid == 0, "Gradient output has unwritten, non-finite, or corrupted RGB pixels");
    for (size_t i = 0; i < bandTotals.size(); ++i) { Expect(bandCounts[i] != 0, "Gradient band is empty"); bandTotals[i] /= bandCounts[i]; }
    Expect(bandTotals[1] - bandTotals[0] > .2 && bandTotals[3] - bandTotals[2] > .15,
        "Gradient axes were flipped, lost or transposed");
    std::printf("Gradient bands: red left/right %.5f/%.5f, green top/bottom %.5f/%.5f\n",
        bandTotals[0], bandTotals[1], bandTotals[2], bandTotals[3]);
}

void RunGradient(ID3D11Device* device, ID3D11DeviceContext* immediate, const wchar_t* runtime, const GradientCase& test) {
    std::printf("Gradient case %s: %ux%u -> %ux%u, manual LDR, moving/jittered, masks=%u\n",
        test.name, test.width, test.height, test.outputWidth, test.outputHeight, test.masks ? 1u : 0u);
    const size_t pixels = static_cast<size_t>(test.width) * test.height;
    std::vector<uint16_t> colors(pixels * 4), motions(pixels * 2);
    std::vector<float> depths(pixels);
    std::vector<uint8_t> reactive(pixels), composition(pixels);
    for (uint32_t y = 0; y < test.height; ++y) for (uint32_t x = 0; x < test.width; ++x) {
        const size_t i = static_cast<size_t>(y) * test.width + x;
        depths[i] = .25f + .5f * (y + .5f) / test.height;
        motions[i * 2] = FloatToHalf(.25f / test.width);
        motions[i * 2 + 1] = FloatToHalf(-.125f / test.height);
        reactive[i] = x > test.width / 2 ? 128 : 0;
        composition[i] = y > test.height / 2 ? 192 : 0;
    }
    auto color = Texture(device, test.width, test.height, DXGI_FORMAT_R16G16B16A16_FLOAT, nullptr, 0);
    auto depth = Texture(device, test.width, test.height, DXGI_FORMAT_R32_FLOAT, depths.data(), test.width * 4);
    auto motion = Texture(device, test.width, test.height, DXGI_FORMAT_R16G16_FLOAT, motions.data(), test.width * 4);
    auto reactiveTexture = Texture(device, test.width, test.height, DXGI_FORMAT_R8_UNORM, reactive.data(), test.width);
    auto compositionTexture = Texture(device, test.width, test.height, DXGI_FORMAT_R8_UNORM, composition.data(), test.width);
    const float exposureValue = 1;
    auto exposure = Texture(device, 1, 1, DXGI_FORMAT_R32_FLOAT, &exposureValue, sizeof(float));
    std::vector<uint16_t> unwritten(static_cast<size_t>(test.outputWidth) * test.outputHeight * 4, 0x7e00);
    auto output = Texture(device, test.outputWidth, test.outputHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, unwritten.data(), test.outputWidth * 8);
    RbaFsrCreateDesc create{ sizeof(create), 1, test.width, test.height, test.outputWidth, test.outputHeight, RbaFsrInvertedDepth, 11, runtime };
    NativeContext context;
    std::array<char, 512> error{};
    const uint32_t result = RbaFsrCreate(&create, &context.handle, error.data(), static_cast<uint32_t>(error.size()));
    if (result) throw std::runtime_error(error.data());
    RbaFsrDispatchDesc frame{};
    frame.structSize = sizeof(frame); frame.abiVersion = 1; frame.context = context.handle;
    frame.color = color.Get(); frame.depth = depth.Get(); frame.motionVectors = motion.Get(); frame.output = output.Get();
    frame.exposure = exposure.Get(); frame.reactive = test.masks ? reactiveTexture.Get() : nullptr;
    frame.transparencyAndComposition = test.masks ? compositionTexture.Get() : nullptr;
    frame.motionScaleX = static_cast<float>(test.width); frame.motionScaleY = static_cast<float>(test.height);
    frame.frameTimeMilliseconds = 16.66667f; frame.preExposure = 1;
    frame.cameraNear = 1000; frame.cameraFar = .1f; frame.verticalFovRadians = 1; frame.viewSpaceToMeters = 1;
    const std::array<std::array<float, 2>, 4> jitters = { std::array<float, 2>{0, -1.f / 6}, {-.25f, 1.f / 6}, {.25f, -7.f / 18}, {-.375f, -1.f / 18} };
    for (uint32_t tick = 0; tick < 12; ++tick) {
        frame.frameId = tick == 6 ? 1 : tick + 1; // Explicit reset permits a new frame-ID epoch.
        frame.reset = tick == 0 || tick == 6 ? 1 : 0;
        frame.jitterX = jitters[tick % jitters.size()][0]; frame.jitterY = jitters[tick % jitters.size()][1];
        for (uint32_t y = 0; y < test.height; ++y) for (uint32_t x = 0; x < test.width; ++x) {
            const size_t i = (static_cast<size_t>(y) * test.width + x) * 4;
            colors[i] = FloatToHalf(.1f + .6f * (x + .5f + tick * .25f - frame.jitterX) / test.width);
            colors[i + 1] = FloatToHalf(.15f + .5f * (y + .5f - tick * .125f - frame.jitterY) / test.height);
            colors[i + 2] = FloatToHalf(.75f); colors[i + 3] = FloatToHalf(1);
        }
        immediate->UpdateSubresource(color.Get(), 0, nullptr, colors.data(), test.width * 8, 0);
        void* packet = nullptr;
        Expect(RbaFsrPrepareDispatch(&frame, &packet) == RbaFsrOk && packet, "Could not prepare gradient frame");
        context.render(RbaFsrDispatch, packet);
        RbaFsrStatus status{}; status.structSize = sizeof(status);
        Expect(RbaFsrGetStatus(context.handle, &status) == RbaFsrOk, "Could not read gradient frame status");
        if (status.state != RbaFsrReady) throw std::runtime_error(status.error);
        Expect(status.lastSubmittedFrameId == frame.frameId, "Gradient frame ID was not submitted");
    }
    // A prepared packet that was never submitted must also retire on teardown.
    // Exercise this with GPU work outstanding and caller inputs already released.
    frame.frameId = 13;
    void* unusedPacket = nullptr;
    Expect(RbaFsrPrepareDispatch(&frame, &unusedPacket) == RbaFsrOk, "Could not prepare final queued packet");
    color.Reset(); depth.Reset(); motion.Reset(); exposure.Reset(); reactiveTexture.Reset(); compositionTexture.Reset();
    context.render(RbaFsrDestroy, context.handle);
    context.render(RbaFsrDestroy, context.handle); // Teardown must be idempotent before CPU release.
    RbaFsrStatus status{}; status.structSize = sizeof(status);
    Expect(RbaFsrGetStatus(context.handle, &status) == RbaFsrOk && status.state == RbaFsrDestroyed,
        "Gradient context did not retire during lifecycle switch");
    Expect(RbaFsrPrepareDispatch(&frame, &unusedPacket) == RbaFsrBusy && !unusedPacket,
        "Destroyed context accepted another packet");
    Expect(RbaFsrRelease(context.handle) == RbaFsrOk, "Gradient context CPU release failed");
    context.handle = nullptr;
    CheckGradient(device, immediate, output.Get());
}
void CheckOutput(ID3D11Device* device, ID3D11DeviceContext* immediate, ID3D11Texture2D* output, uint32_t scale) {
    D3D11_TEXTURE2D_DESC description{}; output->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> readback;
    Check(device->CreateTexture2D(&description, nullptr, &readback), "Create readback texture");
    immediate->CopyResource(readback.Get(), output);
    D3D11_MAPPED_SUBRESOURCE mapping{};
    Check(immediate->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapping), "Read reconstructed output");
    std::array<double, 3> total{};
    const std::array<double, 3> expected = { .25, .5, .75 };
    uint64_t pixels = 0, invalid = 0;
    for (uint32_t y = 8; y + 8 < description.Height; ++y) {
        const auto* row = reinterpret_cast<const uint16_t*>(static_cast<const unsigned char*>(mapping.pData) + y * mapping.RowPitch);
        for (uint32_t x = 8; x + 8 < description.Width; ++x) {
            for (size_t channel = 0; channel < total.size(); ++channel) {
                const float value = HalfToFloat(row[x * 4 + channel]);
                if (!std::isfinite(value) || std::abs(value - expected[channel]) >= .035) ++invalid;
                else total[channel] += value;
            }
            ++pixels;
        }
    }
    immediate->Unmap(readback.Get(), 0);
    Expect(invalid == 0, "Reconstructed output was not written or did not preserve each uniform-color pixel");
    std::printf("%ux output mean RGB %.6f %.6f %.6f (%llu pixels)\n", scale,
        total[0] / pixels, total[1] / pixels, total[2] / pixels, static_cast<unsigned long long>(pixels));
    for (size_t channel = 0; channel < total.size(); ++channel)
        Expect(std::abs(total[channel] / pixels - expected[channel]) < .035, "Reconstruction changed uniform linear color unexpectedly");
}
void Run(ID3D11Device* device, ID3D11DeviceContext* immediate, const wchar_t* runtime, uint32_t scale, bool autoExposure) {
    constexpr uint32_t width = 128, height = 128;
    std::vector<uint16_t> colors(width * height * 4);
    for (size_t i = 0; i < colors.size(); i += 4) { colors[i] = 0x3400; colors[i+1] = 0x3800; colors[i+2] = 0x3a00; colors[i+3] = 0x3c00; }
    std::vector<float> depths(width * height, .5f);
    std::vector<uint16_t> motions(width * height * 2, 0);
    std::vector<uint16_t> unwritten(width * scale * height * scale * 4, 0x7e00);
    auto color = Texture(device, width, height, DXGI_FORMAT_R16G16B16A16_FLOAT, colors.data(), width * 8);
    auto depth = Texture(device, width, height, DXGI_FORMAT_R32_FLOAT, depths.data(), width * 4);
    auto motion = Texture(device, width, height, DXGI_FORMAT_R16G16_FLOAT, motions.data(), width * 4);
    auto output = Texture(device, width * scale, height * scale, DXGI_FORMAT_R16G16B16A16_FLOAT, unwritten.data(), width * scale * 8);
    const float exposureValue = 1.0f;
    auto exposure = Texture(device, 1, 1, DXGI_FORMAT_R32_FLOAT, &exposureValue, sizeof(float));
    RbaFsrCreateDesc create{ sizeof(create), 1, width, height, width * scale, height * scale,
        RbaFsrInvertedDepth | (autoExposure ? RbaFsrHdr | RbaFsrAutoExposure : 0), 11, runtime };
    RbaFsrStatus probe{}; probe.structSize = sizeof(probe);
    const uint32_t probeResult = RbaFsrProbe(&create, color.Get(), &probe);
    if (probeResult) throw std::runtime_error(std::string("Provider probe: ") + probe.error);
    std::printf("Probe selected provider: %s (opaque id %llu, adapter %04x:%04x)\n", probe.providerName,
        static_cast<unsigned long long>(probe.providerVersionId), probe.adapterVendorId, probe.adapterDeviceId);
    Expect(probe.state == RbaFsrReady && probe.providerName[0], "Probe did not identify an actual created provider");
    NativeContext context;
    std::array<char, 512> error{};
    const uint32_t created = RbaFsrCreate(&create, &context.handle, error.data(), static_cast<uint32_t>(error.size()));
    if (created) throw std::runtime_error(error.data());
    Expect(RbaFsrRelease(context.handle) == RbaFsrBusy, "Release accepted a live context");
    RbaFsrDispatchDesc frame{};
    frame.structSize = sizeof(frame); frame.abiVersion = 1; frame.context = context.handle;
    frame.color = color.Get(); frame.depth = depth.Get(); frame.motionVectors = motion.Get(); frame.output = output.Get();
    frame.exposure = autoExposure ? nullptr : exposure.Get();
    frame.motionScaleX = width; frame.motionScaleY = height; frame.frameTimeMilliseconds = 16.66667f;
    frame.preExposure = 1; frame.cameraNear = .1f; frame.cameraFar = 1000;
    frame.verticalFovRadians = 1; frame.viewSpaceToMeters = 1;
    std::array<void*, 8> packets{};
    for (uint32_t i = 0; i < packets.size(); ++i) {
        frame.frameId = i + 1; frame.reset = i == 0 ? 1 : 0;
        Expect(RbaFsrPrepareDispatch(&frame, &packets[i]) == RbaFsrOk && packets[i], "Could not prepare bounded packet ring");
    }
    void* extra = nullptr;
    Expect(RbaFsrPrepareDispatch(&frame, &extra) == RbaFsrBusy && !extra, "Full packet ring did not reject overflow");
    // Simulate managed RenderTexture destruction before queued plugin callbacks.
    // Packets must keep the inputs alive, then transfer ownership to GPU slots.
    color.Reset(); depth.Reset(); motion.Reset(); exposure.Reset();
    for (auto packet : packets) {
        context.render(RbaFsrDispatch, packet);
        RbaFsrStatus status{}; status.structSize = sizeof(status);
        Expect(RbaFsrGetStatus(context.handle, &status) == RbaFsrOk, "Could not obtain dispatch status");
        if (status.state != RbaFsrReady) throw std::runtime_error(status.error);
        Expect(std::string(status.providerName) == probe.providerName, "Dispatch selected a different provider from startup probe");
    }
    CheckOutput(device, immediate, output.Get(), scale);
    // Validation failures must be bounded and preserve already reconstructed data.
    frame.frameId = 9; frame.preExposure = NAN;
    Expect(RbaFsrPrepareDispatch(&frame, &extra) == RbaFsrOk, "Could not prepare invalid-value test");
    context.render(RbaFsrDispatch, extra);
    RbaFsrStatus status{}; status.structSize = sizeof(status);
    RbaFsrGetStatus(context.handle, &status);
    Expect(status.state == RbaFsrFailed && status.result == RbaFsrInvalidArgument, "Invalid frame parameters were not rejected");
    context.render(RbaFsrDestroy, context.handle);
    RbaFsrGetStatus(context.handle, &status);
    Expect(status.state == RbaFsrDestroyed, "FSR GPU contexts did not retire and destroy cleanly");
    Expect(RbaFsrRelease(context.handle) == RbaFsrOk, "FSR CPU context did not release");
    context.handle = nullptr;
}
}
int wmain(int argc, wchar_t** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::setvbuf(stderr, nullptr, _IONBF, 0);
    try {
        Expect(argc == 2, "Usage: FsrBridgeTests.exe <approved-runtime-directory>");
        Expect(RbaFsrGetAbiVersion() == 1, "Unexpected native ABI");
        RbaFsrCreateDesc invalid{}; void* bad = nullptr;
        Expect(RbaFsrCreate(&invalid, &bad, nullptr, 0) == RbaFsrAbiMismatch && !bad, "ABI mismatch was not rejected");
        ComPtr<ID3D12Debug> debug12;
        if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug12)))) { debug12->EnableDebugLayer(); std::puts("D3D12 debug layer enabled"); }
        else std::puts("D3D12 debug layer unavailable");
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "Create DXGI factory");
        ComPtr<IDXGIAdapter1> adapter, selected;
        SIZE_T mostMemory = 0;
        for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
            DXGI_ADAPTER_DESC1 description{}; adapter->GetDesc1(&description);
            if (!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && (!selected || description.DedicatedVideoMemory > mostMemory)) {
                selected = adapter; mostMemory = description.DedicatedVideoMemory;
            }
            adapter.Reset();
        }
        Expect(selected != nullptr, "No hardware adapter available");
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> immediate;
        const D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        Check(D3D11CreateDevice(selected.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, featureLevels, 2,
            D3D11_SDK_VERSION, &device, nullptr, &immediate), "Create D3D11 hardware device");
        std::puts("D3D11 hardware device ready; starting native-AA probe");
        Run(device.Get(), immediate.Get(), argv[1], 1, true);
        Run(device.Get(), immediate.Get(), argv[1], 2, true);
        Run(device.Get(), immediate.Get(), argv[1], 1, false);
        const std::array<GradientCase, 4> cases = {
            GradientCase{"Native odd", 257, 145, 257, 145, false},
            GradientCase{"Quality 67%", 257, 146, 383, 217, false},
            GradientCase{"Balanced 59%", 226, 129, 383, 217, true},
            GradientCase{"Performance 50%", 192, 109, 383, 217, false}
        };
        for (const auto& test : cases) RunGradient(device.Get(), immediate.Get(), argv[1], test);
        std::puts("PASS: provider probe, D3D11/D3D12 interop, native-AA/upscale dispatch, bounded packets, validation, GPU retirement; odd/aspect gradients, jitter/motion, masks, reset and resize/context switches");
        return 0;
    } catch (const std::exception& failure) {
        std::fprintf(stderr, "FAIL: %s\n", failure.what());
        return 1;
    }
}
