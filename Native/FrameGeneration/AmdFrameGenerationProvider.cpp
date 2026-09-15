#include "AmdFrameGenerationProvider.h"
#include "AmdFrameGenerationProviderCamera.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <ffx_api_loader.h>
#include <dx12/ffx_api_dx12.h>
#include <ffx_framegeneration.h>
#include <dx12/ffx_api_framegeneration_dx12.h>
#include <condition_variable>
#include <thread>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
constexpr uint32_t Abi = 1;
constexpr const wchar_t* RuntimeName = L"amd_fidelityfx_framegeneration_dx12.dll";
constexpr const char* RuntimeHash = "02297beedd285e822d3a64f314cf00faf378dcec0edc47ff0c4dd71b3a8c2f18";
struct Failure : std::runtime_error {
    uint32_t code;
    Failure(uint32_t c, const char* message) : std::runtime_error(message), code(c) {}
};
void Require(bool condition, uint32_t code, const char* message) { if (!condition) throw Failure(code, message); }
void Check(HRESULT result, const char* message) { Require(SUCCEEDED(result), RbaAmdProviderDeviceError, message); }
void CheckFfx(ffxReturnCode_t result, const char* operation) {
    if (result == FFX_API_RETURN_OK) return;
    char message[256]{};
    std::snprintf(message, sizeof(message), "%s failed (FSR API %u)", operation, result);
    throw Failure(RbaAmdProviderSdkError, message);
}
void CopyText(char* destination, size_t capacity, const char* source) {
    std::snprintf(destination, capacity, "%s", source ? source : "");
}
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct Runtime {
    HMODULE module = nullptr;
    Handle file;
    ffxFunctions api{};
    ~Runtime() { if (module) FreeLibrary(module); }
    explicit Runtime(const wchar_t* directory) {
        Require(directory && *directory, RbaAmdProviderInvalidArgument, "AMD FG runtime directory is required");
        const std::filesystem::path dir(directory);
        Require(dir.is_absolute(), RbaAmdProviderInvalidArgument, "AMD FG runtime directory must be absolute");
        const auto path = std::filesystem::weakly_canonical(dir / RuntimeName);
        // Hold the verified DLL against replacement for its entire loaded lifetime.
        file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(file.value != INVALID_HANDLE_VALUE, RbaAmdProviderRuntimeUnavailable, "Pinned AMD FG DLL is missing or locked");
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0,
            RbaAmdProviderRuntimeUnavailable, "Could not initialize runtime SHA256");
        try {
            Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0,
                RbaAmdProviderRuntimeUnavailable, "Could not create runtime SHA256");
            std::array<unsigned char, 65536> buffer{};
            DWORD count = 0;
            do {
                Require(ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()), &count, nullptr) != FALSE,
                    RbaAmdProviderRuntimeUnavailable, "Could not read AMD FG DLL");
                if (count) Require(BCryptHashData(hash, buffer.data(), count, 0) >= 0,
                    RbaAmdProviderRuntimeUnavailable, "Could not hash AMD FG DLL");
            } while (count);
            std::array<unsigned char, 32> digest{};
            Require(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0,
                RbaAmdProviderRuntimeUnavailable, "Could not finish runtime SHA256");
            char hex[65]{};
            for (size_t i = 0; i < digest.size(); ++i) std::snprintf(hex + i * 2, 3, "%02x", digest[i]);
            Require(std::strcmp(hex, RuntimeHash) == 0, RbaAmdProviderRuntimeUnavailable,
                "AMD FG SHA256 does not match pinned SDK 2.3.0");
        } catch (...) {
            if (hash) BCryptDestroyHash(hash);
            BCryptCloseAlgorithmProvider(algorithm, 0);
            throw;
        }
        BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
        if (HMODULE loaded = GetModuleHandleW(RuntimeName)) {
            std::array<wchar_t, 32768> loadedPath{};
            const DWORD size = GetModuleFileNameW(loaded, loadedPath.data(), static_cast<DWORD>(loadedPath.size()));
            Require(size && size < loadedPath.size() && std::filesystem::equivalent(path, loadedPath.data()),
                RbaAmdProviderRuntimeUnavailable, "Different AMD FG DLL is already loaded");
        }
        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(module != nullptr, RbaAmdProviderRuntimeUnavailable, "Pinned AMD FG DLL could not be loaded");
        ffxLoadFunctions(&api, module);
        if (!api.CreateContext || !api.DestroyContext || !api.Query || !api.Dispatch || !api.Configure) {
            FreeLibrary(module); module = nullptr;
            throw Failure(RbaAmdProviderRuntimeUnavailable, "Pinned AMD FG DLL lacks the required FSR API exports");
        }
    }
};
void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* texture, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition = {texture, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
    list->ResourceBarrier(1, &barrier);
}
bool SameObject(IUnknown* a, IUnknown* b) {
    ComPtr<IUnknown> x, y;
    return a && b && SUCCEEDED(a->QueryInterface(IID_PPV_ARGS(&x))) &&
        SUCCEEDED(b->QueryInterface(IID_PPV_ARGS(&y))) && x.Get() == y.Get();
}
RbaAmdProviderStatus ProviderStatus() {
    RbaAmdProviderStatus s{}; s.structSize = sizeof(s); s.abiVersion = Abi; return s;
}
struct Provider {
    std::mutex mutex;
    std::condition_variable changed;
    std::thread worker;
    RbaAmdProviderCreateDesc desc{};
    std::wstring directory;
    HWND child = nullptr, parent = nullptr;
    RbaAmdProviderStatus status = ProviderStatus();
    bool job = false, stop = false, ended = false, cleanupFailed = false;
    bool shaderStatesSubmitted = false, restorationSubmitted = false;
    uint32_t lastProcessed = 0;
    UINT nativeBaseline = 0;
    bool nativeBaselineValid = false;
    ComPtr<ID3D11Device> source;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Device> device;
    ComPtr<IDXGIFactory2> factory;
    ComPtr<IDXGISwapChain4> chain;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> completion, producerFence;
    ComPtr<ID3D12Resource> backbuffer;
    std::array<ComPtr<ID3D12Resource>, 4> inputs;
    RbaAmdProviderFrame frame{};
    Handle completeEvent;
    uint64_t completionValue = 0;
    std::unique_ptr<Runtime> runtime;
    ffxContext effect = nullptr, swapchain = nullptr;
    // Descriptor chains remain alive until their SDK contexts are destroyed.
    ffxCreateContextDescFrameGeneration create{};
    ffxCreateContextDescFrameGenerationVersion version{};
    ffxCreateContextDescFrameGenerationHudless hudlessCreate{};
    ffxCreateBackendDX12Desc backend{};
    ffxOverrideVersion overrideVersion{};
    ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 swapCreate{};
    ffxCreateContextDescFrameGenerationSwapChainVersionDX12 swapVersion{};
    DXGI_SWAP_CHAIN_DESC1 chainDesc{};
    IDXGISwapChain4* createdChain = nullptr;

