// Explicit opt-in visible standalone diagnostic. No Unity/game hooks or assets.
#include "StreamlineProvider.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <string>
using Microsoft::WRL::ComPtr;
namespace {
constexpr UINT Width = 1280, Height = 720;
void Need(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
void Hr(HRESULT value, const char* operation) {
    if (FAILED(value)) { char message[256]{}; std::snprintf(message, sizeof(message), "%s HRESULT0x%08lx", operation, static_cast<unsigned long>(value)); throw std::runtime_error(message); }
}
void Fg(uint32_t value, const RbaSlFgStatus& status, const char* operation) {
    if (value != RbaSlFgOk) { char message[768]{}; std::snprintf(message, sizeof(message), "%s result%u: %s", operation, value, status.reason); throw std::runtime_error(message); }
}
void Mark(uint32_t value, const char* operation) { Need(value == RbaSlFgOk, operation); }
LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_CLOSE) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}
struct Texture {
    ComPtr<ID3D12Resource> gpu, upload;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
    unsigned char* mapped = nullptr;
    void Create(ID3D12Device* device, DXGI_FORMAT format) {
        D3D12_RESOURCE_DESC desc{}; desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; desc.Width = Width; desc.Height = Height;
        desc.DepthOrArraySize = desc.MipLevels = 1; desc.Format = format; desc.SampleDesc.Count = 1;
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        Hr(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&gpu)), "Create test texture");
        uint64_t bytes = 0; device->GetCopyableFootprints(&desc, 0, 1, 0, &layout, nullptr, nullptr, &bytes);
        heap.Type = D3D12_HEAP_TYPE_UPLOAD; D3D12_RESOURCE_DESC buffer{}; buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        buffer.Width = bytes; buffer.Height = 1; buffer.DepthOrArraySize = 1; buffer.MipLevels = 1; buffer.SampleDesc.Count = 1; buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Hr(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upload)), "Create test upload");
        D3D12_RANGE none{}; Hr(upload->Map(0, &none, reinterpret_cast<void**>(&mapped)), "Map test upload");
    }
    void Copy(ID3D12GraphicsCommandList* list) const {
        D3D12_RESOURCE_BARRIER barrier{}; barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition = {gpu.Get(), D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST};
        list->ResourceBarrier(1, &barrier);
        D3D12_TEXTURE_COPY_LOCATION dst{}; dst.pResource = gpu.Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        D3D12_TEXTURE_COPY_LOCATION src{}; src.pResource = upload.Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; src.PlacedFootprint = layout;
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter); list->ResourceBarrier(1, &barrier);
    }
};
struct Diagnostic {
    HWND parent = nullptr, child = nullptr;
    uint64_t handle = 0;
    RbaSlFgStatus status{};
    RbaSlFgInterfaces interfaces{};
    ComPtr<ID3D11Device> sourceDevice;
    ComPtr<ID3D11DeviceContext> sourceContext;
    ComPtr<IDXGISwapChain2> sourceChain;
    ComPtr<ID3D11RenderTargetView> sourceView;
    HANDLE sourceWaitable = nullptr;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    std::array<Texture, 4> textures;
    uint64_t sourcePresents = 0, sourceWaits = 0;
    void* retiredCommonAddress = nullptr;
    bool forceCommonRelocation = false;
    bool hiddenFirstPresent = false;
    void Start(const wchar_t* runtime, uint32_t multiplier, bool discovery = true) {
        status.structSize = sizeof(status); status.abiVersion = 1;
        WNDCLASSW cls{}; cls.lpfnWndProc = WindowProc; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"ReduxBetterAA.Provider.Diagnostic";
        Need(RegisterClassW(&cls) != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS, "Register diagnostic class");
        RECT rect{0, 0, Width, Height}; Need(AdjustWindowRectEx(&rect, WS_OVERLAPPEDWINDOW, FALSE, WS_EX_NOREDIRECTIONBITMAP), "Adjust diagnostic window");
        parent = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP, cls.lpszClassName, L"ReduxBetterAA NVIDIA provider diagnostic", WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, cls.hInstance, nullptr);
        Need(parent != nullptr, "Create diagnostic parent");
        child = CreateWindowExW(0, cls.lpszClassName, L"Provider output", WS_CHILD | WS_DISABLED, 0, 0, Width, Height, parent, nullptr, cls.hInstance, nullptr);
        Need(child != nullptr && !IsWindowVisible(child), "Create initially hidden disabled child");
        ComPtr<IDXGIFactory2> factory; Hr(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "Create native source factory");
        ComPtr<IDXGIAdapter1> adapter; Hr(factory->EnumAdapters1(0, &adapter), "Select source adapter");
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0}; D3D_FEATURE_LEVEL level{};
        Hr(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION,
            &sourceDevice, &level, &sourceContext), "Create native source D3D11 device");
        DXGI_SWAP_CHAIN_DESC1 sd{}; sd.Width = Width; sd.Height = Height; sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.SampleDesc.Count = 1; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 2;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; sd.Scaling = DXGI_SCALING_STRETCH; sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE; sd.Flags = 0x842;
        ComPtr<IDXGISwapChain1> source; Hr(factory->CreateSwapChainForHwnd(sourceDevice.Get(), parent, &sd, nullptr, nullptr, &source), "Create unhooked parent source chain");
        Hr(source.As(&sourceChain), "Query source latency chain"); Hr(sourceChain->SetMaximumFrameLatency(2), "Set source maxLatency2");
        sourceWaitable = sourceChain->GetFrameLatencyWaitableObject(); Need(sourceWaitable != nullptr, "Obtain source waitable");
        ComPtr<ID3D11Texture2D> back; Hr(sourceChain->GetBuffer(0, IID_PPV_ARGS(&back)), "Get source backbuffer");
        Hr(sourceDevice->CreateRenderTargetView(back.Get(), nullptr, &sourceView), "Create source view");
        StartProvider(runtime,multiplier,discovery);
    }
    void StartNextSession(const wchar_t* runtime,uint32_t multiplier) {
        Need(parent != nullptr && child == nullptr && handle == 0,"Previous session must retire before reusing source parent");
        child=CreateWindowExW(0,L"ReduxBetterAA.Provider.Diagnostic",L"Provider output",WS_CHILD|WS_DISABLED,
            0,0,Width,Height,parent,nullptr,GetModuleHandleW(nullptr),nullptr);
        Need(child != nullptr && !IsWindowVisible(child),"Recreate child on retained source parent");
        sourcePresents=sourceWaits=0;
        StartProvider(runtime,multiplier,false);
    }
    void StartProvider(const wchar_t* runtime,uint32_t multiplier,bool discovery) {
        RbaSlFgCreateDesc create{sizeof(create), 1, runtime, sourceDevice.Get(), child, 1, Width, Height, multiplier, 33333};
        Fg(RbaSlFgCreate(&create, &handle, &status), status, "Create provider");
        Need(!IsWindowVisible(child) && status.realPresentCalls == 0 && !status.generatedPresentationObserved,
            "Hidden capability creation must not present or claim generated frames");
        if (discovery) {
            const auto discoveryMask = status.supportedMultiplierMask;
            Fg(RbaSlFgDestroy(handle, 5000, &status), status, "Destroy hidden discovery provider"); handle = 0;
            for (const wchar_t* name : {L"sl.interposer.dll", L"sl.common.dll", L"sl.pcl.dll", L"sl.reflex.dll", L"sl.dlss_g.dll", L"nvngx_dlssg.dll"})
                if (GetModuleHandleW(name)) std::printf("POST_SHUTDOWN_LOADED %ls\n", name);
            Fg(RbaSlFgCreate(&create, &handle, &status), status, "Recreate provider after hidden discovery");
            Need(status.supportedMultiplierMask == discoveryMask && status.realPresentCalls == 0,
                "Discovery/recreate changed actual capability or presented unexpectedly");
        }
        interfaces.structSize = sizeof(interfaces); interfaces.abiVersion = 1;
        Fg(RbaSlFgGetInterfaces(handle, &interfaces), status, "Get native interfaces");
        auto* device = static_cast<ID3D12Device*>(interfaces.device12);
        Hr(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create input allocator");
        Hr(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)), "Create input list"); Hr(list->Close(), "Close input list");
        textures[0].Create(device, DXGI_FORMAT_R8G8B8A8_UNORM); textures[1].Create(device, DXGI_FORMAT_R8G8B8A8_UNORM);
        textures[2].Create(device, DXGI_FORMAT_R32_FLOAT); textures[3].Create(device, DXGI_FORMAT_R32G32_FLOAT);
        ShowWindow(parent, SW_SHOW);
        if (!hiddenFirstPresent) ShowWindow(child, SW_SHOWNA);
        // Start-Process Hidden can override the first ShowWindow through the
        // process startup flags. Explicitly show this application's own window.
        Need(SetWindowPos(parent, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW), "Show owned diagnostic parent");
        SetForegroundWindow(parent); SetActiveWindow(parent); SetFocus(parent); UpdateWindow(parent);
        Need(IsWindowVisible(parent) && (hiddenFirstPresent ? !IsWindowVisible(child) : IsWindowVisible(child)),
            "Actual diagnostic parent/child visibility differs from the selected control");
        std::printf("CREATE multiplier=%u mask=0x%x maxGenerated=%u parent=%p child=%p childEnabled=%d parentStyle=0x%llx childStyle=0x%llx foregroundParent=%d\n",
            multiplier, status.supportedMultiplierMask, status.maxGeneratedFrames, parent, child, IsWindowEnabled(child),
            static_cast<unsigned long long>(GetWindowLongPtrW(parent, GWL_STYLE)), static_cast<unsigned long long>(GetWindowLongPtrW(child, GWL_STYLE)), GetForegroundWindow() == parent);
    }
    void Run(uint32_t multiplier,bool preserveParent=false) {
        RbaSlFgFrame frame{}; frame.structSize = sizeof(frame); frame.abiVersion = 1; frame.windowEpoch = 1;
        auto& camera = frame.camera; camera.nearPlane = .1f; camera.farPlane = 100.f; camera.verticalFovRadians = 1.0471975512f;
        camera.aspectRatio = float(Width) / Height; camera.viewSpaceToMeters = 1; camera.motionScaleX = camera.motionScaleY = 1;
        const float cot = 1.f / std::tan(camera.verticalFovRadians * .5f);
        camera.projection[0] = cot / camera.aspectRatio; camera.projection[5] = cot;
        camera.projection[10] = camera.farPlane / (camera.nearPlane - camera.farPlane); camera.projection[11] = camera.nearPlane * camera.farPlane / (camera.nearPlane - camera.farPlane); camera.projection[14] = -1;
        for (size_t i = 0; i < 4; ++i) camera.worldToView[i * 4 + i] = 1;
        for (size_t i = 0; i < 16; ++i) camera.viewProjection[i] = camera.previousViewProjection[i] = camera.projection[i];
        auto* queue = static_cast<ID3D12CommandQueue*>(interfaces.queue12);
        LARGE_INTEGER frequency{}, last{}; QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&last);
        for (uint32_t id = 1; id <= 120; ++id) {
            MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { Need(message.message != WM_QUIT, "Diagnostic window closed"); TranslateMessage(&message); DispatchMessageW(&message); }
            Need(WaitForSingleObject(sourceWaitable, 2000) == WAIT_OBJECT_0, "Source frame-latency wait timed out"); ++sourceWaits;
            Mark(RbaSlFgBeginSimulation(handle, id), "Begin actual simulation");
            for (uint32_t y = 0; y < Height; ++y) {
                auto* final = reinterpret_cast<uint32_t*>(textures[0].mapped + y * textures[0].layout.Footprint.RowPitch);
                auto* hudless = reinterpret_cast<uint32_t*>(textures[1].mapped + y * textures[1].layout.Footprint.RowPitch);
                auto* depth = reinterpret_cast<float*>(textures[2].mapped + y * textures[2].layout.Footprint.RowPitch);
                auto* motion = reinterpret_cast<float*>(textures[3].mapped + y * textures[3].layout.Footprint.RowPitch);
                for (uint32_t x = 0; x < Width; ++x) {
                    const bool cell = (((x + Width - (2 * id % Width)) / 32) + y / 32) % 2;
                    final[x] = hudless[x] = cell ? 0xff4080d0u : 0xffc0a020u;
                    depth[x] = camera.farPlane / (camera.farPlane - camera.nearPlane) - camera.nearPlane * camera.farPlane / ((camera.farPlane - camera.nearPlane) * 2.f);
                    motion[x * 2] = -2.f / Width; motion[x * 2 + 1] = 0;
                }
            }
            Mark(RbaSlFgEndSimulation(handle, id), "End actual simulation"); Mark(RbaSlFgBeginRender(handle, id), "Begin actual render submission");
            Hr(allocator->Reset(), "Reset input allocator"); Hr(list->Reset(allocator.Get(), nullptr), "Reset input list");
            for (const auto& texture : textures) texture.Copy(list.Get()); Hr(list->Close(), "Close input uploads");
            ID3D12CommandList* commands[]{list.Get()}; queue->ExecuteCommandLists(1, commands);
            Mark(RbaSlFgEndRender(handle, id), "End actual render submission");
            UINT before = 0, after = 0; Hr(sourceChain->GetLastPresentCount(&before), "Read source pre-Present count");
            const float red[]{.9f, .1f, .05f, 1.f}; sourceContext->ClearRenderTargetView(sourceView.Get(), red);
            const auto sourceResult = sourceChain->Present(1, 0); ++sourcePresents;
            Hr(sourceChain->GetLastPresentCount(&after), "Read source post-Present count");
            Need(sourceResult == S_OK && after - before == 1, "Unhooked parent did not present exactly once without occlusion");
            LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
            camera.resetHistory = id == 1; camera.frameTimeMilliseconds = id == 1 ? 0.f : static_cast<float>((now.QuadPart - last.QuadPart) * 1000.0 / frequency.QuadPart); last = now;
            frame.realFrameId = id; frame.finalColor = textures[0].gpu.Get(); frame.hudlessColor = textures[1].gpu.Get(); frame.rawDepth = textures[2].gpu.Get(); frame.normalizedMotion = textures[3].gpu.Get();
            Fg(RbaSlFgSubmit(handle, &frame, &status), status, "Submit real frame");
            const auto submitted = status.lastSubmittedFrameId;
            Need(RbaSlFgSubmit(handle, &frame, &status) == RbaSlFgBusy && status.lastSubmittedFrameId == submitted && status.inputBatchPending && !status.poisoned,
                "Busy failed to preserve actual provider input lease");
            if (id == 1) for (auto& texture : textures) texture.gpu.Reset(); // Provider must retain all caller-released resources.
            Fg(RbaSlFgPoll(handle, 5000, &status), status, "Retire every SDK input reader");
            Need(status.lastRetiredFrameId == id && !status.inputBatchPending, "Poll did not prove actual input retirement");
            if (id == 1 && hiddenFirstPresent) {
                Need(!IsWindowVisible(child), "The hidden-first control showed its child before first completion");
                std::printf("HIDDEN_FIRST_COMPLETE real=%u sdk=%u status=%u hr=0x%08x childVisible=0\n",
                    id, status.lastSdkPresented, status.sdkRuntimeStatus, status.lastPresentHresult);
                Need(SetWindowPos(child, HWND_TOP, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW) != FALSE && IsWindowVisible(child),
                    "Show actual child after first completed hidden Present");
            }
            if (id == 1) {
                auto* device = static_cast<ID3D12Device*>(interfaces.device12);
                textures[0].Create(device, DXGI_FORMAT_R8G8B8A8_UNORM); textures[1].Create(device, DXGI_FORMAT_R8G8B8A8_UNORM);
                textures[2].Create(device, DXGI_FORMAT_R32_FLOAT); textures[3].Create(device, DXGI_FORMAT_R32G32_FLOAT);
            }
            if (id <= 3 || id % 20 == 0) std::printf("FRAME real=%u source=%llu waits=%llu sdk=%llu native=%llu lastSDK=%u errors=%u foregroundParent=%d\n",
                id, sourcePresents, sourceWaits, status.sdkReportedPresentations, status.nativePresentCountDelta, status.lastSdkPresented, status.asyncApiErrors, GetForegroundWindow() == parent);
        }
        Need(sourcePresents == 120 && sourceWaits == 120 && status.realPresentCalls == 120 && status.sdkReportedPresentations > 120 &&
            status.nativePresentCountValid && status.nativePresentCountDelta > 120 && status.generatedPresentationObserved && !status.asyncApiErrors && !status.poisoned,
            "Provider control did not independently observe extra SDK and native Presents");
        const auto final = status;
        const auto oldCommon = GetModuleHandleW(L"sl.common.dll");
        Fg(RbaSlFgDestroy(handle, 5000, &status), status, "Destroy after input retirement"); handle = 0;
        if (forceCommonRelocation && preserveParent) {
            // Reproduce address churn from a real engine between Off and On.
            // Never replace a live module: reserve only a now-vacant image range.
            const auto retainedCommon = GetModuleHandleW(L"sl.common.dll");
            if (!retainedCommon) {
                retiredCommonAddress = VirtualAlloc(oldCommon, 0x10000, MEM_RESERVE, PAGE_NOACCESS);
                Need(retiredCommonAddress == oldCommon, "Reserve vacated common-module address");
            } else Need(retainedCommon == oldCommon, "Retained common-module identity changed");
            std::printf("COMMON_RESTART old=%p retained=%p vacantReserved=%p\n", oldCommon, retainedCommon, retiredCommonAddress);
        }
        textures={};list.Reset();allocator.Reset();interfaces={};
        Need(DestroyWindow(child),"Release drained child window");child=nullptr;
        if(!preserveParent) {
            Need(CloseHandle(sourceWaitable) != FALSE, "Close source waitable"); sourceWaitable = nullptr;
            Need(DestroyWindow(parent), "Release drained diagnostic parent");parent=nullptr;
        }
        std::printf("PROVIDER_PASS multiplier=%u real=%llu sdk=%llu native=%llu source=%llu callerInputsReleasedAfterSubmit=1 busyPreserved=1 errors=%u\n",
            multiplier, final.realPresentCalls, final.sdkReportedPresentations, final.nativePresentCountDelta, sourcePresents, final.asyncApiErrors);
    }
};
}
int wmain(int argc, wchar_t** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX); std::setvbuf(stdout, nullptr, _IONBF, 0);
    // Diagnostic errors intentionally retain potentially live resources until
    // process exit. They must not unwind a provider's unproven GPU/window leases.
    try { Need(argc == 3 || argc == 4, "Pass absolute pinned runtime directory, multiplier2/3/4, optional restart"); const auto multiplier = static_cast<uint32_t>(std::stoul(argv[2]));
        const bool relocate = argc == 4 && std::wstring(argv[3]) == L"restart-relocated";
        const bool hiddenFirst = argc == 4 && std::wstring(argv[3]) == L"hidden-first";
        const bool restart = argc == 4 && (std::wstring(argv[3]) == L"restart" || relocate);
        Need(argc == 3 || restart || hiddenFirst, "Unknown GPU test option");
        auto* diagnostic = new Diagnostic;
        diagnostic->forceCommonRelocation = relocate;
        diagnostic->hiddenFirstPresent = hiddenFirst;
        for (unsigned cycle=0; cycle<(restart?2u:1u); ++cycle) {
            const auto count = cycle == 0 ? multiplier : (multiplier == 4 ? 2u : multiplier + 1);
            std::printf("ACTUAL_SESSION_BEGIN cycle=%u multiplier=%u\n",cycle+1,count);
            if(cycle==0)diagnostic->Start(argv[1],count,true);
            else diagnostic->StartNextSession(argv[1],count);
            diagnostic->Run(count,restart && cycle==0);
            std::printf("ACTUAL_SESSION_RETIRED cycle=%u\n",cycle+1);
        }
        delete diagnostic;
        return 0; }
    catch (const std::exception& error) { std::fprintf(stderr, "PROVIDER_FAIL %s\n", error.what()); return 1; }
}
