#include "StreamlineRuntime.h"
#include "StreamlineProviderConstants.h"
#include <sl_reflex.h>
#include <sl_pcl.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <unordered_map>

namespace {
struct Provider;
// NGX retains callbacks in sl.common after shutdown. Reinitialization can call
// unmapped code when that DLL reloads at a different address. Initialize once;
// Off frees the viewport/chain but retains the audited device session.
std::mutex IdleRuntimeMutex;
using ModuleIdentities = std::array<HMODULE, PinnedFiles.size()>;
void ValidateDeviceSessionModules(const std::filesystem::path& directory, const ModuleIdentities& expected) {
    AuditLoadedRuntime(directory);
    for (size_t i = 0; i < expected.size(); ++i)
        Require(GetModuleHandleW(PinnedFiles[i].name) == expected[i], RbaSlFgAlreadyOwned,
            "An initialized NVIDIA module was replaced or unloaded; restart the process");
}
struct DeviceSession {
    RuntimeOwner runtime;
    ComPtr<ID3D12Device> proxyDevice;
    ComPtr<ID3D12CommandQueue> queue, nativeQueue;
    DWORD thread = 0;
    bool reusable = false;
    ModuleIdentities modules{};
    // RuntimeOwner runs slShutdown before the engine device/queues are released.
    ~DeviceSession() {
        if (runtime.value && runtime.value->initAttempted && runtime.value->Stop() != sl::Result::eOk) {
            (void)runtime.value.release(); (void)proxyDevice.Detach(); (void)queue.Detach(); (void)nativeQueue.Detach();
        }
    }
};
// No SDK calls or COM destruction from CRT/DllMain process detach: the vendor
// may need worker threads that Windows has already stopped. The OS reclaims this
// bounded device-session cache at process exit; ordinary Off frees every HWND,
// swapchain, input and feature allocation before transferring it here.
std::unique_ptr<DeviceSession>& IdleRuntime = *new std::unique_ptr<DeviceSession>();
std::atomic<uint32_t> AsyncErrors{0};
void ApiError(const sl::APIError&) { AsyncErrors.fetch_add(1, std::memory_order_relaxed); }
bool StatusAbi(const RbaSlFgStatus* s) { return s && s->structSize == sizeof(*s) && s->abiVersion == Abi; }
void InitStatus(RbaSlFgStatus& s) { s = {}; s.structSize = sizeof(s); s.abiVersion = Abi; }
uint32_t Report(RbaSlFgStatus& s, uint32_t code, const char* message) {
    s.result = code; Text(s.reason, sizeof(s.reason), message); return code;
}
bool SameObject(IUnknown* a, IUnknown* b) {
    ComPtr<IUnknown> aa, bb;
    return a && b && SUCCEEDED(a->QueryInterface(IID_PPV_ARGS(&aa))) && SUCCEEDED(b->QueryInterface(IID_PPV_ARGS(&bb))) && aa == bb;
}
void Barrier(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{}; barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after}; list->ResourceBarrier(1, &barrier);
}

