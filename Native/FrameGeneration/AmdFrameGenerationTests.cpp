#include "AmdFrameGeneration.h"
#include "SyntheticHud.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <cstdio>
#include <cwchar>
#include <cstring>
#include <stdexcept>
#include <thread>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
constexpr uint32_t Width = 640, Height = 360;
void Check(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D12 test operation failed"); }
void Expect(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
void Barrier(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to) {
    D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, from, to}; list->ResourceBarrier(1, &b);
}
struct Gpu {
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    HANDLE event = nullptr;
    uint64_t fenceValue = 0;
    Gpu() {
        ComPtr<IDXGIFactory6> factory; Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
        for (UINT index = 0; ; ++index) {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter)) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 desc{}; Check(adapter->GetDesc1(&desc));
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            if (SUCCEEDED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)))) {
                std::wprintf(L"Adapter: %ls\n", desc.Description); break;
            }
        }
        Expect(device != nullptr, "No hardware D3D12 adapter");
        D3D12_COMMAND_QUEUE_DESC desc{}; desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue)));
        Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
        Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)));
        Check(list->Close()); Check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr); Expect(event != nullptr, "Fence event failed");
    }
    ~Gpu() { if (event) CloseHandle(event); }
    void Begin() { Check(allocator->Reset()); Check(list->Reset(allocator.Get(), nullptr)); }
    void End() {
        Check(list->Close()); ID3D12CommandList* commands[] = {list.Get()}; queue->ExecuteCommandLists(1, commands);
        Check(queue->Signal(fence.Get(), ++fenceValue)); Check(fence->SetEventOnCompletion(fenceValue, event));
        Expect(WaitForSingleObject(event, 5000) == WAIT_OBJECT_0, "Synthetic GPU work timed out");
        Check(device->GetDeviceRemovedReason());
    }
    ComPtr<ID3D12Resource> Texture(DXGI_FORMAT format) {
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        d.Width = Width; d.Height = Height; d.DepthOrArraySize = 1; d.MipLevels = 1;
        d.Format = format; d.SampleDesc.Count = 1; d.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        ComPtr<ID3D12Resource> resource;
        Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&resource)));
        return resource;
    }
    ComPtr<ID3D12Resource> Buffer(uint64_t bytes, D3D12_HEAP_TYPE kind) {
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = kind;
        D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        d.Width = bytes; d.Height = 1; d.DepthOrArraySize = 1; d.MipLevels = 1;
        d.SampleDesc.Count = 1; d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ComPtr<ID3D12Resource> resource;
        Check(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d,
            kind == D3D12_HEAP_TYPE_UPLOAD ? D3D12_RESOURCE_STATE_GENERIC_READ : D3D12_RESOURCE_STATE_COPY_DEST,
            nullptr, IID_PPV_ARGS(&resource))); return resource;
    }
    void Upload(ID3D12Resource* texture, const void* data) {
        const auto desc = texture->GetDesc(); D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{}; uint64_t bytes = 0;
        device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &bytes);
        auto upload = Buffer(bytes, D3D12_HEAP_TYPE_UPLOAD); void* mapped = nullptr;
        D3D12_RANGE empty{0, 0}; Check(upload->Map(0, &empty, &mapped));
        for (uint32_t y = 0; y < Height; ++y) std::memcpy(static_cast<char*>(mapped) + y * footprint.Footprint.RowPitch,
            static_cast<const char*>(data) + y * Width * 4, Width * 4);
        upload->Unmap(0, nullptr);
        Begin(); Barrier(list.Get(), texture, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_TEXTURE_COPY_LOCATION from{}, to{}; from.pResource = upload.Get(); from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        from.PlacedFootprint = footprint; to.pResource = texture; to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
        Barrier(list.Get(), texture, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON); End();
    }
    std::vector<uint32_t> Read(ID3D12Resource* texture) {
        const auto desc = texture->GetDesc(); D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{}; uint64_t bytes = 0;
        device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &bytes);
        auto readback = Buffer(bytes, D3D12_HEAP_TYPE_READBACK);
        Begin(); Barrier(list.Get(), texture, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
        D3D12_TEXTURE_COPY_LOCATION from{}, to{}; from.pResource = texture; from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        to.pResource = readback.Get(); to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; to.PlacedFootprint = footprint;
        list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
        Barrier(list.Get(), texture, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON); End();
        void* mapped = nullptr; D3D12_RANGE range{0, static_cast<SIZE_T>(bytes)}; Check(readback->Map(0, &range, &mapped));
        std::vector<uint32_t> pixels(Width * Height);
        for (uint32_t y = 0; y < Height; ++y) std::memcpy(pixels.data() + y * Width,
            static_cast<char*>(mapped) + y * footprint.Footprint.RowPitch, Width * 4);
        D3D12_RANGE empty{0, 0}; readback->Unmap(0, &empty); return pixels;
    }
};
struct Effect {
    void* value = nullptr;
    ~Effect() { if (value) RbaAmdFgDestroy(value); }
};
struct UnblockQueue {
    ComPtr<ID3D12Fence> fence;
    ~UnblockQueue() { if (fence) fence->Signal(2); }
};
void CheckHudCompatibility(Gpu& gpu, void* effect) {
    auto color = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM), hudLess = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM);
    auto output = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM), depth = gpu.Texture(DXGI_FORMAT_R32_FLOAT);
    auto motion = gpu.Texture(DXGI_FORMAT_R16G16_FLOAT);
    std::vector<float> depths(Width * Height, 0.5f);
    std::vector<uint32_t> motions(Width * Height, 0x0000c400u);
    gpu.Upload(depth.Get(), depths.data()); gpu.Upload(motion.Get(), motions.data());
    RbaAmdFgFrame f{}; f.structSize = sizeof(f); f.abiVersion = 1; f.frameId = 1;
    f.renderWidth = Width; f.renderHeight = Height; f.color = color.Get(); f.depth = depth.Get();
    f.motion = motion.Get(); f.output = output.Get(); f.motionScaleX = 1; f.motionScaleY = 1;
    f.frameTimeMilliseconds = 16.6667f; f.cameraNear = 0.1f; f.cameraFar = 1000;
    f.cameraFovRadians = 1; f.viewSpaceToMeters = 1; f.cameraUp[1] = 1; f.cameraRight[0] = 1; f.cameraForward[2] = 1;
    RbaAmdFgStatus status{};
    Expect(RbaAmdFgDispatchWithHudLess(effect, &f, color.Get(), &status) == RbaAmdFgInvalidArgument,
        "D3D12 HUD-less/final alias must reject before submission");
    Expect(RbaAmdFgDispatchWithHudLess(effect, &f, output.Get(), &status) == RbaAmdFgInvalidArgument,
        "D3D12 HUD-less/output alias must reject before submission");
    Expect(RbaAmdFgDispatchWithHudLess(effect, &f, depth.Get(), &status) == RbaAmdFgInvalidArgument,
        "D3D12 HUD-less color format mismatch must reject before submission");
    Expect(RbaAmdFgPoll(effect, &status) == RbaAmdFgOk && status.result == RbaAmdFgOk,
        "Rejected HUD-less input must leave coherent idle Poll status");
    std::vector<uint32_t> pixels(Width * Height), previous;
    for (uint32_t id = 1; id <= 6; ++id) {
        for (uint32_t y = 0; y < Height; ++y) for (uint32_t x = 0; x < Width; ++x) {
            const uint32_t sx = (x + Width - id * 4) % Width;
            pixels[y * Width + x] = sx > 100 && sx < 240 && y > 80 && y < 280 ?
                0xff28dcf0u : 0xff000000u | ((sx / 3) << 8) | (y / 2);
        }
        gpu.Upload(hudLess.Get(), pixels.data()); synthetic_hud::Add(pixels, Width, Height);
        gpu.Upload(color.Get(), pixels.data()); f.frameId = id;
        Expect(RbaAmdFgDispatchWithHudLess(effect, &f, hudLess.Get(), &status) == RbaAmdFgOk,
            "D3D12 HUD-less compatibility dispatch failed");
        if (id == 6) {
            const auto generated = gpu.Read(output.Get()); synthetic_hud::Validate(generated, pixels, Width, Height);
            uint64_t differsCurrent = 0, differsPrevious = 0;
            for (uint32_t y = 0; y < Height; ++y) for (uint32_t x = 0; x < Width; ++x) {
                if (synthetic_hud::Contains(x, y, Width, Height)) continue;
                const size_t i = static_cast<size_t>(y) * Width + x;
                if ((generated[i] & 0xffffffu) != (pixels[i] & 0xffffffu)) ++differsCurrent;
                if ((generated[i] & 0xffffffu) != (previous[i] & 0xffffffu)) ++differsPrevious;
            }
            Expect(differsCurrent > 100 && differsPrevious > 100, "D3D12 UI compatibility must still interpolate the moving scene");
            std::printf("D3D12 HUD compatibility scene differs from current=%llu, previous=%llu pixels.\n",
                static_cast<unsigned long long>(differsCurrent), static_cast<unsigned long long>(differsPrevious));
        }
        previous = pixels;
    }
    Expect(RbaAmdFgDispatchWithHudLess(effect, &f, hudLess.Get(), &status) == RbaAmdFgInvalidArgument,
        "Stale HUD-less frame must reject before submission");
    // Switching between the optional UI strategy and legacy scene-only calls
    // uses the same ABI/context and resets temporal history at the transition.
    f.frameId = 7; Expect(RbaAmdFgDispatch(effect, &f, &status) == RbaAmdFgOk, "Clearing optional HUD-less input failed");
    f.frameId = 8; Expect(RbaAmdFgDispatchWithHudLess(effect, &f, hudLess.Get(), &status) == RbaAmdFgOk,
        "Restoring optional HUD-less input failed");
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        Expect(argc == 2 || (argc == 3 && std::wcscmp(argv[2], L"--debug") == 0),
            "Usage: AmdFrameGenerationTests.exe <absolute SDK signedbin directory> [--debug]");
        ComPtr<ID3D12Debug> debug;
        if (argc == 3) {
            Expect(SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug))),
                "D3D12 debug layer unavailable (Windows Graphics Tools required); debug validation was not performed");
            debug->EnableDebugLayer();
        }
        RbaAmdFgStatus status{}; void* rejected = nullptr;
        Expect(RbaAmdFgCreate(nullptr, &rejected, &status) == RbaAmdFgInvalidArgument && !rejected, "Null create not rejected");
        Expect(RbaAmdFgDestroy(nullptr) == RbaAmdFgOk, "Null destruction must be idempotent");
        Gpu gpu;
        ComPtr<ID3D12InfoQueue> debugMessages;
        if (debug) Check(gpu.device.As(&debugMessages));
        RbaAmdFgCreateDesc desc{sizeof(desc), 1, argv[1], gpu.queue.Get(), Width, Height, Width, Height, 0, 0};
        Effect effect;
        auto result = RbaAmdFgCreate(&desc, &effect.value, &status);
        std::printf("Create: %u, provider: %s, error: %s\n", result, status.providerName, status.error);
        Expect(result == RbaAmdFgOk && effect.value, "Actual AMD FG context creation failed");
        Expect(status.maximumMultiplier == 2 && status.presentationSupported == 0 && status.providerVersionId,
            "Texture provider must identify itself without claiming presentation support");
        auto color = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM), output = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM);
        auto depth = gpu.Texture(DXGI_FORMAT_R32_FLOAT), motion = gpu.Texture(DXGI_FORMAT_R16G16_FLOAT);
        std::vector<float> depths(Width * Height, 0.5f);
        std::vector<uint32_t> motions(Width * Height, 0x0000c400u); // half2(-4,0), pixel motion.
        gpu.Upload(depth.Get(), depths.data()); gpu.Upload(motion.Get(), motions.data());
        RbaAmdFgFrame frame{};
        frame.structSize = sizeof(frame); frame.abiVersion = 1; frame.renderWidth = Width; frame.renderHeight = Height;
        frame.color = color.Get(); frame.depth = depth.Get(); frame.motion = motion.Get(); frame.output = output.Get();
        frame.motionScaleX = 1; frame.motionScaleY = 1; frame.frameTimeMilliseconds = 16.6667f;
        frame.cameraNear = 0.1f; frame.cameraFar = 1000; frame.cameraFovRadians = 1; frame.viewSpaceToMeters = 1;
        frame.cameraUp[1] = 1; frame.cameraRight[0] = 1; frame.cameraForward[2] = 1;
        frame.frameId = 1;
        auto invalid = frame; invalid.output = invalid.color;
        Expect(RbaAmdFgDispatch(effect.value, &invalid, &status) == RbaAmdFgInvalidArgument, "Input/output alias must fail before dispatch");
        invalid = frame; invalid.cameraUp[1] = 0;
        Expect(RbaAmdFgDispatch(effect.value, &invalid, &status) == RbaAmdFgInvalidArgument, "Degenerate camera axes must fail before dispatch");
        Expect(RbaAmdFgPoll(effect.value, &status) == RbaAmdFgOk && status.result == RbaAmdFgOk,
            "Rejected metadata must not leave inconsistent direct Poll status");
        std::vector<uint32_t> previous, pixels(Width * Height);
        uint64_t differentFromCurrent = 0, differentFromPrevious = 0, absoluteColorError = 0;
        for (uint32_t id = 1; id <= 6; ++id) {
            for (uint32_t y = 0; y < Height; ++y) for (uint32_t x = 0; x < Width; ++x) {
                const uint32_t sx = (x + Width - id * 4) % Width;
                const bool rectangle = sx > 100 && sx < 240 && y > 80 && y < 280;
                pixels[y * Width + x] = rectangle ? 0xff28dcf0u : (0xff000000u | ((sx / 3) << 8) | (y / 2));
            }
            gpu.Upload(color.Get(), pixels.data()); frame.frameId = id;
            result = RbaAmdFgDispatch(effect.value, &frame, &status);
            std::printf("Frame %u: result=%u generated=%u %s\n", id, result, status.lastGeneratedFrames, status.error);
            Expect(result == RbaAmdFgOk, "Synthetic AMD FG GPU dispatch failed");
            auto generated = gpu.Read(output.Get());
            if (id == 6) {
                for (size_t i = 0; i < pixels.size(); ++i) {
                    if ((generated[i] & 0x00ffffffu) != (pixels[i] & 0x00ffffffu)) ++differentFromCurrent;
                    if ((generated[i] & 0x00ffffffu) != (previous[i] & 0x00ffffffu)) ++differentFromPrevious;
                    for (uint32_t shift = 0; shift < 24; shift += 8) {
                        const int expected = static_cast<int>((pixels[i] >> shift) & 255u);
                        const int actual = static_cast<int>((generated[i] >> shift) & 255u);
                        absoluteColorError += static_cast<uint64_t>(expected > actual ? expected - actual : actual - expected);
                    }
                }
                Expect(status.lastGeneratedFrames == 1, "Vendor did not report a generated texture");
                Expect(differentFromCurrent > 100 && differentFromPrevious > 100, "Generated texture must differ from both real source frames");
                Expect(absoluteColorError < pixels.size() * 3 * 16, "Generated RGB must retain scene content, not merely overwrite output");
            }
            previous = pixels;
        }
        Expect(RbaAmdFgDispatch(effect.value, &frame, &status) == RbaAmdFgInvalidArgument, "Duplicate real frame must be rejected");
        frame.frameId = 20; // A discontinuity is a safe reset, not interpolation across missing frames.
        Expect(RbaAmdFgDispatch(effect.value, &frame, &status) == RbaAmdFgOk, "Disjoint real-frame reset failed");
        Expect(status.submittedRealFrames == 7, "Invalid frame changed submission count");
        Expect(RbaAmdFgPoll(effect.value, &status) == RbaAmdFgOk, "Completed frame still pending");
        // Real delayed-queue regression: a timed-out frame signals the event after
        // Dispatch returns, and Poll retires it without consuming that event.
        // The next submission must still wait for its own fence, not the old event.
        UnblockQueue unblock;
        Check(gpu.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&unblock.fence)));
        Check(gpu.queue->Wait(unblock.fence.Get(), 1));
        frame.frameId = 21;
        Expect(RbaAmdFgDispatch(effect.value, &frame, &status) == RbaAmdFgGpuBusy, "Blocked queue must time out without releasing the frame");
        Expect(RbaAmdFgPoll(effect.value, &status) == RbaAmdFgGpuBusy, "Polling must retain unfinished input resources");
        Check(unblock.fence->Signal(1));
        gpu.Begin(); gpu.End(); // Guarantees the old FG fence/event have signaled.
        Expect(RbaAmdFgPoll(effect.value, &status) == RbaAmdFgOk && status.result == RbaAmdFgOk, "Late completion did not retire the timed-out frame");
        Check(gpu.queue->Wait(unblock.fence.Get(), 2));
        std::thread release([fence = unblock.fence]() { Sleep(150); fence->Signal(2); });
        const uint64_t before = GetTickCount64();
        frame.frameId = 22;
        result = RbaAmdFgDispatch(effect.value, &frame, &status);
        const uint64_t elapsed = GetTickCount64() - before;
        release.join();
        Expect(result == RbaAmdFgOk && elapsed >= 100, "Stale completion event retired a newer unfinished frame");
        Expect(status.submittedRealFrames == 9, "Timeout recovery changed the real-frame submission count");
        std::printf("Timeout/poll recovery: 9 submissions, next delayed dispatch waited %llu ms for its own fence.\n",
            static_cast<unsigned long long>(elapsed));
        Expect(RbaAmdFgDestroy(effect.value) == RbaAmdFgOk, "Drained context destruction failed"); effect.value = nullptr;
        desc.preferCompatibility = 1;
        Expect(RbaAmdFgCreate(&desc, &effect.value, &status) == RbaAmdFgOk, "Enumerated compatibility provider creation failed");
        Expect(std::strstr(status.providerName, "3.1.") != nullptr, "Compatibility selection did not select FSR 3.1");
        CheckHudCompatibility(gpu, effect.value);
        Expect(RbaAmdFgDestroy(effect.value) == RbaAmdFgOk, "Compatibility context destruction failed"); effect.value = nullptr;
        if (debugMessages) {
            uint64_t errors = 0;
            for (uint64_t i = 0; i < debugMessages->GetNumStoredMessages(); ++i) {
                SIZE_T size = 0; Check(debugMessages->GetMessage(i, nullptr, &size));
                std::vector<unsigned char> memory(size); auto* message = reinterpret_cast<D3D12_MESSAGE*>(memory.data());
                Check(debugMessages->GetMessage(i, message, &size));
                if (message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR) { ++errors; std::printf("D3D12 ERROR: %s\n", message->pDescription); }
            }
            Expect(errors == 0, "D3D12 debug layer reported errors");
        }
        std::printf("PASS: generated RGB differs from current=%llu, previous=%llu pixels; RGB MAE=%f; compatibility=%s. No presents were performed.\n",
            static_cast<unsigned long long>(differentFromCurrent), static_cast<unsigned long long>(differentFromPrevious),
            static_cast<double>(absoluteColorError) / (Width * Height * 3), status.providerName);
        return 0;
    } catch (const std::exception& error) { std::fprintf(stderr, "FAIL: %s\n", error.what()); return 1; }
}
