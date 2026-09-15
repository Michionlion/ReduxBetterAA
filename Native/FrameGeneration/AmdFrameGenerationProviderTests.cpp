#include "AmdFrameGenerationProvider.h"
#include "AmdFrameGenerationProviderCamera.h"
#include "SyntheticHud.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
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
unsigned Checks = 0; void Expect(bool condition, const char* message) { ++Checks; if (!condition) throw std::runtime_error(message); }
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
    Gpu(ID3D12Device* d12, ID3D12CommandQueue* q12) : device(d12), queue(q12) {
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
void Pump() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
}
struct Fixture {
    HWND parent = nullptr, child = nullptr;
    ComPtr<ID3D11Device> source;
    ComPtr<ID3D11DeviceContext> immediate;
    ComPtr<IDXGISwapChain2> sourceChain;
    HANDLE sourceLatency = nullptr;
    void* provider = nullptr;
    uint32_t sourcePresents = 0;
    std::unique_ptr<Gpu> gpu;
    RbaAmdProviderStatus status{};
    Fixture() {
        WNDCLASSW wc{}; wc.hInstance = GetModuleHandleW(nullptr); wc.lpfnWndProc = DefWindowProcW;
        wc.lpszClassName = L"RbaAmdPacedProviderGpuTest"; RegisterClassW(&wc);
        RECT rect{0, 0, Width, Height}; AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
        parent = CreateWindowExW(WS_EX_NOACTIVATE, wc.lpszClassName, L"AMD paced child validation", WS_OVERLAPPEDWINDOW,
            40, 40, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, wc.hInstance, nullptr);
        child = CreateWindowExW(WS_EX_NOACTIVATE, wc.lpszClassName, L"AMD generated output", WS_CHILD | WS_DISABLED,
            0, 0, Width, Height, parent, nullptr, wc.hInstance, nullptr);
        Expect(parent && child, "Could not create owned parent and disabled child");
        ComPtr<IDXGIFactory6> factory; Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
        for (UINT i = 0;; ++i) {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter)) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 d{}; Check(adapter->GetDesc1(&d)); if (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            if (SUCCEEDED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr, 0, D3D11_SDK_VERSION, &source, nullptr, &immediate))) { std::wprintf(L"Adapter: %ls\n", d.Description); break; }
        }
        Expect(source != nullptr, "No hardware D3D11 source adapter");
        DXGI_SWAP_CHAIN_DESC1 sd{}; sd.Width = Width; sd.Height = Height; sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.SampleDesc.Count = 1; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 3;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; sd.Scaling = DXGI_SCALING_STRETCH; sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        sd.Flags = 0x842; // Actual Unity source flags, including the independent latency wait.
        ComPtr<IDXGISwapChain1> base; Check(factory->CreateSwapChainForHwnd(source.Get(), parent, &sd, nullptr, nullptr, &base));
        Check(base.As(&sourceChain)); Check(sourceChain->SetMaximumFrameLatency(2));
        sourceLatency = sourceChain->GetFrameLatencyWaitableObject(); Expect(sourceLatency != nullptr, "Source latency object missing");
        ShowWindow(parent, SW_SHOWNOACTIVATE); ShowWindow(child, SW_SHOWNOACTIVATE); Pump();
    }
    uint32_t Wait(uint32_t timeout = 10000) {
        const auto end = GetTickCount64() + timeout;
        uint32_t result;
        do { Pump(); result = RbaAmdProviderPoll(provider, &status);
            if (result != RbaAmdProviderBusy) return result; Sleep(1); } while (GetTickCount64() < end);
        return result;
    }
    void Start(const wchar_t* path, uint32_t compatibility, ID3D12CommandQueue* supplied = nullptr) {
        RbaAmdProviderCreateDesc d{sizeof(d), 1, path, source.Get(), child, 17, Width, Height, Width, Height, 0, compatibility, supplied};
        const auto start = GetTickCount64();
        Expect(RbaAmdProviderCreate(&d, &provider, &status) == RbaAmdProviderBusy && provider, "AMD Create did not publish its worker handle");
        Expect(GetTickCount64() - start < 1000, "AMD Create caller blocked on vendor initialization");
        const auto result = Wait();
        std::printf("AMD paced initialization=%u provider=%s family=%u reason=%s\n", result, status.providerName, status.providerFamily, status.reason);
        Expect(result == RbaAmdProviderOk && status.initialized && status.providerVersionId && status.supportedMultiplierMask == (1u << 2),
            "Actual AMD paced child provider creation failed");
        Expect(!compatibility || status.providerFamily == 3, "Explicit compatibility did not select actual SDK family3");
        RbaAmdProviderInterfaces interfaces{sizeof(interfaces), 1, nullptr, nullptr};
        Expect(RbaAmdProviderGetInterfaces(provider, &interfaces) == RbaAmdProviderOk && interfaces.device12 && interfaces.queue12,
            "AMD native provider interfaces unavailable");
        gpu = std::make_unique<Gpu>(static_cast<ID3D12Device*>(interfaces.device12), static_cast<ID3D12CommandQueue*>(interfaces.queue12));
        Expect(!supplied || supplied == gpu->queue.Get(), "AMD provider replaced the caller's actual native queue");
        ComPtr<IDXGIDevice> dxgi; Check(source.As(&dxgi)); ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter));
        DXGI_ADAPTER_DESC ad{}; Check(adapter->GetDesc(&ad)); const LUID luid = gpu->device->GetAdapterLuid();
        Expect(ad.AdapterLuid.LowPart == luid.LowPart && ad.AdapterLuid.HighPart == luid.HighPart, "Owned AMD queue is not on the source adapter");
    }
    void SourcePresent() {
        Expect(WaitForSingleObject(sourceLatency, 5000) == WAIT_OBJECT_0, "Independent Unity-style source latency wait starved");
        Check(sourceChain->Present(1, 0)); ++sourcePresents;
    }
    void Close() {
        const auto end = GetTickCount64() + 10000;
        uint32_t result = RbaAmdProviderBusy;
        while (result == RbaAmdProviderBusy && GetTickCount64() < end) {
            result = RbaAmdProviderDestroy(provider, &status); Pump(); if (result == RbaAmdProviderBusy) Sleep(1);
        }
        std::printf("AMD paced destroy=%u %s\n", result, status.reason);
        Expect(result == RbaAmdProviderOk, "AMD worker/provider cleanup failed; fixture quarantined until process exit");
        provider = nullptr; gpu.reset();
    }
    void End() {
        Expect(provider == nullptr, "Provider must retire before child destruction");
        sourceChain.Reset(); if (sourceLatency) { CloseHandle(sourceLatency); sourceLatency = nullptr; }
        DestroyWindow(child); DestroyWindow(parent); child = nullptr; parent = nullptr;
    }
};
RbaAmdProviderCamera Camera() {
    RbaAmdProviderCamera c{};
    c.nearPlane = .1f; c.farPlane = 1000; c.verticalFovRadians = 1; c.aspectRatio = static_cast<float>(Width) / Height;
    c.viewSpaceToMeters = 1; c.frameTimeMilliseconds = 16.6667f; c.motionScaleX = 1; c.motionScaleY = 1; c.resetHistory = 1;
    c.projection[0] = 1 / std::tan(c.verticalFovRadians / 2) / c.aspectRatio; c.projection[5] = 1 / std::tan(c.verticalFovRadians / 2);
    c.projection[10] = c.farPlane / (c.nearPlane - c.farPlane); c.projection[11] = c.nearPlane * c.farPlane / (c.nearPlane - c.farPlane);
    c.projection[14] = -1;
    for (size_t i = 0; i < 4; ++i) c.worldToView[i * 5] = 1;
    std::memcpy(c.viewProjection, c.projection, sizeof(c.projection)); std::memcpy(c.previousViewProjection, c.projection, sizeof(c.projection));
    return c;
}
void Frames(Fixture& fixture, uint32_t count, bool blockedTest) {
    auto& gpu = *fixture.gpu;
    auto final = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM), scene = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM);
    auto depth = gpu.Texture(DXGI_FORMAT_R32_FLOAT), motion = gpu.Texture(DXGI_FORMAT_R16G16_FLOAT);
    std::vector<float> depths(Width * Height, .5f);
    // half2(-4/640,0): normalized current-to-previous motion; approximate half is intentional.
    std::vector<uint32_t> motions(Width * Height, 0x00009e66u);
    gpu.Upload(depth.Get(), depths.data()); gpu.Upload(motion.Get(), motions.data());
    RbaAmdProviderFrame f{}; f.structSize = sizeof(f); f.abiVersion = 1; f.realFrameId = 1; f.windowEpoch = 17;
    f.finalColor = final.Get(); f.hudlessColor = scene.Get(); f.rawDepth = depth.Get(); f.normalizedMotion = motion.Get(); f.camera = Camera();
    Expect(rba_amd_provider::CameraValid(f.camera), "Synthetic camera metadata is invalid");
    auto invalid = f; invalid.camera.worldToView[0] = 0;
    Expect(RbaAmdProviderSubmit(fixture.provider, &invalid, &fixture.status) == RbaAmdProviderInvalidArgument, "Nonrigid camera must reject before consumption");
    invalid = f; invalid.hudlessColor = invalid.finalColor;
    Expect(RbaAmdProviderSubmit(fixture.provider, &invalid, &fixture.status) == RbaAmdProviderInvalidArgument, "HUDless/final alias must reject before consumption");
    invalid = f; invalid.windowEpoch = 18;
    Expect(RbaAmdProviderSubmit(fixture.provider, &invalid, &fixture.status) == RbaAmdProviderWindowChanged, "Foreign child epoch accepted");
    Expect(RbaAmdProviderPoll(fixture.provider, &fixture.status) == RbaAmdProviderOk && fixture.status.acceptedRealFrames == 0,
        "Rejected metadata consumed a frame or polluted idle Poll");
    std::vector<uint32_t> pixels(Width * Height);
    for (uint32_t id = 1; id <= count; ++id) {
        for (uint32_t y = 0; y < Height; ++y) for (uint32_t x = 0; x < Width; ++x) {
            const uint32_t sx = (x + Width - id * 4) % Width;
            pixels[y * Width + x] = sx > 100 && sx < 240 && y > 80 && y < 280 ? 0xff28dcf0u : 0xff000000u | ((sx / 3) << 8) | (y / 2);
        }
        gpu.Upload(scene.Get(), pixels.data()); synthetic_hud::Add(pixels, Width, Height); gpu.Upload(final.Get(), pixels.data());
        f.realFrameId = id; f.camera.resetHistory = id == 1; fixture.SourcePresent();
        ComPtr<ID3D12Fence> blocker;
        if (blockedTest && id == count) {
            Check(gpu.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&blocker)));
            f.inputReadyFence = blocker.Get(); f.inputReadyValue = 1;
        }
        const uint64_t submitTime = GetTickCount64();
        Expect(RbaAmdProviderSubmit(fixture.provider, &f, &fixture.status) == RbaAmdProviderOk, "AMD worker rejected valid batch");
        Expect(GetTickCount64() - submitTime < 500, "AMD Submit blocked on vendor work");
        if (blocker) {
            Sleep(20); Pump();
            Expect(RbaAmdProviderPoll(fixture.provider, &fixture.status) == RbaAmdProviderBusy && fixture.status.inputBatchPending &&
                fixture.status.lastRetiredFrameId == id - 1, "Blocked producer did not retain current input batch");
            auto next = f; next.realFrameId = id + 1; next.hudlessColor = nullptr;
            Expect(RbaAmdProviderSubmit(fixture.provider, &next, &fixture.status) == RbaAmdProviderBusy && fixture.status.lastAcceptedFrameId == id,
                "Busy worker inspected/consumed a replacement batch");
            // Drop all caller texture references while the worker/SDK still needs them.
            final.Reset(); scene.Reset(); depth.Reset(); motion.Reset();
            Check(blocker->Signal(1));
        }
        const auto result = fixture.Wait();
        if (result != RbaAmdProviderOk) std::printf("AMD frame%u failed=%u %s\n", id, result, fixture.status.reason);
        Expect(result == RbaAmdProviderOk && !fixture.status.inputBatchPending && fixture.status.lastRetiredFrameId == id,
            "Actual AMD paced batch failed or did not retire");
    }
    const auto& s = fixture.status;
    std::printf("AMD paced: accepted=%llu realChild=%llu generatedDispatches=%llu native=%llu source=%u provider=%s\n",
        static_cast<unsigned long long>(s.acceptedRealFrames), static_cast<unsigned long long>(s.realPresentCalls),
        static_cast<unsigned long long>(s.generatedDispatches), static_cast<unsigned long long>(s.nativePresentCountDelta), fixture.sourcePresents, s.providerName);
    Expect(s.acceptedRealFrames == count && s.realPresentCalls == count && s.generatedDispatches >= count - 1,
        "AMD wrapper did not dispatch and present each accepted real frame");
    Expect(s.nativePresentCountValid && s.nativePresentCountDelta > count + count / 2,
        "Actual AMD native child count did not establish generated presentation");
    if (!blockedTest) {
        const auto read = gpu.Read(final.Get());
        Expect(read == pixels, "Retired caller final texture did not retain exact pixels/COMMON state");
        invalid = f;
        Expect(RbaAmdProviderSubmit(fixture.provider, &invalid, &fixture.status) == RbaAmdProviderInvalidArgument,
            "Stale real-frame submission accepted");
        invalid.realFrameId = count + 1;
        RECT window{}; GetWindowRect(fixture.parent, &window);
        SetWindowPos(fixture.parent, nullptr, 0, 0, window.right - window.left + 32, window.bottom - window.top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        Expect(RbaAmdProviderSubmit(fixture.provider, &invalid, &fixture.status) == RbaAmdProviderWindowChanged &&
            fixture.status.lastAcceptedFrameId == count, "Resized parent with old-sized child was incorrectly accepted");
        SetWindowPos(fixture.parent, nullptr, 0, 0, window.right - window.left, window.bottom - window.top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(fixture.child, nullptr, 1, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Expect(RbaAmdProviderSubmit(fixture.provider, &invalid, &fixture.status) == RbaAmdProviderWindowChanged,
            "Child no longer covering parent origin was accepted");
        SetWindowPos(fixture.child, nullptr, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
void StopBlocked(Fixture& fixture) {
    auto& gpu = *fixture.gpu;
    auto final = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM), scene = gpu.Texture(DXGI_FORMAT_R8G8B8A8_UNORM);
    auto depth = gpu.Texture(DXGI_FORMAT_R32_FLOAT), motion = gpu.Texture(DXGI_FORMAT_R16G16_FLOAT);
    std::vector<uint32_t> pixels(Width * Height, 0xff7f7f7fu), motions(Width * Height, 0);
    std::vector<float> depths(Width * Height, .5f);
    gpu.Upload(final.Get(), pixels.data()); gpu.Upload(scene.Get(), pixels.data());
    gpu.Upload(depth.Get(), depths.data()); gpu.Upload(motion.Get(), motions.data());
    ComPtr<ID3D12Fence> blocker; Check(gpu.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&blocker)));
    RbaAmdProviderFrame f{}; f.structSize = sizeof(f); f.abiVersion = 1; f.realFrameId = 1; f.windowEpoch = 17;
    f.finalColor = final.Get(); f.hudlessColor = scene.Get(); f.rawDepth = depth.Get(); f.normalizedMotion = motion.Get(); f.camera = Camera();
    f.inputReadyFence = blocker.Get(); f.inputReadyValue = 1;
    fixture.SourcePresent();
    Expect(RbaAmdProviderSubmit(fixture.provider, &f, &fixture.status) == RbaAmdProviderOk, "Blocked cleanup batch rejected");
    const auto start = GetTickCount64();
    Expect(RbaAmdProviderDestroy(fixture.provider, &fixture.status) == RbaAmdProviderBusy && fixture.status.inputBatchPending &&
        GetTickCount64() - start < 500, "Destroy blocked or released an active vendor/input batch");
    final.Reset(); scene.Reset(); depth.Reset(); motion.Reset();
    Sleep(20); Pump();
    Expect(RbaAmdProviderDestroy(fixture.provider, &fixture.status) == RbaAmdProviderBusy && IsWindow(fixture.child),
        "Destroy falsely retired an unsignaled input dependency");
    Check(blocker->Signal(1)); fixture.Close();
}
}
int wmain(int argc, wchar_t** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    try {
        Expect(argc == 2, "Usage: AmdFrameGenerationProviderTests.exe <pinned signedbin directory>");
        void* none = nullptr; RbaAmdProviderStatus status{};
        Expect(RbaAmdProviderCreate(nullptr, &none, &status) == RbaAmdProviderInvalidArgument && !none, "Null create accepted");
        Expect(RbaAmdProviderDestroy(nullptr, &status) == RbaAmdProviderOk, "Null destroy is not idempotent");
        // Heap fixture intentionally survives assertion failures with live vendor/GPU
        // work. It is only deleted after explicit successful provider retirement.
        auto* fixture = new Fixture;
        fixture->Start(argv[1], 0); Frames(*fixture, 30, true);
        ComPtr<ID3D12CommandQueue> callerQueue = fixture->gpu->queue; fixture->Close();
        fixture->Start(argv[1], 1, callerQueue.Get()); Frames(*fixture, 8, false); fixture->Close();
        fixture->Start(argv[1], 1); StopBlocked(*fixture);
        Expect(fixture->sourcePresents == 39, "Original parent was not maintained exactly once per real frame");
        fixture->End(); delete fixture;
        std::printf("AMD paced provider: %u checks passed. Actual SDK family reported above; no AMD4 hardware claim.\n", Checks);
        return 0;
    } catch (const std::exception& e) { std::fprintf(stderr, "FAILED: %s\n", e.what()); return 1; }
}