struct Provider {
    RuntimeOwner runtime;
    std::filesystem::path directory;
    DWORD presentationThread = GetCurrentThreadId();
    HWND child = nullptr, parent = nullptr;
    uint32_t width = 0, height = 0;
    uint64_t epoch = 0;
    std::mutex mutex;
    std::condition_variable condition;
    unsigned activeCalls = 0;
    bool stopping = false, destroyed = false, initialized = false, cleanupFailed = false, runtimeLoaded = false, sessionReady = false;
    ModuleIdentities modules{};
    RbaSlFgStatus status{};
    rba_sl::FrameLedger ledger;
    std::array<sl::FrameToken*, 3> tokens{};
    ComPtr<ID3D12Device> proxyDevice;
    ComPtr<ID3D12CommandQueue> queue, nativeQueue;
    ComPtr<IDXGISwapChain3> chain, nativeChain;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> completion, producerFence, sdkFence;
    std::array<ComPtr<ID3D12Resource>, 5> inputs;
    ComPtr<ID3D12Resource> backbuffer;
    File event;
    uint64_t completionValue = 0;
    bool retirementArmed = false;
    uint32_t inputFrame = 0, uiStrategy = RbaSlFgHudlessAndFinal;
    UINT nativeBaseline = 0;
    bool nativeBaselineValid = false;
    sl::ViewportHandle viewport{0};
    sl::DLSSGOptions fgOptions{};
    PFun_slUpgradeInterface* upgrade = nullptr;
    PFun_slGetNativeInterface* native = nullptr;
    PFun_slGetNewFrameToken* newFrame = nullptr;
    PFun_slSetConstants* constants = nullptr;
    PFun_slSetTagForFrame* tag = nullptr;
    PFun_slFreeResources* freeResources = nullptr;
    PFun_slDLSSGGetState* getState = nullptr;
    PFun_slDLSSGSetOptions* options = nullptr;
    PFun_slReflexSleep* sleep = nullptr;
    PFun_slReflexSetOptions* reflexOptions = nullptr;
    PFun_slPCLSetMarker* marker = nullptr;
    Provider() { InitStatus(status); }
    void Sl(sl::Result result, const char* operation) {
        { std::lock_guard<std::mutex> lock(mutex); status.sdkResult = static_cast<uint32_t>(result); }
        if (result != sl::Result::eOk) {
            std::string message = std::string(operation) + ": " + sl::getResultAsStr(result);
            { std::lock_guard<std::mutex> lock(LogMutex); if (!LastSdkError.empty()) message += " | First SDK error this initialization: " + LastSdkError; }
            throw Failure(RbaSlFgSdkError, message);
        }
    }
    template<class T> void Feature(sl::Feature feature, const char* name, const wchar_t* module, T*& function) {
        void* entry = nullptr; Sl(runtime.value->featureFunction(feature, name, entry), name);
        AuditFeatureModule(directory, module, entry); function = reinterpret_cast<T*>(entry);
    }
    void Thread() { Require(GetCurrentThreadId() == presentationThread, RbaSlFgInvalidOrder, "Operation must run on the creating presentation thread"); }
    void Window() const {
        DWORD childProcess = 0, parentProcess = 0;
        GetWindowThreadProcessId(child, &childProcess); GetWindowThreadProcessId(parent, &parentProcess);
        const auto style = static_cast<uintptr_t>(GetWindowLongPtrW(child, GWL_STYLE));
        RECT childRect{}, parentRect{}; POINT origin{};
        Require(IsWindow(child) && IsWindow(parent) && childProcess == GetCurrentProcessId() && parentProcess == childProcess &&
            GetParent(child) == parent && (style & (WS_CHILD | WS_DISABLED)) == (WS_CHILD | WS_DISABLED) && !IsWindowEnabled(child) &&
            GetClientRect(child, &childRect) && GetClientRect(parent, &parentRect) && ClientToScreen(child, &origin) && ScreenToClient(parent, &origin) &&
            origin.x == 0 && origin.y == 0 && childRect.left == 0 && childRect.top == 0 && parentRect.left == 0 && parentRect.top == 0 &&
            childRect.right == static_cast<LONG>(width) && childRect.bottom == static_cast<LONG>(height) &&
            parentRect.right == childRect.right && parentRect.bottom == childRect.bottom,
            RbaSlFgWindowChanged, "The actual disabled child, parent, or full-client extent changed; drain before releasing the host lease");
    }
    void Start(const RbaSlFgCreateDesc& desc) {
        { std::lock_guard<std::mutex> lock(LogMutex); LastSdkError.clear(); }
        child = static_cast<HWND>(desc.childHwnd); parent = GetParent(child); width = desc.width; height = desc.height; epoch = desc.windowEpoch; Window();
        status.multiplier = desc.multiplier; status.windowEpoch = epoch;
        directory = std::filesystem::weakly_canonical(desc.runtimeDirectory);
        ComPtr<IDXGIDevice> source; Check(static_cast<ID3D11Device*>(desc.sourceD3D11Device)->QueryInterface(IID_PPV_ARGS(&source)), "Query source DXGI device");
        ComPtr<IDXGIAdapter> adapter; Check(source->GetAdapter(&adapter), "Get source adapter");
        DXGI_ADAPTER_DESC ad{}; Check(adapter->GetDesc(&ad), "Get source adapter identity");
        { std::lock_guard<std::mutex> lock(IdleRuntimeMutex);
          if (IdleRuntime) {
              Require(IdleRuntime->reusable && IdleRuntime->runtime.value && IdleRuntime->runtime.value->initAttempted,
                  RbaSlFgRuntimeUnavailable, "The NVIDIA device session failed; restart the process before enabling DLSS frame generation");
              Require(std::filesystem::equivalent(directory, IdleRuntime->runtime.value->directoryString), RbaSlFgRuntimeUnavailable,
                  "This process already pinned another provider runtime directory; restart before changing runtime files");
              ValidateDeviceSessionModules(directory, IdleRuntime->modules);
              Require(IdleRuntime->thread == presentationThread, RbaSlFgInvalidOrder,
                  "The initialized NVIDIA session belongs to a different presentation thread; restart the process");
              const auto oldLuid = IdleRuntime->runtime.value->device12->GetAdapterLuid();
              Require(oldLuid.LowPart == ad.AdapterLuid.LowPart && oldLuid.HighPart == ad.AdapterLuid.HighPart &&
                  SUCCEEDED(IdleRuntime->runtime.value->device12->GetDeviceRemovedReason()), RbaSlFgDeviceError,
                  "The initialized NVIDIA device was removed or the adapter changed; restart the process");
              runtime.value = std::move(IdleRuntime->runtime.value); proxyDevice = std::move(IdleRuntime->proxyDevice);
              queue = std::move(IdleRuntime->queue); nativeQueue = std::move(IdleRuntime->nativeQueue);
              modules = IdleRuntime->modules;
              IdleRuntime.reset(); runtimeLoaded = true; sessionReady = true;
          } }
        auto& rt = *runtime.value;
        if (!runtimeLoaded) { rt.Load(directory); runtimeLoaded = true; }
        if (!sessionReady) {
            rt.directoryString = directory.wstring(); rt.unityVersion = "6000.5.8f1"; rt.projectId = "5be2ef0c-dad9-b134-4ae1-03b0d475456b";
            rt.paths[0] = rt.directoryString.c_str(); auto& pref = rt.preferences;
            pref.flags = sl::PreferenceFlags::eUseManualHooking | sl::PreferenceFlags::eUseFrameBasedResourceTagging | sl::PreferenceFlags::eDisableCLStateTracking;
            pref.featuresToLoad = rt.features; pref.numFeaturesToLoad = 2; pref.pathsToPlugins = rt.paths; pref.numPathsToPlugins = 1;
            pref.engine = sl::EngineType::eUnity; pref.engineVersion = rt.unityVersion.c_str(); pref.projectId = rt.projectId.c_str();
            pref.renderAPI = sl::RenderAPI::eD3D12; pref.logMessageCallback = Log;
            const sl::Result start = rt.Start(); AuditLoadedRuntime(directory); Sl(start, "slInit");
        }
        rt.Import(upgrade, "slUpgradeInterface"); rt.Import(native, "slGetNativeInterface"); rt.Import(newFrame, "slGetNewFrameToken");
        rt.Import(constants, "slSetConstants"); rt.Import(tag, "slSetTagForFrame"); rt.Import(freeResources, "slFreeResources");
        sl::AdapterInfo info{}; info.deviceLUID = reinterpret_cast<uint8_t*>(&ad.AdapterLuid); info.deviceLUIDSizeInBytes = sizeof(LUID);
        Require(rt.isSupported(sl::kFeatureDLSS_G, info) == sl::Result::eOk && rt.isSupported(sl::kFeatureReflex, info) == sl::Result::eOk,
            RbaSlFgUnsupported, "DLSS FG and Reflex are not supported on the source adapter");
        if (!sessionReady) Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&rt.device12)), "Create same-adapter D3D12 device");
        const auto luid = rt.device12->GetAdapterLuid(); Require(luid.LowPart == ad.AdapterLuid.LowPart && luid.HighPart == ad.AdapterLuid.HighPart,
            RbaSlFgDeviceError, "Provider/source adapter identity mismatch");
        if (!sessionReady) { const auto deviceResult = rt.setDevice(rt.device12.Get()); AuditLoadedRuntime(directory); Sl(deviceResult, "slSetD3DDevice"); }
        Require(rt.isSupported(sl::kFeatureDLSS_G, info) == sl::Result::eOk && rt.isSupported(sl::kFeatureReflex, info) == sl::Result::eOk,
            RbaSlFgUnsupported, "Initialized DLSS FG/Reflex does not support the source adapter");
        if (!sessionReady) { proxyDevice = rt.device12; Sl(upgrade(reinterpret_cast<void**>(proxyDevice.GetAddressOf())), "Upgrade FG device"); }
        ComPtr<IDXGIFactory2> factory; Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "Create FG factory");
        Sl(upgrade(reinterpret_cast<void**>(factory.GetAddressOf())), "Upgrade FG factory");
        D3D12_COMMAND_QUEUE_DESC qd{}; qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (!sessionReady) {
            Check(proxyDevice->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)), "Create intercepted FG queue");
            Sl(native(queue.Get(), reinterpret_cast<void**>(nativeQueue.GetAddressOf())), "Get native provider queue");
        }
        ComPtr<ID3D12Device> queueDevice; Check(nativeQueue->GetDevice(IID_PPV_ARGS(&queueDevice)), "Get native queue device");
        Require(SameObject(queueDevice.Get(), rt.device12.Get()), RbaSlFgDeviceError, "Native queue/device identity mismatch");
        Feature(sl::kFeatureDLSS_G, "slDLSSGGetState", L"sl.dlss_g.dll", getState);
        Feature(sl::kFeatureDLSS_G, "slDLSSGSetOptions", L"sl.dlss_g.dll", options);
        Feature(sl::kFeatureReflex, "slReflexSleep", L"sl.reflex.dll", sleep);
        Feature(sl::kFeatureReflex, "slReflexSetOptions", L"sl.reflex.dll", reflexOptions);
        Feature(sl::kFeaturePCL, "slPCLSetMarker", L"sl.pcl.dll", marker);
        if (!sessionReady) for (size_t i = 0; i < modules.size(); ++i) {
            modules[i] = GetModuleHandleW(PinnedFiles[i].name);
            Require(modules[i] != nullptr, RbaSlFgRuntimeUnavailable, "Initialized NVIDIA runtime module is missing");
        }
        sessionReady = true;
        sl::DLSSGState state{}; Sl(getState(viewport, state, nullptr), "Query FG multipliers");
        status.maxGeneratedFrames = state.numFramesToGenerateMax; status.sdkRuntimeStatus = static_cast<uint32_t>(state.status);
        for (uint32_t multiplier = 2; multiplier <= 4; ++multiplier) if (multiplier - 1 <= state.numFramesToGenerateMax) status.supportedMultiplierMask |= 1u << multiplier;
        Require((status.supportedMultiplierMask & (1u << desc.multiplier)) != 0, RbaSlFgUnsupported, "Requested multiplier exceeds the actual adapter/runtime capability");
        DXGI_SWAP_CHAIN_DESC1 cd{}; cd.Width = width; cd.Height = height; cd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        cd.SampleDesc.Count = 1; cd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; cd.BufferCount = 3;
        cd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; cd.Scaling = DXGI_SCALING_STRETCH; cd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> base; Check(factory->CreateSwapChainForHwnd(queue.Get(), child, &cd, nullptr, nullptr, &base), "Create intercepted child chain");
        Check(base.As(&chain), "Query child chain3");
        ComPtr<IDXGISwapChain> nativeBase; Sl(native(chain.Get(), reinterpret_cast<void**>(nativeBase.GetAddressOf())), "Get actual native child chain");
        Check(nativeBase.As(&nativeChain), "Query actual native chain3"); HWND actual = nullptr;
        Check(nativeChain->GetHwnd(&actual), "Read actual child-chain HWND"); Require(actual == child, RbaSlFgWindowChanged, "Streamline native chain HWND does not match the leased child");
        nativeBaselineValid = SUCCEEDED(nativeChain->GetLastPresentCount(&nativeBaseline));
        Check(rt.device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create provider allocator");
        Check(rt.device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)), "Create provider command list");
        Check(list->Close(), "Close initial provider list"); Check(rt.device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&completion)), "Create provider retirement fence");
        event.handle = CreateEventW(nullptr, FALSE, FALSE, nullptr); Require(event.handle != nullptr, RbaSlFgDeviceError, "Create provider completion event failed");
        sl::ReflexOptions ro{}; ro.mode = sl::ReflexMode::eLowLatency; ro.frameLimitUs = desc.reflexFrameLimitUs; Sl(reflexOptions(ro), "Enable host-driven Reflex");
        fgOptions.mode = sl::DLSSGMode::eOn; fgOptions.numFramesToGenerate = desc.multiplier - 1; fgOptions.onErrorCallback = ApiError;
        AsyncErrors.store(0); Sl(options(viewport, fgOptions), "Enable requested fixed FG multiplier"); AuditLoadedRuntime(directory);
        initialized = true; Report(status, RbaSlFgOk, "Provider initialized; generated presentation and UI quality have not yet been observed");
    }
    void Snapshot(RbaSlFgStatus& out) {
        std::lock_guard<std::mutex> lock(mutex); status.asyncApiErrors = AsyncErrors.load();
        if (status.asyncApiErrors && !destroyed) { status.poisoned = 1;
            if (status.result == RbaSlFgOk) Report(status, RbaSlFgSdkError, "An asynchronous NVIDIA API error requires drained provider destruction"); }
        out = status;
    }
    uint32_t Fail(uint32_t code, const char* message, bool poison) {
        std::lock_guard<std::mutex> lock(mutex); if (poison) status.poisoned = 1;
        return Report(status, code, message);
    }
    uint32_t InitializationFailure(uint32_t code, const char* message) {
        return Fail(code, message, code == RbaSlFgSdkError || code == RbaSlFgDeviceError ||
            code == RbaSlFgRuntimeUnavailable || code == RbaSlFgPoisoned);
    }
    bool BeginCall(bool allowStopping = false) {
        std::lock_guard<std::mutex> lock(mutex);
        if (destroyed || (stopping && !allowStopping)) return false; ++activeCalls; return true;
    }
    void EndCall() { std::lock_guard<std::mutex> lock(mutex); --activeCalls; condition.notify_all(); }
    void Healthy() {
        std::lock_guard<std::mutex> lock(mutex);
        if (AsyncErrors.load()) status.poisoned = 1;
        Require(initialized && !status.poisoned && !AsyncErrors.load(), RbaSlFgPoisoned, "Provider is uninitialized or has a terminal SDK/device error; retain leases and destroy after retirement");
    }
    bool Drain(uint32_t timeout) {
        if (!inputFrame) return true;
        Require(retirementArmed, RbaSlFgPoisoned, "Input completion could not be armed; retain the provider and input/window leases until process exit");
        const auto deadline = GetTickCount64() + timeout;
        for (;;) {
            const auto value = completion->GetCompletedValue();
            Require(value != UINT64_MAX, RbaSlFgDeviceError, "Provider device was removed; input retirement is unproven");
            if (value >= completionValue) break;
            if (!timeout || GetTickCount64() >= deadline) return false;
            Require(ResetEvent(event.handle) != FALSE, RbaSlFgDeviceError, "Reset completion event failed");
            Check(completion->SetEventOnCompletion(completionValue, event.handle), "Arm completion event");
            const auto now = GetTickCount64();
            const auto remaining = deadline > now ? deadline - now : 0;
            const DWORD wait = WaitForSingleObject(event.handle, static_cast<DWORD>(remaining));
            Require(wait == WAIT_OBJECT_0 || wait == WAIT_TIMEOUT, RbaSlFgDeviceError, "Wait for input completion failed");
            // Event wakes can belong to an earlier arm. Only the actual fence
            // value proves retirement; a timeout never releases input objects.
        }
        { std::lock_guard<std::mutex> lock(mutex);
          const int slot = ledger.Find(inputFrame); if (slot >= 0) { ledger.frames[slot] = {}; tokens[slot] = nullptr; }
          status.lastRetiredFrameId = inputFrame; status.inputBatchPending = 0; }
        inputs = {}; producerFence.Reset(); sdkFence.Reset(); backbuffer.Reset(); inputFrame = 0; retirementArmed = false;
        return true;
    }
    void ValidateResources(const RbaSlFgFrame& frame, std::array<ComPtr<ID3D12Resource>, 5>& retained, ComPtr<ID3D12Fence>& ready) {
        Require(frame.structSize == sizeof(frame) && frame.abiVersion == Abi, RbaSlFgAbiMismatch, "Frame does not match provider ABI 1");
        Require(frame.windowEpoch == epoch && frame.realFrameId != 0, RbaSlFgWindowChanged, "Frame/window lease identity mismatch");
        Require(rba_sl::CameraValid(frame.camera), RbaSlFgInvalidArgument, "Frame camera matrices, planes, conventions, units or real elapsed time are invalid");
        Require(frame.uiStrategy <= RbaSlFgHudlessAndRealUiAlpha &&
            (frame.realUiAlpha != nullptr) == (frame.uiStrategy == RbaSlFgHudlessAndRealUiAlpha), RbaSlFgInvalidArgument, "UI strategy must match an actual separate alpha texture");
        const void* raw[] = {frame.finalColor, frame.hudlessColor, frame.rawDepth, frame.normalizedMotion, frame.realUiAlpha};
        D3D12_RESOURCE_DESC descriptions[5]{};
        for (size_t i = 0; i < retained.size(); ++i) {
            if (i == 4 && !raw[i]) continue;
            Require(raw[i] != nullptr, RbaSlFgInvalidArgument, "A required frame texture is missing");
            Require(SUCCEEDED(static_cast<IUnknown*>(const_cast<void*>(raw[i]))->QueryInterface(IID_PPV_ARGS(&retained[i]))),
                RbaSlFgInvalidArgument, "Frame input is not a D3D12 resource");
            for (size_t j = 0; j < i; ++j) Require(!SameObject(retained[i].Get(), retained[j].Get()), RbaSlFgInvalidArgument, "Frame textures must not alias the same resource");
            ComPtr<ID3D12Device> device; Check(retained[i]->GetDevice(IID_PPV_ARGS(&device)), "Query frame texture device");
            Require(SameObject(device.Get(), runtime.value->device12.Get()), RbaSlFgInvalidArgument, "Frame texture belongs to a different D3D12 device");
            descriptions[i] = retained[i]->GetDesc(); const auto& d = descriptions[i];
            Require(d.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D && d.Width && d.Height && d.MipLevels == 1 && d.DepthOrArraySize == 1 &&
                d.SampleDesc.Count == 1 && !(d.Flags & D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE), RbaSlFgInvalidArgument, "Frame textures must be single-sampled shader-readable 2D resources");
            if (i < 2 || i == 4) Require(d.Width == width && d.Height == height && d.Format == (i == 4 ? DXGI_FORMAT_R8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM),
                RbaSlFgInvalidArgument, "Final/HUD-less RGBA8 and real R8 alpha must match the leased display extent");
        }
        Require(descriptions[2].Format == DXGI_FORMAT_R32_FLOAT &&
            (descriptions[3].Format == DXGI_FORMAT_R16G16_FLOAT || descriptions[3].Format == DXGI_FORMAT_R32G32_FLOAT) &&
            descriptions[2].Width == descriptions[3].Width && descriptions[2].Height == descriptions[3].Height &&
            descriptions[2].Width <= width && descriptions[2].Height <= height,
            RbaSlFgInvalidArgument, "Raw R32 depth and normalized float2 motion must have matching render extents no larger than the display");
        Require(frame.inputReadyFence || frame.inputReadyValue == 0, RbaSlFgInvalidArgument, "A producer value requires an actual GPU fence");
        if (frame.inputReadyFence) {
            Require(SUCCEEDED(static_cast<IUnknown*>(frame.inputReadyFence)->QueryInterface(IID_PPV_ARGS(&ready))),
                RbaSlFgInvalidArgument, "Producer completion object is not a D3D12 fence"); ComPtr<ID3D12Device> device;
            Check(ready->GetDevice(IID_PPV_ARGS(&device)), "Query producer fence device");
            Require(SameObject(device.Get(), runtime.value->device12.Get()) && frame.inputReadyValue != UINT64_MAX,
                RbaSlFgInvalidArgument, "Producer fence must belong to the provider device and use a valid completion value");
        }
    }
    void Submit(const RbaSlFgFrame& frame) {
        Thread(); Healthy();
        // Backpressure cannot inspect, overwrite or consume either frame's leases.
        Require(!inputFrame, RbaSlFgBusy, "Previous input batch is still retained; Poll before submitting another frame");
        Window();
        std::array<ComPtr<ID3D12Resource>, 5> retained; ComPtr<ID3D12Fence> ready;
        ValidateResources(frame, retained, ready);
        int slot = -1; sl::FrameToken* token = nullptr;
        { std::lock_guard<std::mutex> lock(mutex); const auto result = ledger.Enter(frame.realFrameId, rba_sl::FramePhase::RenderEnded, slot);
          Require(result == RbaSlFgOk, result, "Submit requires complete actual simulation/render markers for this real frame"); token = tokens[slot]; }
        bool enqueued = false;
        try {
            const bool strategyChanged = uiStrategy != frame.uiStrategy;
            if (strategyChanged) { fgOptions.enableUserInterfaceRecomposition = frame.uiStrategy == RbaSlFgHudlessAndRealUiAlpha ? sl::eTrue : sl::eFalse;
                Sl(options(viewport, fgOptions), "Set actual UI input strategy"); }
            uint32_t previousFrame = 0; { std::lock_guard<std::mutex> lock(mutex); previousFrame = status.lastSubmittedFrameId; }
            const auto c = rba_sl::MakeConstants(frame.camera, !previousFrame || frame.realFrameId != previousFrame + 1 || strategyChanged);
            Sl(constants(c, *token, viewport), "Set real frame constants");
            Check(allocator->Reset(), "Reset provider allocator"); Check(list->Reset(allocator.Get(), nullptr), "Reset provider command list");
            ComPtr<ID3D12Resource> back; Check(chain->GetBuffer(chain->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&back)), "Get current child backbuffer");
            Barrier(list.Get(), retained[0].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
            Barrier(list.Get(), back.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST);
            list->CopyResource(back.Get(), retained[0].Get());
            Barrier(list.Get(), back.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT);
            Barrier(list.Get(), retained[0].Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);
            const sl::Extent display{0, 0, width, height}; const auto depth = retained[2]->GetDesc();
            const sl::Extent render{0, 0, static_cast<uint32_t>(depth.Width), depth.Height};
            sl::Resource hud(sl::ResourceType::eTex2d, retained[1].Get(), D3D12_RESOURCE_STATE_COMMON);
            sl::Resource z(sl::ResourceType::eTex2d, retained[2].Get(), D3D12_RESOURCE_STATE_COMMON);
            sl::Resource motion(sl::ResourceType::eTex2d, retained[3].Get(), D3D12_RESOURCE_STATE_COMMON);
            sl::Resource alpha(sl::ResourceType::eTex2d, retained[4].Get(), D3D12_RESOURCE_STATE_COMMON);
            sl::ResourceTag tags[] = {{&hud, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent, &display},
                {&z, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, &render},
                {&motion, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, &render},
                {nullptr, sl::kBufferTypeBackbuffer, sl::ResourceLifecycle::eValidUntilPresent, &display},
                {nullptr, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eValidUntilPresent, &display},
                {retained[4] ? &alpha : nullptr, sl::kBufferTypeUIAlpha, sl::ResourceLifecycle::eValidUntilPresent, &display}};
            // Tagging publishes pointers to the SDK as well as recording list
            // work. Retain before that call, including partial failure, rather
            // than assuming an exception before Execute leaves no SDK reader.
            inputs = std::move(retained); producerFence = std::move(ready); backbuffer = std::move(back); inputFrame = frame.realFrameId;
            { std::lock_guard<std::mutex> lock(mutex); status.inputBatchPending = 1; ledger.Complete(slot, rba_sl::FramePhase::Submitted); }
            enqueued = true;
            Sl(tag(*token, viewport, tags, static_cast<uint32_t>(std::size(tags)), list.Get()), "Tag actual retained frame resources");
            Check(list->Close(), "Close real-frame child copy");
            if (producerFence) Check(queue->Wait(producerFence.Get(), frame.inputReadyValue), "Wait for truthful producer completion");
            ID3D12CommandList* commands[] = {list.Get()}; queue->ExecuteCommandLists(1, commands);
            Sl(marker(sl::PCLMarker::ePresentStart, *token), "Mark actual child Present start");
            const HRESULT presented = chain->Present(0, 0);
            { std::lock_guard<std::mutex> lock(mutex); ++status.realPresentCalls; status.lastPresentHresult = static_cast<uint32_t>(presented); status.lastSubmittedFrameId = frame.realFrameId; }
            Sl(marker(sl::PCLMarker::ePresentEnd, *token), "Mark actual child Present end"); Check(presented, "Present actual FG child");
            sl::DLSSGState state{}; Sl(getState(viewport, state, nullptr), "Read actual presentation/input-completion state");
            { std::lock_guard<std::mutex> lock(mutex); status.sdkRuntimeStatus = static_cast<uint32_t>(state.status);
              status.lastSdkPresented = state.numFramesActuallyPresented; status.sdkReportedPresentations += state.numFramesActuallyPresented; }
            Require(state.inputsProcessingCompletionFence != nullptr, RbaSlFgSdkError, "SDK did not report an input-processing completion fence; input retirement remains unproven");
            sdkFence = static_cast<ID3D12Fence*>(state.inputsProcessingCompletionFence);
            Check(queue->Wait(sdkFence.Get(), state.lastPresentInputsProcessingCompletionFenceValue), "Wait for every SDK input reader");
            Require(completionValue < UINT64_MAX - 1, RbaSlFgPoisoned, "Provider completion sequence exhausted");
            ++completionValue; Check(queue->Signal(completion.Get(), completionValue), "Signal provider and SDK input retirement"); retirementArmed = true;
            UINT count = 0; const bool countValid = nativeBaselineValid && SUCCEEDED(nativeChain->GetLastPresentCount(&count));
            { std::lock_guard<std::mutex> lock(mutex); status.nativePresentCountValid = countValid;
              if (countValid) status.nativePresentCountDelta = static_cast<uint32_t>(count - nativeBaseline);
              if (countValid && status.nativePresentCountDelta > status.realPresentCalls && status.sdkReportedPresentations > status.realPresentCalls)
                  status.generatedPresentationObserved = 1; }
            uiStrategy = frame.uiStrategy;
            Require(state.status == sl::DLSSGStatus::eOk && !AsyncErrors.load(), RbaSlFgSdkError, "SDK reports a frame-generation runtime or asynchronous API error");
            Fail(RbaSlFgOk, "Real child Present submitted; input lease remains held until Poll proves SDK and queue completion", false);
        } catch (...) {
            { std::lock_guard<std::mutex> lock(mutex); if (enqueued) status.poisoned = 1; else ledger.Complete(slot, rba_sl::FramePhase::RenderEnded); }
            throw;
        }
    }
    bool Cleanup(uint32_t timeout) {
        Thread();
        if (cleanupFailed) throw Failure(RbaSlFgPoisoned, "SDK cleanup previously failed; preserve this quarantined handle and HWND until process exit");
        if (!Drain(timeout)) return false;
        if (initialized && options) {
            fgOptions.mode = sl::DLSSGMode::eOff;
            const auto result = options(viewport, fgOptions);
            if (result != sl::Result::eOk) cleanupFailed = true;
            Sl(result, "Disable DLSS FG before teardown");
        }
        const bool reusable = runtimeLoaded && sessionReady && !status.poisoned && !AsyncErrors.load();
        // Documented long-term Off: free this viewport's feature memory, then
        // release its swapchain. Preserve the initialized SDK/device (no HWNDs
        // or inputs) so its NGX callback owner is never unloaded/reinitialized.
        if (reusable && freeResources) {
            const auto result = freeResources(sl::kFeatureDLSS_G, viewport);
            if (result != sl::Result::eOk) cleanupFailed = true;
            Sl(result, "Free disabled DLSS FG viewport resources");
        }
        if (!reusable && runtime.value && runtime.value->initAttempted) {
            const auto result = runtime.value->Stop();
            if (result != sl::Result::eOk) { cleanupFailed = true; Sl(result, "slShutdown (session quarantined)"); }
        }
        list.Reset(); allocator.Reset(); chain.Reset(); nativeChain.Reset(); completion.Reset();
        if (runtimeLoaded && runtime.value) {
            auto session = std::make_unique<DeviceSession>();
            session->thread = presentationThread; session->reusable = reusable; session->modules = modules;
            std::lock_guard<std::mutex> lock(IdleRuntimeMutex);
            Require(!IdleRuntime, RbaSlFgPoisoned, "Unexpected second idle SDK owner; preserve the provider lease");
            session->runtime.value = std::move(runtime.value);
            session->queue = std::move(queue); session->nativeQueue = std::move(nativeQueue); session->proxyDevice = std::move(proxyDevice);
            IdleRuntime = std::move(session);
        } else runtime.value.reset();
        initialized = false;
        { std::lock_guard<std::mutex> lock(mutex); destroyed = true; ledger = {}; tokens = {}; }
        return true;
    }
};

