#include "AmdFrameGenerationD3D11.h"
#include "SyntheticHud.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <thread>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
void Expect(bool b, const char* text) { if (!b) throw std::runtime_error(text); }
void Check(HRESULT h, const char* text) { Expect(SUCCEEDED(h), text); }
struct Effect { void* value = nullptr; ~Effect() { if (value) RbaAmdFgD3D11Destroy(value); } };
struct Gpu {
    ComPtr<ID3D11Device5> device;
    ComPtr<ID3D11DeviceContext4> context;
    Gpu() {
        ComPtr<ID3D11Device> d; ComPtr<ID3D11DeviceContext> c;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &d, nullptr, &c), "Create D3D11 device failed");
        Check(d.As(&device), "D3D11 shared fence device unavailable"); Check(c.As(&context), "D3D11 shared fence context unavailable");
    }
    ComPtr<ID3D11Texture2D> Texture(uint32_t width, uint32_t height, DXGI_FORMAT format) {
        D3D11_TEXTURE2D_DESC d{}; d.Width = width; d.Height = height; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = format; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> result; Check(device->CreateTexture2D(&d, nullptr, &result), "Create D3D11 test texture failed"); return result;
    }
    std::vector<uint32_t> Read(ID3D11Texture2D* texture) {
        D3D11_TEXTURE2D_DESC d{}; texture->GetDesc(&d); d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging; Check(device->CreateTexture2D(&d, nullptr, &staging), "Create D3D11 readback failed");
        context->CopyResource(staging.Get(), texture); D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Map D3D11 output failed");
        std::vector<uint32_t> pixels(d.Width * d.Height);
        for (uint32_t y = 0; y < d.Height; ++y) std::memcpy(pixels.data() + y * d.Width,
            static_cast<const char*>(mapped.pData) + y * mapped.RowPitch, d.Width * 4);
        context->Unmap(staging.Get(), 0); return pixels;
    }
};
struct Blocker {
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12Fence> fence12;
    ComPtr<ID3D11Fence> fence11;
    explicit Blocker(Gpu& gpu) {
        ComPtr<IDXGIDevice> dxgi; Check(gpu.device.As(&dxgi), "Get D3D11 adapter failed");
        ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter), "Get D3D11 adapter failed");
        Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device12)), "Create test fence device failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12)), "Create test blocking fence failed");
        HANDLE handle = nullptr; Check(device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle), "Share test fence failed");
        const HRESULT result = gpu.device->OpenSharedFence(handle, IID_PPV_ARGS(&fence11)); CloseHandle(handle);
        Check(result, "Open blocking fence failed");
    }
    ~Blocker() { if (fence12) fence12->Signal(1); }
};
void Run(Gpu& gpu, const wchar_t* runtime, uint32_t renderWidth, uint32_t renderHeight, uint32_t width, uint32_t height, bool timeout, bool hud = false) {
    RbaAmdFgStatus status{}; Effect effect;
    RbaAmdFgD3D11CreateDesc desc{sizeof(desc), 1, runtime, gpu.device.Get(), renderWidth, renderHeight, width, height, 0, 0, RbaAmdFgSrgbEncoded, 0};
    auto invalidCreate = desc; invalidCreate.colorTransfer = 0;
    Expect(RbaAmdFgD3D11Create(&invalidCreate, &effect.value, &status) == RbaAmdFgInvalidArgument, "Implicit color encoding must fail");
    const uint32_t created = RbaAmdFgD3D11Create(&desc, &effect.value, &status);
    std::printf("D3D11 create %ux%u -> %ux%u result=%u provider=%s %s\n", renderWidth, renderHeight, width, height,
        created, status.providerName, status.error);
    Expect(created == RbaAmdFgOk && !status.presentationSupported, "Actual D3D11 FG transport creation failed");
    auto color = gpu.Texture(width, height, DXGI_FORMAT_R8G8B8A8_TYPELESS), output = gpu.Texture(width, height, DXGI_FORMAT_R8G8B8A8_UNORM);
    ComPtr<ID3D11Texture2D> hudLess;
    if (hud) hudLess = gpu.Texture(width, height, DXGI_FORMAT_R8G8B8A8_UNORM);
    const auto dispatch = [&](const RbaAmdFgFrame& frame) {
        return hud ? RbaAmdFgD3D11DispatchWithHudLess(effect.value, &frame, hudLess.Get(), &status) :
            RbaAmdFgD3D11Dispatch(effect.value, &frame, &status);
    };
    auto depth = gpu.Texture(renderWidth, renderHeight, DXGI_FORMAT_R32_FLOAT), motion = gpu.Texture(renderWidth, renderHeight, DXGI_FORMAT_R16G16_FLOAT);
    std::vector<float> depths(renderWidth * renderHeight, 0.5f);
    std::vector<uint32_t> motions(renderWidth * renderHeight, renderWidth == width ? 0xc400u : 0xc000u);
    gpu.context->UpdateSubresource(depth.Get(), 0, nullptr, depths.data(), renderWidth * 4, 0);
    gpu.context->UpdateSubresource(motion.Get(), 0, nullptr, motions.data(), renderWidth * 4, 0);
    RbaAmdFgFrame f{}; f.structSize = sizeof(f); f.abiVersion = 1; f.frameId = 1;
    f.renderWidth = renderWidth; f.renderHeight = renderHeight; f.color = color.Get(); f.depth = depth.Get(); f.motion = motion.Get(); f.output = output.Get();
    f.motionScaleX = 1; f.motionScaleY = 1; f.frameTimeMilliseconds = 16.6667f;
    f.cameraNear = 0.1f; f.cameraFar = 1000; f.cameraFovRadians = 1; f.viewSpaceToMeters = 1;
    f.cameraUp[1] = 1; f.cameraRight[0] = 1; f.cameraForward[2] = 1;
    auto invalid = f; invalid.output = f.color;
    Expect(RbaAmdFgD3D11Dispatch(effect.value, &invalid, &status) == RbaAmdFgInvalidArgument, "D3D11 alias must fail");
    Gpu other; auto foreign = other.Texture(width, height, DXGI_FORMAT_R8G8B8A8_UNORM);
    invalid = f; invalid.output = foreign.Get();
    Expect(RbaAmdFgD3D11Dispatch(effect.value, &invalid, &status) == RbaAmdFgInvalidArgument, "Foreign D3D11 device must fail");
    invalid = f; invalid.renderWidth += 1;
    Expect(RbaAmdFgD3D11Dispatch(effect.value, &invalid, &status) == RbaAmdFgInvalidArgument, "Changed extent must require transport recreation");
    if (hud) {
        Expect(RbaAmdFgD3D11DispatchWithHudLess(effect.value, &f, color.Get(), &status) == RbaAmdFgInvalidArgument,
            "HUD-less/final alias must fail before copies");
        Expect(RbaAmdFgD3D11DispatchWithHudLess(effect.value, &f, output.Get(), &status) == RbaAmdFgInvalidArgument,
            "HUD-less/output alias must fail before copies");
        Expect(RbaAmdFgD3D11DispatchWithHudLess(effect.value, &f, foreign.Get(), &status) == RbaAmdFgInvalidArgument,
            "Foreign HUD-less device must fail before copies");
        auto wrongExtent = gpu.Texture(width + 1, height, DXGI_FORMAT_R8G8B8A8_UNORM);
        Expect(RbaAmdFgD3D11DispatchWithHudLess(effect.value, &f, wrongExtent.Get(), &status) == RbaAmdFgInvalidArgument,
            "HUD-less extent mismatch must fail before copies");
        Expect(RbaAmdFgD3D11DispatchWithHudLess(effect.value, &f, depth.Get(), &status) == RbaAmdFgInvalidArgument,
            "HUD-less format mismatch must fail before copies");
    }
    std::vector<uint32_t> pixels(width * height), previous;
    uint64_t differentCurrent = 0, differentPrevious = 0, error = 0;
    for (uint32_t id = 1; id <= 5; ++id) {
        for (uint32_t y = 0; y < height; ++y) for (uint32_t x = 0; x < width; ++x) {
            const uint32_t sx = (x + width - id * 4) % width;
            const bool rectangle = sx > width / 5 && sx < width / 2 && y > height / 4 && y < height * 3 / 4;
            pixels[y * width + x] = rectangle ? 0xff28dcf0u : 0xff000000u | ((sx * 200 / width) << 8) | (y * 180 / height);
        }
        if (hud) {
            gpu.context->UpdateSubresource(hudLess.Get(), 0, nullptr, pixels.data(), width * 4, 0);
            synthetic_hud::Add(pixels, width, height);
        }
        gpu.context->UpdateSubresource(color.Get(), 0, nullptr, pixels.data(), width * 4, 0); f.frameId = id;
        const uint32_t result = dispatch(f);
        if (result != RbaAmdFgOk) std::printf("Dispatch result=%u %s\n", result, status.error);
        Expect(result == RbaAmdFgOk, "D3D11 GPU round-trip failed");
        auto generated = gpu.Read(output.Get());
        if (hud && id == 5) synthetic_hud::Validate(generated, pixels, width, height);
        if (id == 5) for (size_t i = 0; i < pixels.size(); ++i) {
            if ((generated[i] & 0xffffffu) != (pixels[i] & 0xffffffu)) ++differentCurrent;
            if ((generated[i] & 0xffffffu) != (previous[i] & 0xffffffu)) ++differentPrevious;
            for (uint32_t shift = 0; shift < 24; shift += 8) {
                const int a = (generated[i] >> shift) & 255u, b = (pixels[i] >> shift) & 255u;
                error += static_cast<uint64_t>(a > b ? a - b : b - a);
            }
        }
        previous = pixels;
    }
    Expect(differentCurrent > 100 && differentPrevious > 100 && error < pixels.size() * 3 * 16,
        "D3D11 generated RGB must interpolate real scene content");
    Expect(dispatch(f) == RbaAmdFgInvalidArgument, "Stale frame including HUD-less metadata must reject before copies");
    uint32_t wrongThread = 0;
    std::thread otherThread([&]() { RbaAmdFgStatus s{}; wrongThread = RbaAmdFgD3D11Poll(effect.value, &s); }); otherThread.join();
    Expect(wrongThread == RbaAmdFgInvalidArgument, "Cross-thread Poll must not touch D3D11 immediate context");
    if (timeout) {
        Blocker blocker(gpu);
        Check(gpu.context->Wait(blocker.fence11.Get(), 1), "Queue D3D11 test blocker failed");
        f.frameId = 6;
        invalid = f; invalid.cameraFovRadians = 0;
        const uint64_t invalidStart = GetTickCount64();
        Expect(dispatch(invalid) == RbaAmdFgInvalidArgument &&
            GetTickCount64() - invalidStart < 1000, "Invalid metadata must reject before submitting blocked input copies");
        Expect(RbaAmdFgD3D11Poll(effect.value, &status) == RbaAmdFgOk && status.result == RbaAmdFgOk,
            "Rejected metadata must leave no pending lease or stale error on successful Poll");
        Expect(dispatch(f) == RbaAmdFgGpuBusy, "Blocked D3D11 producer must retain the pending FG frame");
        color.Reset(); depth.Reset(); motion.Reset(); hudLess.Reset(); // Retain every source, including optional HUD-less color.
        Expect(RbaAmdFgD3D11Poll(effect.value, &status) == RbaAmdFgGpuBusy, "Blocked frame must remain pending");
        Check(blocker.fence12->Signal(1), "Release test producer fence failed");
        const uint64_t deadline = GetTickCount64() + 5000;
        uint32_t result;
        do { result = RbaAmdFgD3D11Poll(effect.value, &status); if (result == RbaAmdFgGpuBusy) Sleep(1); }
        while (result == RbaAmdFgGpuBusy && GetTickCount64() < deadline);
        Expect(result == RbaAmdFgOk && status.result == RbaAmdFgOk, "Late D3D11 output delivery failed after input references were released");
        const auto lateOutput = gpu.Read(output.Get());
        Expect(lateOutput.size() == pixels.size(), "Late generated output readback failed");
        if (hud) synthetic_hud::Validate(lateOutput, pixels, width, height);
    }
    const uint32_t destroyed = RbaAmdFgD3D11Destroy(effect.value); if (destroyed == RbaAmdFgOk) effect.value = nullptr;
    Expect(destroyed == RbaAmdFgOk, "D3D11 transport GPU drain failed");
    std::printf("PASS D3D11 RGB: differs current=%llu previous=%llu, MAE=%f, timeout recovery=%s. No presents.\n",
        static_cast<unsigned long long>(differentCurrent), static_cast<unsigned long long>(differentPrevious),
        static_cast<double>(error) / (pixels.size() * 3), timeout ? "tested" : "not requested");
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        Expect(argc == 2, "Usage: AmdFrameGenerationD3D11Tests.exe <absolute pinned runtime directory>");
        Gpu gpu; Run(gpu, argv[1], 320, 180, 640, 360, true); Run(gpu, argv[1], 320, 180, 320, 180, false);
        Run(gpu, argv[1], 320, 180, 640, 360, true, true);
        return 0;
    } catch (const std::exception& e) { std::fprintf(stderr, "FAIL: %s\n", e.what()); return 1; }
}