    explicit Provider(const RbaAmdProviderCreateDesc& d) : desc(d), directory(d.runtimeDirectory),
        child(static_cast<HWND>(d.childHwnd)), parent(GetParent(child)), source(static_cast<ID3D11Device*>(d.sourceD3D11Device)) {
        desc.runtimeDirectory = nullptr; desc.sourceD3D11Device = nullptr; desc.optionalQueue12 = nullptr;
        if (d.optionalQueue12) queue = static_cast<ID3D12CommandQueue*>(d.optionalQueue12);
        status.windowEpoch = d.windowEpoch; status.result = RbaAmdProviderBusy;
        CopyText(status.reason, sizeof(status.reason), "AMD paced provider is initializing on its worker");
        Window();
        // A blocked SDK worker or callback must never run out of an unloaded DLL.
        HMODULE pinned = nullptr;
        Require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&Provider::Generate), &pinned) != FALSE,
            RbaAmdProviderRuntimeUnavailable, "Could not pin AMD provider worker/callback module");
    }
    void Window() {
        DWORD process = 0, parentProcess = 0; GetWindowThreadProcessId(child, &process); GetWindowThreadProcessId(parent, &parentProcess);
        RECT rect{}, parentRect{}; POINT origin{}; const LONG_PTR style = GetWindowLongPtrW(child, GWL_STYLE);
        Require(IsWindow(child) && IsWindow(parent) && process == GetCurrentProcessId() && parentProcess == process && GetParent(child) == parent &&
            (style & WS_CHILD) && (style & WS_DISABLED) && !IsWindowEnabled(child) && GetClientRect(child, &rect) && GetClientRect(parent, &parentRect) &&
            ClientToScreen(child, &origin) && ScreenToClient(parent, &origin) && origin.x == 0 && origin.y == 0 &&
            parentRect.right - parentRect.left == static_cast<LONG>(desc.displayWidth) &&
            parentRect.bottom - parentRect.top == static_cast<LONG>(desc.displayHeight) &&
            rect.right - rect.left == static_cast<LONG>(desc.displayWidth) &&
            rect.bottom - rect.top == static_cast<LONG>(desc.displayHeight),
            RbaAmdProviderWindowChanged, "AMD child HWND lease, disabled style, parent or extent changed");
    }
    void PublishFailure(uint32_t code, const char* reason) {
        std::lock_guard<std::mutex> lock(mutex);
        status.result = code; status.poisoned = 1; CopyText(status.reason, sizeof(status.reason), reason);
    }
    static ffxReturnCode_t Generate(ffxDispatchDescFrameGeneration* params, void* user) noexcept {
        auto& self = *static_cast<Provider*>(user);
        if (!params) return FFX_API_RETURN_ERROR_PARAMETER;
        params->reset = params->reset || self.frame.camera.resetHistory || !self.lastProcessed ||
            self.frame.realFrameId != self.lastProcessed + 1;
        // SDK callback occurs during worker Present. It owns command list/output,
        // transitions, interpolation submission and pacing. No substitute Presents.
        const auto result = self.runtime->api.Dispatch(&self.effect, &params->header);
        { std::lock_guard<std::mutex> lock(self.mutex);
          self.status.sdkResult = result;
          if (result == FFX_API_RETURN_OK) self.status.generatedDispatches += params->numGeneratedFrames;
          else { self.status.poisoned = 1; self.status.result = RbaAmdProviderSdkError;
              CopyText(self.status.reason, sizeof(self.status.reason), "AMD interpolation callback failed"); } }
        // The wrapper tests output count independently of the return code.
        if (result != FFX_API_RETURN_OK) params->numGeneratedFrames = 0;
        return result;
    }
    void Initialize() {
        Window();
        runtime = std::make_unique<Runtime>(directory.c_str());
        ComPtr<IDXGIDevice> dxgi; Check(source.As(&dxgi), "Query AMD source DXGI device failed");
        ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter), "Get AMD source adapter failed");
        DXGI_ADAPTER_DESC ad{}; Check(adapter->GetDesc(&ad), "Read AMD source adapter failed");
        if (queue) {
            Check(queue->GetDevice(IID_PPV_ARGS(&device)), "Get supplied AMD native queue device failed");
            Require(queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT, RbaAmdProviderInvalidArgument, "AMD provider requires a native DIRECT queue");
        } else {
            Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)), "Create same-adapter AMD D3D12 device failed");
            D3D12_COMMAND_QUEUE_DESC q{}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            Check(device->CreateCommandQueue(&q, IID_PPV_ARGS(&queue)), "Create AMD native DIRECT queue failed");
        }
        const LUID luid = device->GetAdapterLuid();
        Require(luid.LowPart == ad.AdapterLuid.LowPart && luid.HighPart == ad.AdapterLuid.HighPart,
            RbaAmdProviderInvalidArgument, "AMD source and provider devices are on different adapters");
        D3D12_FEATURE_DATA_SHADER_MODEL shader{D3D_SHADER_MODEL_6_2};
        Check(device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &shader, sizeof(shader)), "Query AMD shader model failed");
        Require(shader.HighestShaderModel >= D3D_SHADER_MODEL_6_2, RbaAmdProviderUnsupported, "AMD frame generation requires shader model6.2");
        D3D12_FEATURE_DATA_FORMAT_SUPPORT format{DXGI_FORMAT_R16G16B16A16_UNORM};
        Check(device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &format, sizeof(format)), "Query AMD typed UAV loads failed");
        Require((format.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD) != 0, RbaAmdProviderUnsupported, "AMD typed UAV load requirement unavailable");
        Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "Create AMD native factory failed");
        Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create AMD provider allocator failed");
        Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)), "Create AMD provider list failed");
        Check(list->Close(), "Close initial AMD provider list failed");
        Check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&completion)), "Create AMD provider fence failed");
        completeEvent.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        Require(completeEvent.value != nullptr, RbaAmdProviderDeviceError, "Create AMD worker event failed");
        create.header = {FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION, &version.header};
        create.displaySize = {desc.displayWidth, desc.displayHeight}; create.maxRenderSize = {desc.renderWidth, desc.renderHeight};
        create.backBufferFormat = FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
        if (desc.reversedDepth) create.flags |= FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED;
        version.header = {FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION, &hudlessCreate.header};
        version.version = FFX_FRAMEGENERATION_VERSION;
        hudlessCreate.header = {FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_HUDLESS, &backend.header};
        hudlessCreate.hudlessBackBufferFormat = FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12; backend.device = device.Get();
        if (desc.preferCompatibility) {
            uint64_t count = 0; ffxQueryDescGetVersions q{}; q.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
            q.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION; q.device = device.Get(); q.outputCount = &count;
            CheckFfx(runtime->api.Query(nullptr, &q.header), "Enumerate AMD provider versions");
            Require(count && count <= 64, RbaAmdProviderUnsupported, "No bounded AMD provider enumeration");
            std::vector<uint64_t> ids(static_cast<size_t>(count)); std::vector<const char*> names(static_cast<size_t>(count));
            q.versionIds = ids.data(); q.versionNames = names.data();
            CheckFfx(runtime->api.Query(nullptr, &q.header), "Read AMD provider versions");
            for (size_t i = 0; i < ids.size() && i < count; ++i) if (names[i] && std::strstr(names[i], "3.1.")) {
                overrideVersion.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION; overrideVersion.versionId = ids[i];
                backend.header.pNext = &overrideVersion.header; break;
            }
            Require(overrideVersion.versionId != 0, RbaAmdProviderUnsupported, "AMD SDK did not enumerate compatibility FG3.1");
        }
        CheckFfx(runtime->api.CreateContext(&effect, &create.header, nullptr), "Create AMD paced effect");
        ffxQueryGetProviderVersion selected{}; selected.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        CheckFfx(runtime->api.Query(&effect, &selected.header), "Query actual AMD paced provider version");
        Require(selected.versionName && selected.versionId, RbaAmdProviderUnsupported, "AMD provider identity unavailable");
        const uint32_t family = std::strstr(selected.versionName, "4.") == selected.versionName ? 4u :
            std::strstr(selected.versionName, "3.1.") == selected.versionName ? 3u : 0u;
        Require(family != 0, RbaAmdProviderUnsupported, "AMD selected an unrecognized provider family");
        chainDesc.Width = desc.displayWidth; chainDesc.Height = desc.displayHeight;
        chainDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; chainDesc.SampleDesc.Count = 1;
        chainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; chainDesc.BufferCount = 3;
        chainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; chainDesc.Scaling = DXGI_SCALING_STRETCH;
        chainDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        swapCreate.header = {FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12, &swapVersion.header};
        swapCreate.swapchain = &createdChain; swapCreate.hwnd = child; swapCreate.desc = &chainDesc;
        swapCreate.dxgiFactory = factory.Get(); swapCreate.gameQueue = queue.Get();
        swapVersion.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_VERSION_DX12;
        swapVersion.version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION;
        CheckFfx(runtime->api.CreateContext(&swapchain, &swapCreate.header, nullptr), "Create official AMD paced child swapchain");
        chain.Attach(createdChain); createdChain = nullptr;
        Require(chain != nullptr, RbaAmdProviderSdkError, "AMD wrapper returned no child swapchain");
        HWND actual = nullptr; Check(chain->GetHwnd(&actual), "Read AMD child swapchain HWND failed");
        Require(actual == child, RbaAmdProviderWindowChanged, "AMD wrapper HWND differs from the actual child lease");
        Check(chain->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709), "Set AMD SDR swapchain color space failed");
        nativeBaselineValid = SUCCEEDED(chain->GetLastPresentCount(&nativeBaseline));
        std::lock_guard<std::mutex> lock(mutex);
        status.initialized = 1; status.supportedMultiplierMask = 1u << 2; status.activeMultiplier = 2;
        status.providerFamily = family; status.providerVersionId = selected.versionId;
        CopyText(status.providerName, sizeof(status.providerName), selected.versionName);
        status.result = RbaAmdProviderOk; status.reason[0] = 0;
    }
    void WaitQueue() {
        Check(queue->Signal(completion.Get(), ++completionValue), "Signal AMD worker retirement failed");
        for (;;) {
            const uint64_t completed = completion->GetCompletedValue();
            Require(completed != UINT64_MAX && SUCCEEDED(device->GetDeviceRemovedReason()), RbaAmdProviderDeviceError, "AMD provider device was removed");
            if (completed >= completionValue) return;
            Check(ResetEvent(completeEvent.value) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "Reset AMD worker event failed");
            Check(completion->SetEventOnCompletion(completionValue, completeEvent.value), "Arm AMD worker retirement failed");
            const auto result = WaitForSingleObject(completeEvent.value, 1000);
            Require(result == WAIT_TIMEOUT || result == WAIT_OBJECT_0, RbaAmdProviderDeviceError, "AMD worker retirement wait failed");
        }
    }
    void WaitSdk() {
        if (!swapchain) return;
        ffxDispatchDescFrameGenerationSwapChainWaitForPresentsDX12 wait{};
        wait.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_WAIT_FOR_PRESENTS_DX12;
        CheckFfx(runtime->api.Dispatch(&swapchain, &wait.header), "Wait for AMD paced presents");
    }
    void RestoreInputs() {
        if (!shaderStatesSubmitted) return;
        if (restorationSubmitted) {
            // A prior restoration Execute may have succeeded before its signal
            // failed. Retry proof of completion, never the state transition.
            WaitQueue(); restorationSubmitted = false; shaderStatesSubmitted = false; return;
        }
        Check(allocator->Reset(), "Reset AMD restoration allocator failed");
        Check(list->Reset(allocator.Get(), nullptr), "Reset AMD restoration list failed");
        for (size_t i = 1; i < inputs.size(); ++i)
            Transition(list.Get(), inputs[i].Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
        Check(list->Close(), "Close AMD input restoration failed");
        ID3D12CommandList* commands[] = {list.Get()}; restorationSubmitted = true;
        queue->ExecuteCommandLists(1, commands); WaitQueue();
        restorationSubmitted = false; shaderStatesSubmitted = false;
    }
    void Process() {
        Window(); const auto& c = frame.camera;
        if (producerFence) Check(queue->Wait(producerFence.Get(), frame.inputReadyValue), "Queue AMD producer dependency failed");
        ffxConfigureDescFrameGeneration config{}; config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.swapChain = chain.Get(); config.frameGenerationCallback = Generate; config.frameGenerationCallbackUserContext = this;
        config.frameGenerationEnabled = true; config.allowAsyncWorkloads = false;
        config.HUDLessColor = ffxApiGetResourceDX12(inputs[1].Get());
        config.generationRect = {0, 0, static_cast<int32_t>(desc.displayWidth), static_cast<int32_t>(desc.displayHeight)};
        config.frameID = frame.realFrameId;
        CheckFfx(runtime->api.Configure(&effect, &config.header), "Configure AMD paced real frame");
        Check(allocator->Reset(), "Reset AMD prepare allocator failed");
        Check(list->Reset(allocator.Get(), nullptr), "Reset AMD prepare list failed");
        Check(chain->GetBuffer(chain->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&backbuffer)), "Get AMD replacement real backbuffer failed");
        Transition(list.Get(), inputs[0].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
        Transition(list.Get(), backbuffer.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST);
        list->CopyResource(backbuffer.Get(), inputs[0].Get());
        Transition(list.Get(), inputs[0].Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);
        Transition(list.Get(), backbuffer.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT);
        for (size_t i = 1; i < inputs.size(); ++i)
            Transition(list.Get(), inputs[i].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        ffxDispatchDescFrameGenerationPrepareV2 prepare{};
        prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
        prepare.commandList = list.Get(); prepare.frameID = frame.realFrameId;
        prepare.renderSize = {desc.renderWidth, desc.renderHeight};
        prepare.jitterOffset = {c.jitterRenderPixelsX, c.jitterRenderPixelsY};
        prepare.motionVectorScale = {c.motionScaleX * desc.renderWidth, c.motionScaleY * desc.renderHeight};
        prepare.frameTimeDelta = c.frameTimeMilliseconds;
        prepare.reset = c.resetHistory || !lastProcessed || frame.realFrameId != lastProcessed + 1;
        prepare.cameraNear = c.nearPlane; prepare.cameraFar = c.farPlane; prepare.cameraFovAngleVertical = c.verticalFovRadians;
        prepare.viewSpaceToMetersFactor = c.viewSpaceToMeters;
        prepare.depth = ffxApiGetResourceDX12(inputs[2].Get()); prepare.motionVectors = ffxApiGetResourceDX12(inputs[3].Get());
        rba_amd_provider::Matrix view{}, inverse{};
        rba_amd_provider::MatrixRead(c.worldToView, view); rba_amd_provider::Invert(view, inverse);
        for (size_t i = 0; i < 3; ++i) {
            prepare.cameraPosition[i] = static_cast<float>(inverse[i * 4 + 3]);
            prepare.cameraRight[i] = static_cast<float>(inverse[i * 4]);
            prepare.cameraUp[i] = static_cast<float>(inverse[i * 4 + 1]);
            prepare.cameraForward[i] = -static_cast<float>(inverse[i * 4 + 2]);
        }
        CheckFfx(runtime->api.Dispatch(&effect, &prepare.header), "Prepare AMD paced inputs");
        Check(list->Close(), "Close AMD prepare commands failed");
        ID3D12CommandList* commands[] = {list.Get()};
        shaderStatesSubmitted = true; queue->ExecuteCommandLists(1, commands);
        Window();
        const HRESULT presented = chain->Present(1, 0);
        { std::lock_guard<std::mutex> lock(mutex); ++status.realPresentCalls; status.lastPresentHresult = presented; }
        Check(presented, "AMD paced child Present failed");
        WaitSdk(); WaitQueue(); RestoreInputs();
        UINT count = 0; const bool valid = nativeBaselineValid && SUCCEEDED(chain->GetLastPresentCount(&count));
        bool healthy = false;
        { std::lock_guard<std::mutex> lock(mutex);
          status.nativePresentCountValid = valid;
          if (valid) status.nativePresentCountDelta = static_cast<UINT>(count - nativeBaseline);
          healthy = !status.poisoned; }
        Require(healthy, RbaAmdProviderSdkError, "AMD generated callback failed; destroy the provider after retirement");
        inputs = {}; producerFence.Reset(); backbuffer.Reset();
        lastProcessed = frame.realFrameId;
        { std::lock_guard<std::mutex> lock(mutex);
          status.lastRetiredFrameId = lastProcessed; status.inputBatchPending = 0; status.result = RbaAmdProviderOk; status.reason[0] = 0; }
    }
    void Cleanup() {
        // Runs only on the worker, with no concurrent vendor/API use. Unknown
        // vendor waits keep this worker/context and all resources quarantined.
        WaitSdk();
        if (queue && completion) { WaitQueue(); RestoreInputs(); }
        if (effect) {
            ffxConfigureDescFrameGeneration disabled{}; disabled.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
            disabled.swapChain = chain.Get();
            if (chain) CheckFfx(runtime->api.Configure(&effect, &disabled.header), "Disable AMD child generation");
            CheckFfx(runtime->api.DestroyContext(&effect, nullptr), "Destroy AMD paced effect"); effect = nullptr;
        }
        if (swapchain) { CheckFfx(runtime->api.DestroyContext(&swapchain, nullptr), "Destroy AMD paced swapchain context"); swapchain = nullptr; }
        chain.Reset();
        if (createdChain) { createdChain->Release(); createdChain = nullptr; }
        inputs = {}; producerFence.Reset(); backbuffer.Reset();
        list.Reset(); allocator.Reset(); completion.Reset(); factory.Reset(); queue.Reset(); device.Reset(); source.Reset(); runtime.reset();
    }
    void Run() noexcept {
        try { Initialize(); }
        catch (const Failure& e) { PublishFailure(e.code, e.what()); }
        catch (const std::exception& e) { PublishFailure(RbaAmdProviderRuntimeUnavailable, e.what()); }
        catch (...) { PublishFailure(RbaAmdProviderRuntimeUnavailable, "Unknown AMD provider initialization failure"); }
        for (;;) {
            { std::unique_lock<std::mutex> lock(mutex); changed.wait(lock, [&] { return job || stop; });
              if (stop && !job) break; }
            try { Process(); }
            catch (const Failure& e) { PublishFailure(e.code, e.what()); }
            catch (const std::exception& e) { PublishFailure(RbaAmdProviderSdkError, e.what()); }
            catch (...) { PublishFailure(RbaAmdProviderSdkError, "Unknown AMD provider submission failure"); }
            { std::lock_guard<std::mutex> lock(mutex); job = false; }
        }
        try { Cleanup(); }
        catch (const Failure& e) { PublishFailure(e.code, e.what()); cleanupFailed = true; }
        catch (...) { PublishFailure(RbaAmdProviderPoisoned, "AMD cleanup failed; retain the provider/window lease until process exit"); cleanupFailed = true; }
        { std::lock_guard<std::mutex> lock(mutex); ended = true; }
    }
    void Validate(const RbaAmdProviderFrame& f, std::array<ComPtr<ID3D12Resource>, 4>& retained, ComPtr<ID3D12Fence>& ready) {
        Require(f.structSize == sizeof(f) && f.abiVersion == Abi && !f.reserved && f.realFrameId &&
            f.realFrameId > status.lastAcceptedFrameId, RbaAmdProviderInvalidArgument, "Invalid/stale AMD real-frame ABI or identity");
        Require(f.windowEpoch == desc.windowEpoch, RbaAmdProviderWindowChanged, "AMD frame does not match the retained child epoch");
        Require(rba_amd_provider::CameraValid(f.camera) && f.camera.reversedDepth == desc.reversedDepth,
            RbaAmdProviderInvalidArgument, "Invalid AMD camera matrices, elapsed time, depth or motion convention");
        Require(std::isfinite(f.camera.motionScaleX * desc.renderWidth) && std::isfinite(f.camera.motionScaleY * desc.renderHeight),
            RbaAmdProviderInvalidArgument, "AMD pixel motion scale overflows");
        const void* raw[] = {f.finalColor, f.hudlessColor, f.rawDepth, f.normalizedMotion};
        for (size_t i = 0; i < retained.size(); ++i) {
            Require(raw[i] && SUCCEEDED(static_cast<IUnknown*>(const_cast<void*>(raw[i]))->QueryInterface(IID_PPV_ARGS(&retained[i]))),
                RbaAmdProviderInvalidArgument, "AMD frame is missing a required D3D12 texture");
            for (size_t j = 0; j < i; ++j) Require(!SameObject(retained[i].Get(), retained[j].Get()),
                RbaAmdProviderInvalidArgument, "AMD input textures must be distinct");
            ComPtr<ID3D12Device> owner; Check(retained[i]->GetDevice(IID_PPV_ARGS(&owner)), "Query AMD texture device failed");
            Require(SameObject(owner.Get(), device.Get()), RbaAmdProviderInvalidArgument, "AMD input texture belongs to another D3D12 device");
            const auto d = retained[i]->GetDesc();
            Require(d.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D && d.DepthOrArraySize == 1 && d.MipLevels == 1 &&
                d.SampleDesc.Count == 1 && !(d.Flags & D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE) &&
                d.Width == (i < 2 ? desc.displayWidth : desc.renderWidth) && d.Height == (i < 2 ? desc.displayHeight : desc.renderHeight),
                RbaAmdProviderInvalidArgument, "AMD input texture shape does not match its frame contract");
            const bool format = i < 2 ? d.Format == DXGI_FORMAT_R8G8B8A8_UNORM : i == 2 ? d.Format == DXGI_FORMAT_R32_FLOAT :
                d.Format == DXGI_FORMAT_R16G16_FLOAT || d.Format == DXGI_FORMAT_R32G32_FLOAT;
            Require(format, RbaAmdProviderInvalidArgument, "AMD input texture format is incompatible");
        }
        Require(f.inputReadyFence || f.inputReadyValue == 0, RbaAmdProviderInvalidArgument, "AMD producer value requires a real fence");
        if (f.inputReadyFence) {
            Require(SUCCEEDED(static_cast<IUnknown*>(f.inputReadyFence)->QueryInterface(IID_PPV_ARGS(&ready))),
                RbaAmdProviderInvalidArgument, "AMD producer object is not a D3D12 fence");
            ComPtr<ID3D12Device> owner; Check(ready->GetDevice(IID_PPV_ARGS(&owner)), "Query AMD producer fence device failed");
            Require(SameObject(owner.Get(), device.Get()) && f.inputReadyValue != UINT64_MAX,
                RbaAmdProviderInvalidArgument, "AMD producer fence device/value mismatch");
        }
    }
};
uint32_t Report(RbaAmdProviderStatus* out, RbaAmdProviderStatus s, uint32_t result, const char* reason) {
    s.result = result; CopyText(s.reason, sizeof(s.reason), reason); if (out) *out = s; return result;
}
}
static_assert(sizeof(RbaAmdProviderCamera) == 304, "AMD camera ABI1");
static_assert(sizeof(RbaAmdProviderCreateDesc) == 72, "AMD create ABI1");
static_assert(sizeof(RbaAmdProviderFrame) == 376, "AMD frame ABI1");
static_assert(sizeof(RbaAmdProviderStatus) == 496, "AMD status ABI1");