std::mutex RegistryMutex;
// Active/quarantined handles need the same process-detach rule as the idle
// session. Successful Destroy erases normally on its presentation thread.
std::unordered_map<uint64_t, std::shared_ptr<Provider>>& Registry =
    *new std::unordered_map<uint64_t, std::shared_ptr<Provider>>();
uint64_t NextHandle = 1;
std::shared_ptr<Provider> Lookup(uint64_t handle) {
    std::lock_guard<std::mutex> lock(RegistryMutex); const auto found = Registry.find(handle); return found == Registry.end() ? nullptr : found->second;
}
struct Call {
    std::shared_ptr<Provider> provider;
    explicit Call(uint64_t handle, bool allowStopping = false) : provider(Lookup(handle)) {
        if (provider && !provider->BeginCall(allowStopping)) provider.reset();
    }
    ~Call() { if (provider) provider->EndCall(); }
};
template<class F> uint32_t WithStatus(uint64_t handle, RbaSlFgStatus* output, F&& operation) {
    if (!StatusAbi(output)) return RbaSlFgAbiMismatch;
    Call call(handle, true);
    if (!call.provider) { InitStatus(*output); return Report(*output, RbaSlFgInvalidHandle, "Provider handle is not live"); }
    auto& p = *call.provider;
    try { operation(p); }
    catch (const Failure& f) { p.Fail(f.code, f.what(), false); }
    catch (const std::exception& e) { p.Fail(RbaSlFgSdkError, e.what(), p.inputFrame != 0); }
    catch (...) { p.Fail(RbaSlFgSdkError, "Unexpected provider failure", p.inputFrame != 0); }
    p.Snapshot(*output); return output->result;
}
uint32_t Marker(uint64_t handle, uint32_t id, rba_sl::FramePhase expected, rba_sl::FramePhase next, sl::PCLMarker value, bool first) {
    Call call(handle); if (!call.provider) return RbaSlFgInvalidHandle;
    auto& p = *call.provider; int slot = -1;
    try {
        p.Healthy();
        { std::lock_guard<std::mutex> lock(p.mutex); const auto result = first ? p.ledger.Reserve(id, slot) : p.ledger.Enter(id, expected, slot);
          if (result != RbaSlFgOk) return result; }
        if (first) { p.Sl(p.newFrame(p.tokens[slot], &id), "Create real-frame token"); p.Sl(p.sleep(*p.tokens[slot]), "Sleep before actual simulation"); }
        p.Sl(p.marker(value, *p.tokens[slot]), "Set actual engine marker");
        { std::lock_guard<std::mutex> lock(p.mutex); p.ledger.Complete(slot, next); }
        return RbaSlFgOk;
    } catch (const Failure& f) {
        if (slot >= 0) { std::lock_guard<std::mutex> lock(p.mutex); p.ledger.frames[slot].callPending = false; }
        return p.Fail(f.code, f.what(), slot >= 0);
    } catch (...) { return p.Fail(RbaSlFgSdkError, "Unexpected engine marker failure", true); }
}
}

static_assert(sizeof(RbaSlFgCreateDesc) == 56 && sizeof(RbaSlFgInterfaces) == 24 && sizeof(RbaSlFgCamera) == 304 &&
    sizeof(RbaSlFgFrame) == 384 && sizeof(RbaSlFgStatus) == 616, "Provider ABI layout changed");

RBA_SL_FG_API uint32_t RbaSlFgCreate(const RbaSlFgCreateDesc* desc, uint64_t* handle, RbaSlFgStatus* output) {
    if (!StatusAbi(output)) return RbaSlFgAbiMismatch; InitStatus(*output);
    if (!handle) return Report(*output, RbaSlFgInvalidArgument, "A handle output is required"); *handle = 0;
    std::shared_ptr<Provider> p;
    uint64_t reservedHandle = 0;
    try {
        Require(desc && desc->structSize == sizeof(*desc) && desc->abiVersion == Abi, RbaSlFgAbiMismatch, "Create description does not match provider ABI 1");
        Require(desc->runtimeDirectory && *desc->runtimeDirectory && std::filesystem::path(desc->runtimeDirectory).is_absolute() &&
            desc->sourceD3D11Device && desc->childHwnd && desc->windowEpoch && desc->width && desc->height && desc->width <= 16384 && desc->height <= 16384 &&
            desc->multiplier >= 2 && desc->multiplier <= 4, RbaSlFgInvalidArgument, "Create requires an absolute runtime, source device, child lease, valid extent and fixed multiplier 2..4");
        p = std::make_shared<Provider>();
        // Allocate the registry node before entering the SDK. An allocation
        // failure must not unwind a newly live GPU session without a handle.
        { std::lock_guard<std::mutex> lock(RegistryMutex); reservedHandle = NextHandle++;
          Require(reservedHandle != 0, RbaSlFgRuntimeUnavailable, "Provider handle sequence exhausted"); Registry.emplace(reservedHandle, p); }
        p->Start(*desc);
    } catch (const Failure& f) { if (p) p->InitializationFailure(f.code, f.what()); else Report(*output, f.code, f.what()); }
      catch (const std::exception& e) { if (p) p->InitializationFailure(RbaSlFgSdkError, e.what()); else Report(*output, RbaSlFgSdkError, e.what()); }
      catch (...) { if (p) p->InitializationFailure(RbaSlFgSdkError, "Unexpected provider initialization failure"); else Report(*output, RbaSlFgSdkError, "Unexpected provider initialization failure"); }
    if (p) {
        p->Snapshot(*output);
        if (output->result != RbaSlFgOk) {
            try { if (p->Cleanup(0)) { std::lock_guard<std::mutex> lock(RegistryMutex); Registry.erase(reservedHandle); return output->result; } }
            catch (...) { p->Fail(output->result, "Initialization failed and cleanup is uncertain; preserve the returned handle and child HWND lease until Destroy succeeds", true); p->Snapshot(*output); }
        }
        *handle = reservedHandle;
    }
    return output->result;
}
RBA_SL_FG_API uint32_t RbaSlFgGetInterfaces(uint64_t handle, RbaSlFgInterfaces* out) {
    if (!out || out->structSize != sizeof(*out) || out->abiVersion != Abi) return RbaSlFgAbiMismatch;
    out->device12 = nullptr; out->queue12 = nullptr; Call call(handle); if (!call.provider) return RbaSlFgInvalidHandle;
    auto& p = *call.provider; std::lock_guard<std::mutex> lock(p.mutex);
    if (!p.initialized || p.status.poisoned) return RbaSlFgPoisoned;
    out->device12 = p.runtime.value->device12.Get(); out->queue12 = p.nativeQueue.Get(); return RbaSlFgOk;
}
RBA_SL_FG_API uint32_t RbaSlFgGetStatus(uint64_t handle, RbaSlFgStatus* out) {
    if (!StatusAbi(out)) return RbaSlFgAbiMismatch; Call call(handle, true);
    if (!call.provider) { InitStatus(*out); return Report(*out, RbaSlFgInvalidHandle, "Provider handle is not live"); }
    call.provider->Snapshot(*out); return out->result;
}
RBA_SL_FG_API uint32_t RbaSlFgBeginSimulation(uint64_t h, uint32_t f) { return Marker(h, f, rba_sl::FramePhase::Empty, rba_sl::FramePhase::Simulation, sl::PCLMarker::eSimulationStart, true); }
RBA_SL_FG_API uint32_t RbaSlFgEndSimulation(uint64_t h, uint32_t f) { return Marker(h, f, rba_sl::FramePhase::Simulation, rba_sl::FramePhase::SimulationEnded, sl::PCLMarker::eSimulationEnd, false); }
RBA_SL_FG_API uint32_t RbaSlFgBeginRender(uint64_t h, uint32_t f) { return Marker(h, f, rba_sl::FramePhase::SimulationEnded, rba_sl::FramePhase::Render, sl::PCLMarker::eRenderSubmitStart, false); }
RBA_SL_FG_API uint32_t RbaSlFgEndRender(uint64_t h, uint32_t f) { return Marker(h, f, rba_sl::FramePhase::Render, rba_sl::FramePhase::RenderEnded, sl::PCLMarker::eRenderSubmitEnd, false); }
RBA_SL_FG_API uint32_t RbaSlFgDiscardFrame(uint64_t handle, uint32_t frame) {
    Call call(handle); if (!call.provider) return RbaSlFgInvalidHandle;
    auto& p = *call.provider; std::lock_guard<std::mutex> lock(p.mutex); const int slot = p.ledger.Find(frame);
    const auto result = p.ledger.Discard(frame); if (result == RbaSlFgOk) p.tokens[slot] = nullptr; return result;
}
RBA_SL_FG_API uint32_t RbaSlFgSubmit(uint64_t handle, const RbaSlFgFrame* frame, RbaSlFgStatus* out) {
    return WithStatus(handle, out, [&](Provider& p) { Require(frame != nullptr, RbaSlFgInvalidArgument, "A real frame is required");
        { std::lock_guard<std::mutex> lock(p.mutex); Require(!p.stopping, RbaSlFgInvalidOrder, "Provider is stopping"); } p.Submit(*frame); });
}
RBA_SL_FG_API uint32_t RbaSlFgPoll(uint64_t handle, uint32_t timeout, RbaSlFgStatus* out) {
    return WithStatus(handle, out, [&](Provider& p) { p.Thread(); Require(timeout <= 5000, RbaSlFgInvalidArgument, "Poll timeout must be 0..5000ms");
        if (!p.Drain(timeout)) p.Fail(RbaSlFgBusy, "GPU inputs remain retained; retry Poll without overwriting their pixels", false);
        else { std::lock_guard<std::mutex> lock(p.mutex); if (!p.status.poisoned) Report(p.status, RbaSlFgOk, "Every SDK input reader and provider queue operation has retired");
            else Report(p.status, RbaSlFgPoisoned, "Inputs retired, but the terminal provider error requires destruction"); } });
}
RBA_SL_FG_API uint32_t RbaSlFgDestroy(uint64_t handle, uint32_t timeout, RbaSlFgStatus* out) {
    if (!StatusAbi(out)) return RbaSlFgAbiMismatch;
    auto p = Lookup(handle); if (!p) { InitStatus(*out); return Report(*out, RbaSlFgInvalidHandle, "Provider handle is not live"); }
    try {
        p->Thread(); Require(timeout <= 5000, RbaSlFgInvalidArgument, "Destroy timeout must be 0..5000ms");
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout);
        { std::unique_lock<std::mutex> lock(p->mutex); p->stopping = true;
          Require(p->condition.wait_until(lock, deadline, [&] { return p->activeCalls == 0; }), RbaSlFgBusy, "A real marker/API call is still active; HWND and provider remain retained"); }
        const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
        Require(p->Cleanup(static_cast<uint32_t>(std::max<int64_t>(0, remaining))), RbaSlFgBusy, "GPU inputs remain retained; retry Destroy");
        p->Fail(RbaSlFgOk, "Viewport and input retirement completed; the host may release its HWND lease", false);
        { std::lock_guard<std::mutex> lock(RegistryMutex); Registry.erase(handle); }
    } catch (const Failure& f) { p->Fail(f.code, f.what(), f.code == RbaSlFgDeviceError || f.code == RbaSlFgSdkError || f.code == RbaSlFgPoisoned); }
      catch (const std::exception& e) { p->Fail(RbaSlFgSdkError, e.what(), true); }
      catch (...) { p->Fail(RbaSlFgSdkError, "Unexpected teardown failure; provider remains retained", true); }
    p->Snapshot(*out); return out->result;
}