uint32_t RbaAmdProviderCreate(const RbaAmdProviderCreateDesc* d, void** output, RbaAmdProviderStatus* s) {
    if (output) *output = nullptr;
    try {
        Require(output && d && d->structSize == sizeof(*d) && d->abiVersion == Abi && d->runtimeDirectory && *d->runtimeDirectory &&
            d->sourceD3D11Device && d->childHwnd && d->windowEpoch && d->renderWidth && d->renderHeight &&
            d->displayWidth >= d->renderWidth && d->displayHeight >= d->renderHeight && d->displayWidth <= 16384 && d->displayHeight <= 16384 &&
            d->reversedDepth <= 1 && d->preferCompatibility <= 1, RbaAmdProviderInvalidArgument, "Invalid AMD paced create ABI, dimensions or lease");
        auto p = std::make_unique<Provider>(*d);
        if (s) *s = p->status;
        p->worker = std::thread([ptr = p.get()] { ptr->Run(); });
        *output = p.release(); return RbaAmdProviderBusy;
    } catch (const Failure& e) { return Report(s, ProviderStatus(), e.code, e.what()); }
    catch (const std::exception& e) { return Report(s, ProviderStatus(), RbaAmdProviderRuntimeUnavailable, e.what()); }
    catch (...) { return Report(s, ProviderStatus(), RbaAmdProviderRuntimeUnavailable, "Unknown AMD worker creation failure"); }
}
uint32_t RbaAmdProviderGetInterfaces(void* context, RbaAmdProviderInterfaces* out) {
    if (!context || !out || out->structSize != sizeof(*out) || out->abiVersion != Abi) return RbaAmdProviderInvalidArgument;
    auto& p = *static_cast<Provider*>(context); std::lock_guard<std::mutex> lock(p.mutex);
    out->device12 = nullptr; out->queue12 = nullptr;
    if (p.stop || p.status.poisoned) return RbaAmdProviderPoisoned;
    if (!p.status.initialized) return RbaAmdProviderBusy;
    out->device12 = p.device.Get(); out->queue12 = p.queue.Get(); return RbaAmdProviderOk;
}
uint32_t RbaAmdProviderSubmit(void* context, const RbaAmdProviderFrame* frame, RbaAmdProviderStatus* s) {
    if (!context) return Report(s, ProviderStatus(), RbaAmdProviderInvalidArgument, "AMD provider context is null");
    auto& p = *static_cast<Provider*>(context); std::lock_guard<std::mutex> lock(p.mutex);
    try {
        Require(!p.stop && !p.status.poisoned, RbaAmdProviderPoisoned, "AMD provider is stopping or poisoned; preserve leases and destroy it");
        Require(p.status.initialized && !p.job && !p.status.inputBatchPending, RbaAmdProviderBusy, "AMD worker is initializing or retaining the previous batch");
        Require(frame != nullptr, RbaAmdProviderInvalidArgument, "AMD frame is null"); p.Window();
        std::array<ComPtr<ID3D12Resource>, 4> retained; ComPtr<ID3D12Fence> ready; p.Validate(*frame, retained, ready);
        p.frame = *frame; p.inputs = std::move(retained); p.producerFence = std::move(ready);
        p.status.inputBatchPending = 1; p.status.lastAcceptedFrameId = frame->realFrameId; ++p.status.acceptedRealFrames;
        p.status.result = RbaAmdProviderOk; p.status.reason[0] = 0; p.job = true; p.changed.notify_one();
        if (s) *s = p.status; return RbaAmdProviderOk;
    } catch (const Failure& e) { return Report(s, p.status, e.code, e.what()); }
    catch (...) { return Report(s, p.status, RbaAmdProviderInvalidArgument, "AMD frame validation failed before acceptance"); }
}
uint32_t RbaAmdProviderPoll(void* context, RbaAmdProviderStatus* s) {
    if (!context) return Report(s, ProviderStatus(), RbaAmdProviderInvalidArgument, "AMD provider context is null");
    auto& p = *static_cast<Provider*>(context); std::lock_guard<std::mutex> lock(p.mutex);
    if (p.status.poisoned) { if (s) *s = p.status; return p.status.result; }
    if (p.stop || !p.status.initialized || p.job || p.status.inputBatchPending)
        return Report(s, p.status, RbaAmdProviderBusy, "AMD worker initialization, submission or cleanup is pending");
    if (s) *s = p.status; return RbaAmdProviderOk;
}
uint32_t RbaAmdProviderDestroy(void* context, RbaAmdProviderStatus* s) {
    if (!context) return Report(s, ProviderStatus(), RbaAmdProviderOk, "");
    auto* p = static_cast<Provider*>(context);
    { std::lock_guard<std::mutex> lock(p->mutex);
      p->stop = true; p->status.destroying = 1; p->changed.notify_one();
      if (!p->ended) return Report(s, p->status, RbaAmdProviderBusy, "AMD worker cleanup remains pending; retain HWND and resource leases");
      if (p->cleanupFailed) return Report(s, p->status, RbaAmdProviderPoisoned, "AMD cleanup is uncertain; quarantine context, inputs and HWND until process exit");
      if (s) { *s = p->status; s->result = RbaAmdProviderOk; s->inputBatchPending = 0; s->reason[0] = 0; } }
    p->worker.join(); delete p; return RbaAmdProviderOk;
}
