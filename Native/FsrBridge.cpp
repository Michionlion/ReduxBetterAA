#include "FsrBridge.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <ffx_api_loader.h>
#include <dx12/ffx_api_dx12.h>
#include <ffx_upscale.h>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;
namespace {
constexpr uint32_t Abi = 1;
constexpr uint32_t AllowedFlags = RbaFsrHdr | RbaFsrInvertedDepth | RbaFsrInfiniteDepth | RbaFsrAutoExposure;
constexpr uint32_t SlotCount = 3;
constexpr DWORD GpuWaitMilliseconds = 5000;
constexpr const wchar_t* UpscalerName = L"amd_fidelityfx_upscaler_dx12.dll";
constexpr const char* UpscalerHash = "d0dcccc74a43c44ba435b7a369b456e0970d8a4464e4bd683119b374f2c9fb46";

struct Failure : std::runtime_error {
    uint32_t result;
    Failure(uint32_t code, const std::string& message) : std::runtime_error(message), result(code) {}
};
void Require(bool condition, uint32_t code, const char* message) {
    if (!condition) throw Failure(code, message);
}
void Check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        char message[256];
        std::snprintf(message, sizeof(message), "%s failed (HRESULT 0x%08lx)", operation, static_cast<unsigned long>(result));
        throw Failure(RbaFsrDeviceError, message);
    }
}
void CheckFfx(ffxReturnCode_t result, const char* operation) {
    if (result != FFX_API_RETURN_OK) {
        char message[256];
        std::snprintf(message, sizeof(message), "%s failed (FSR API %u)", operation, result);
        throw Failure(RbaFsrDispatchError, message);
    }
}
void CopyText(char* target, size_t capacity, const char* text) {
    if (target && capacity) {
        const char* source = text ? text : "";
        const size_t available = std::strlen(source);
        const size_t length = available < capacity - 1 ? available : capacity - 1;
        std::memcpy(target, source, length);
        target[length] = 0;
    }
}
struct WinHandle {
    HANDLE value = nullptr;
    ~WinHandle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

// Hold the verified file open without write/delete sharing throughout the DLL
// lifetime. Hash verification must finish before executing any vendor code.
struct Runtime {
    HMODULE module = nullptr;
    WinHandle file;
    ffxFunctions functions{};
    ~Runtime() { if (module) FreeLibrary(module); }
    explicit Runtime(const wchar_t* directory) {
        Require(directory && *directory, RbaFsrInvalidArgument, "FSR runtime directory is missing");
        const std::filesystem::path dir(directory);
        Require(dir.is_absolute(), RbaFsrInvalidArgument, "FSR runtime directory must be absolute");
        const auto path = std::filesystem::weakly_canonical(dir / UpscalerName);
        file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(file.value != INVALID_HANDLE_VALUE, RbaFsrRuntimeUnavailable, "Approved AMD FSR upscaler runtime is missing or locked");
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0,
            RbaFsrRuntimeUnavailable, "Could not initialize SHA256");
        try {
            Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0,
                RbaFsrRuntimeUnavailable, "Could not create SHA256 hash");
            std::array<unsigned char, 65536> buffer{};
            DWORD count = 0;
            do {
                Require(ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()), &count, nullptr) != FALSE,
                    RbaFsrRuntimeUnavailable, "Could not read AMD runtime for verification");
                if (count) Require(BCryptHashData(hash, buffer.data(), count, 0) >= 0,
                    RbaFsrRuntimeUnavailable, "Could not hash AMD runtime");
            } while (count);
            std::array<unsigned char, 32> digest{};
            Require(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0,
                RbaFsrRuntimeUnavailable, "Could not finish AMD runtime hash");
            char hex[65]{};
            for (size_t i = 0; i < digest.size(); ++i) std::snprintf(hex + i * 2, 3, "%02x", digest[i]);
            Require(std::strcmp(hex, UpscalerHash) == 0, RbaFsrRuntimeUnavailable,
                "AMD upscaler SHA256 does not match pinned official SDK 2.3.0");
        } catch (...) {
            if (hash) BCryptDestroyHash(hash);
            BCryptCloseAlgorithmProvider(algorithm, 0);
            throw;
        }
        BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(algorithm, 0);
        if (HMODULE loaded = GetModuleHandleW(UpscalerName)) {
            std::array<wchar_t, 32768> loadedPath{};
            const DWORD length = GetModuleFileNameW(loaded, loadedPath.data(), static_cast<DWORD>(loadedPath.size()));
            Require(length && length < loadedPath.size() && std::filesystem::equivalent(path, loadedPath.data()),
                RbaFsrRuntimeUnavailable, "Another AMD upscaler runtime is already loaded from a different directory");
        }
        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(module != nullptr, RbaFsrRuntimeUnavailable, "The approved AMD FSR runtime could not be loaded");
        ffxLoadFunctions(&functions, module);
        if (!functions.CreateContext || !functions.DestroyContext || !functions.Query || !functions.Dispatch || !functions.Configure) {
            FreeLibrary(module); module = nullptr;
            throw Failure(RbaFsrRuntimeUnavailable, "AMD FSR runtime does not export the expected API");
        }
    }
};

void ValidateCreate(const RbaFsrCreateDesc* desc) {
    Require(desc && desc->structSize == sizeof(RbaFsrCreateDesc) && desc->abiVersion == Abi,
        RbaFsrAbiMismatch, "FSR create structure does not match bridge ABI 1");
    Require(desc->graphicsApi == 11, RbaFsrUnsupported, "This bridge requires Direct3D 11; other APIs use the existing Unity fallback");
    Require((desc->flags & ~AllowedFlags) == 0, RbaFsrInvalidArgument, "Unsupported FSR context flags");
    Require(desc->renderWidth && desc->renderHeight && desc->outputWidth >= desc->renderWidth &&
        desc->outputHeight >= desc->renderHeight && desc->outputWidth <= D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
        desc->outputHeight <= D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION, RbaFsrInvalidArgument, "Invalid render/output dimensions");
}

struct SharedTexture {
    ComPtr<ID3D12Resource> dx12;
    ComPtr<ID3D11Texture2D> dx11;
    void Create(ID3D12Device* device12, ID3D11Device5* device11, uint32_t width, uint32_t height, DXGI_FORMAT format) {
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC description{};
        description.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        description.Width = width; description.Height = height;
        description.DepthOrArraySize = 1; description.MipLevels = 1;
        description.Format = format; description.SampleDesc.Count = 1;
        description.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET |
            D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
        Check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &description,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&dx12)), "Create shared FSR texture");
        WinHandle shared;
        Check(device12->CreateSharedHandle(dx12.Get(), nullptr, GENERIC_ALL, nullptr, &shared.value), "Share FSR texture");
        Check(device11->OpenSharedResource1(shared.value, IID_PPV_ARGS(&dx11)), "Open FSR texture in D3D11");
    }
};
enum TextureIndex : size_t { Color, Depth, Motion, Output, Reactive, Exposure, Transparency, TextureCount };
struct Slot {
    std::array<SharedTexture, TextureCount> shared;
    std::array<ComPtr<ID3D11Texture2D>, TextureCount> borrowed;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    uint64_t retirement = 0;
};
struct PendingPacket {
    RbaFsrDispatchDesc frame{}; // First member: pointer also matches callback ABI.
    bool busy = false;
    std::array<ComPtr<IUnknown>, TextureCount> references;
};

struct Context {
    std::mutex mutex;
    RbaFsrCreateDesc description{};
    RbaFsrStatus status{};
    std::unique_ptr<Runtime> runtime;
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> immediate;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence12;
    ComPtr<ID3D11Fence> fence11;
    WinHandle waitEvent;
    std::array<Slot, SlotCount> slots;
    std::array<PendingPacket, 8> packets;
    uint64_t nextFence = 0;
    uint32_t nextSlot = 0;
    DXGI_FORMAT motionFormat = DXGI_FORMAT_UNKNOWN;
    bool hasFrame = false;
    bool gpuReady = false;
    ffxContext effect = nullptr;
    // AMD requires creation descriptor storage to survive until context destroy.
    ffxCreateContextDescUpscale create{};
    ffxCreateContextDescUpscaleVersion version{};
    ffxCreateBackendDX12Desc backend{};

    explicit Context(const RbaFsrCreateDesc& desc) : description(desc) {
        description.runtimeDirectory = nullptr;
        status.structSize = sizeof(status); status.abiVersion = Abi;
        runtime = std::make_unique<Runtime>(desc.runtimeDirectory);
    }
    void Fail(uint32_t result, const char* message) {
        status.state = RbaFsrFailed; status.result = result;
        CopyText(status.error, sizeof(status.error), message);
    }
    void CreateDevice(ID3D11Texture2D* texture) {
        ComPtr<ID3D11Device> base;
        texture->GetDevice(&base);
        Check(base.As(&device11), "Query D3D11 device with shared-fence support");
        ComPtr<IDXGIDevice> dxgiDevice;
        Check(device11.As(&dxgiDevice), "Query DXGI device");
        ComPtr<IDXGIAdapter> adapter;
        Check(dxgiDevice->GetAdapter(&adapter), "Get Unity GPU adapter");
        DXGI_ADAPTER_DESC adapterDesc{};
        Check(adapter->GetDesc(&adapterDesc), "Describe Unity GPU adapter");
        status.adapterVendorId = adapterDesc.VendorId;
        status.adapterDeviceId = adapterDesc.DeviceId;
        Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device12)), "Create same-adapter D3D12 device");
        const auto luid = device12->GetAdapterLuid();
        Require(luid.HighPart == adapterDesc.AdapterLuid.HighPart && luid.LowPart == adapterDesc.AdapterLuid.LowPart,
            RbaFsrUnsupported, "FSR D3D11 and D3D12 adapter identities differ");
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = device12.Get();
        version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
        version.version = FFX_UPSCALER_VERSION;
        version.header.pNext = &backend.header;
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        create.header.pNext = &version.header;
        create.flags = description.flags;
        create.maxRenderSize = { description.renderWidth, description.renderHeight };
        create.maxUpscaleSize = { description.outputWidth, description.outputHeight };
        // Let AMD select its supported default. Never hardcode a provider ID or
        // bypass the runtime's hardware support decision.
        CheckFfx(runtime->functions.CreateContext(&effect, &create.header, nullptr), "Create AMD FSR context");
        ffxQueryGetProviderVersion provider{};
        provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        CheckFfx(runtime->functions.Query(&effect, &provider.header), "Query actual AMD FSR provider");
        Require(provider.versionName && *provider.versionName, RbaFsrUnsupported, "AMD did not identify its selected provider");
        status.providerVersionId = provider.versionId;
        CopyText(status.providerName, sizeof(status.providerName), provider.versionName);
    }
    void InitializeGpu() {
        ComPtr<ID3D11DeviceContext> base;
        device11->GetImmediateContext(&base);
        Check(base.As(&immediate), "Query D3D11 context with shared-fence support");
        D3D12_COMMAND_QUEUE_DESC queueDesc{}; queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device12->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue)), "Create FSR command queue");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12)), "Create FSR shared fence");
        WinHandle shared;
        Check(device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &shared.value), "Share FSR fence");
        Check(device11->OpenSharedFence(shared.value, IID_PPV_ARGS(&fence11)), "Open FSR fence in D3D11");
        waitEvent.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        Require(waitEvent.value != nullptr, RbaFsrDeviceError, "Could not create FSR completion event");
        for (auto& slot : slots) {
            const std::array<DXGI_FORMAT, TextureCount> formats = {
                DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R32_FLOAT, motionFormat,
                DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R8_UNORM };
            for (size_t i = 0; i < TextureCount; ++i) {
                const uint32_t width = i == Exposure ? 1 : (i == Output ? description.outputWidth : description.renderWidth);
                const uint32_t height = i == Exposure ? 1 : (i == Output ? description.outputHeight : description.renderHeight);
                slot.shared[i].Create(device12.Get(), device11.Get(), width, height, formats[i]);
            }
            Check(device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator)), "Create FSR command allocator");
            Check(device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot.allocator.Get(), nullptr, IID_PPV_ARGS(&slot.list)), "Create FSR command list");
            Check(slot.list->Close(), "Close initial FSR command list");
        }
        gpuReady = true;
    }
    void Wait(uint64_t value) {
        if (!value || !fence12) return;
        const uint64_t completed = fence12->GetCompletedValue();
        Require(completed != UINT64_MAX, RbaFsrDeviceError, "FSR device was removed");
        if (completed >= value) return;
        Check(fence12->SetEventOnCompletion(value, waitEvent.value), "Wait for FSR completion");
        const DWORD result = WaitForSingleObject(waitEvent.value, GpuWaitMilliseconds);
        Require(result == WAIT_OBJECT_0, RbaFsrGpuTimeout, "Timed out retiring FSR GPU resources; context retained for safe cleanup");
        Check(device12->GetDeviceRemovedReason(), "Check FSR device");
    }
    void Destroy() {
        for (auto& slot : slots) Wait(slot.retirement);
        if (effect) {
            CheckFfx(runtime->functions.DestroyContext(&effect, nullptr), "Destroy AMD FSR context");
            effect = nullptr; // AMD's API does not guarantee clearing the caller's pointer.
        }
        for (auto& slot : slots) slot = Slot{};
        for (auto& packet : packets) packet = PendingPacket{};
        immediate.Reset(); fence11.Reset(); fence12.Reset(); queue.Reset(); device12.Reset(); device11.Reset();
        gpuReady = false;
        status.state = RbaFsrDestroyed; status.result = RbaFsrOk;
        status.error[0] = 0;
    }
    // For initialization/probe failures, no GPU work has been submitted yet.
    ~Context() { if (effect && runtime) runtime->functions.DestroyContext(&effect, nullptr); }
    std::array<ComPtr<ID3D11Texture2D>, TextureCount> GetTextures(const RbaFsrDispatchDesc& frame) {
        const std::array<void*, TextureCount> pointers = {
            frame.color, frame.depth, frame.motionVectors, frame.output, frame.reactive, frame.exposure, frame.transparencyAndComposition };
        std::array<ComPtr<ID3D11Texture2D>, TextureCount> textures;
        ComPtr<ID3D11Device> expected;
        if (device11) Check(device11.As(&expected), "Get active D3D11 device identity");
        for (size_t i = 0; i < TextureCount; ++i) {
            if (!pointers[i]) {
                Require(i >= Reactive && (i != Exposure || (description.flags & RbaFsrAutoExposure)),
                    RbaFsrInvalidArgument, "Required FSR texture is missing");
                continue;
            }
            Check(static_cast<IUnknown*>(pointers[i])->QueryInterface(IID_PPV_ARGS(&textures[i])), "Query FSR input texture");
            ComPtr<ID3D11Device> actual;
            textures[i]->GetDevice(&actual);
            if (!expected) expected = actual;
            Require(actual.Get() == expected.Get(), RbaFsrInvalidArgument, "FSR textures belong to different D3D11 devices");
            D3D11_TEXTURE2D_DESC desc{}; textures[i]->GetDesc(&desc);
            const uint32_t width = i == Exposure ? 1 : (i == Output ? description.outputWidth : description.renderWidth);
            const uint32_t height = i == Exposure ? 1 : (i == Output ? description.outputHeight : description.renderHeight);
            Require(desc.Width == width && desc.Height == height && desc.MipLevels == 1 && desc.ArraySize == 1 && desc.SampleDesc.Count == 1,
                RbaFsrInvalidArgument, "FSR texture dimensions, mip count, array size or MSAA do not match context");
            bool formatValid = false;
            if (i == Color || i == Output) formatValid = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT || desc.Format == DXGI_FORMAT_R16G16B16A16_TYPELESS;
            else if (i == Depth || i == Exposure) formatValid = desc.Format == DXGI_FORMAT_R32_FLOAT || desc.Format == DXGI_FORMAT_R32_TYPELESS;
            else if (i == Motion) {
                const DXGI_FORMAT typed = (desc.Format == DXGI_FORMAT_R16G16_TYPELESS) ? DXGI_FORMAT_R16G16_FLOAT :
                    (desc.Format == DXGI_FORMAT_R32G32_TYPELESS) ? DXGI_FORMAT_R32G32_FLOAT : desc.Format;
                formatValid = typed == DXGI_FORMAT_R16G16_FLOAT || typed == DXGI_FORMAT_R32G32_FLOAT;
                if (motionFormat == DXGI_FORMAT_UNKNOWN) motionFormat = typed;
                formatValid = formatValid && motionFormat == typed;
            } else formatValid = desc.Format == DXGI_FORMAT_R8_UNORM || desc.Format == DXGI_FORMAT_R8_TYPELESS;
            Require(formatValid, RbaFsrInvalidArgument, "Unsupported FSR texture format; convert inputs to the documented bridge formats");
        }
        Require(textures[Color].Get() != textures[Output].Get(), RbaFsrInvalidArgument, "FSR input and output cannot alias");
        return textures;
    }
    void Dispatch(const RbaFsrDispatchDesc& frame) {
        Require(frame.structSize == sizeof(frame) && frame.abiVersion == Abi && frame.reserved == 0,
            RbaFsrAbiMismatch, "FSR dispatch structure does not match bridge ABI 1");
        Require(status.state != RbaFsrFailed && status.state != RbaFsrDestroyed, RbaFsrBusy, "FSR context is not available for dispatch");
        const std::array<float, 11> values = { frame.jitterX, frame.jitterY, frame.motionScaleX, frame.motionScaleY,
            frame.frameTimeMilliseconds, frame.preExposure, frame.cameraNear, frame.cameraFar, frame.verticalFovRadians,
            frame.viewSpaceToMeters, frame.sharpness };
        for (float value : values) Require(std::isfinite(value), RbaFsrInvalidArgument, "Non-finite FSR frame parameter");
        Require(frame.frameTimeMilliseconds > 0 && frame.preExposure > 0 && frame.cameraNear > 0 && frame.cameraFar > 0 &&
            frame.cameraNear != frame.cameraFar && frame.motionScaleX != 0 && frame.motionScaleY != 0 &&
            frame.verticalFovRadians > 0 && frame.verticalFovRadians < 3.141593f && frame.viewSpaceToMeters > 0 &&
            frame.sharpness >= 0 && frame.sharpness <= 1 && frame.reset <= 1 && frame.enableSharpening <= 1,
            RbaFsrInvalidArgument, "Invalid FSR timing, exposure, projection or sharpening parameter");
        Require(!hasFrame || frame.reset || frame.frameId > status.lastSubmittedFrameId, RbaFsrInvalidArgument, "Duplicate/out-of-order FSR real frame");
        auto textures = GetTextures(frame);
        if (!device12) CreateDevice(textures[Color].Get());
        if (!gpuReady) InitializeGpu();
        Slot& slot = slots[nextSlot];
        Wait(slot.retirement);
        slot.borrowed = std::move(textures);
        Check(slot.allocator->Reset(), "Reset retired FSR allocator");
        Check(slot.list->Reset(slot.allocator.Get(), nullptr), "Reset FSR command list");

        // Prepare the independent DX12 list before making Unity wait on any work.
        // Every shared resource starts/ends COMMON for D3D11 interoperability.
        std::array<D3D12_RESOURCE_BARRIER, TextureCount> barriers{};
        UINT barrierCount = 0;
        for (size_t i = 0; i < TextureCount; ++i) {
            if (!slot.borrowed[i]) continue;
            auto& barrier = barriers[barrierCount++];
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = slot.shared[i].dx12.Get();
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
            barrier.Transition.StateAfter = i == Output ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        }
        slot.list->ResourceBarrier(barrierCount, barriers.data());
        ffxDispatchDescUpscale dispatch{};
        dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
        dispatch.commandList = slot.list.Get();
        auto resource = [&](size_t index) {
            return slot.borrowed[index] ? ffxApiGetResourceDX12(slot.shared[index].dx12.Get(),
                index == Output ? FFX_API_RESOURCE_STATE_UNORDERED_ACCESS : FFX_API_RESOURCE_STATE_COMPUTE_READ) : FfxApiResource{};
        };
        dispatch.color = resource(Color); dispatch.depth = resource(Depth);
        dispatch.motionVectors = resource(Motion); dispatch.output = resource(Output);
        dispatch.reactive = resource(Reactive); dispatch.exposure = resource(Exposure);
        dispatch.transparencyAndComposition = resource(Transparency);
        dispatch.jitterOffset = { frame.jitterX, frame.jitterY };
        dispatch.motionVectorScale = { frame.motionScaleX, frame.motionScaleY };
        dispatch.renderSize = { description.renderWidth, description.renderHeight };
        dispatch.upscaleSize = { description.outputWidth, description.outputHeight };
        dispatch.frameTimeDelta = frame.frameTimeMilliseconds; dispatch.preExposure = frame.preExposure;
        dispatch.cameraNear = frame.cameraNear; dispatch.cameraFar = frame.cameraFar;
        dispatch.cameraFovAngleVertical = frame.verticalFovRadians; dispatch.viewSpaceToMetersFactor = frame.viewSpaceToMeters;
        dispatch.sharpness = frame.sharpness; dispatch.enableSharpening = frame.enableSharpening != 0;
        dispatch.reset = frame.reset != 0 || !hasFrame;
        CheckFfx(runtime->functions.Dispatch(&effect, &dispatch.header), "Dispatch AMD FSR");
        for (UINT i = 0; i < barrierCount; ++i) std::swap(barriers[i].Transition.StateBefore, barriers[i].Transition.StateAfter);
        slot.list->ResourceBarrier(barrierCount, barriers.data());
        Check(slot.list->Close(), "Close FSR command list");

        for (size_t i = 0; i < TextureCount; ++i)
            if (i != Output && slot.borrowed[i]) immediate->CopyResource(slot.shared[i].dx11.Get(), slot.borrowed[i].Get());
        const uint64_t inputReady = ++nextFence;
        Check(immediate->Signal(fence11.Get(), inputReady), "Signal FSR inputs ready");
        slot.retirement = inputReady;
        immediate->Flush();
        Check(queue->Wait(fence12.Get(), inputReady), "Order FSR after D3D11 inputs");
        ID3D12CommandList* commandLists[] = { slot.list.Get() };
        queue->ExecuteCommandLists(1, commandLists);
        const uint64_t reconstructionDone = ++nextFence;
        // Set retirement before Signal: a failed device will retain these
        // resources until cleanup observes device removal, rather than reuse.
        slot.retirement = reconstructionDone;
        Check(queue->Signal(fence12.Get(), reconstructionDone), "Signal FSR reconstruction complete");
        Check(immediate->Wait(fence11.Get(), reconstructionDone), "Order Unity output after FSR");
        immediate->CopyResource(slot.borrowed[Output].Get(), slot.shared[Output].dx11.Get());
        const uint64_t outputCopied = ++nextFence;
        slot.retirement = outputCopied;
        Check(immediate->Signal(fence11.Get(), outputCopied), "Signal FSR output copied");
        immediate->Flush();
        nextSlot = (nextSlot + 1) % SlotCount;
        hasFrame = true;
        status.state = RbaFsrReady; status.result = RbaFsrOk;
        status.lastSubmittedFrameId = frame.frameId; status.error[0] = 0;
    }
};

void __stdcall RenderEvent(int eventId, void* data) {
    if (!data) return;
    Context* context = nullptr;
    if (eventId == RbaFsrDestroy) context = static_cast<Context*>(data);
    else if (eventId == RbaFsrDispatch) context = static_cast<Context*>(static_cast<RbaFsrDispatchDesc*>(data)->context);
    if (!context) return;
    std::lock_guard<std::mutex> lock(context->mutex);
    PendingPacket* consumed = nullptr;
    try {
        if (eventId == RbaFsrDestroy) context->Destroy();
        else {
            for (auto& packet : context->packets) if (&packet.frame == data && packet.busy) { consumed = &packet; break; }
            Require(consumed != nullptr, RbaFsrInvalidArgument, "FSR dispatch packet was not prepared or has already been consumed");
            context->Dispatch(consumed->frame);
        }
    } catch (const Failure& error) { context->Fail(error.result, error.what()); }
      catch (const std::exception& error) { context->Fail(RbaFsrDeviceError, error.what()); }
      catch (...) { context->Fail(RbaFsrDeviceError, "Unexpected native FSR failure"); }
    if (consumed) *consumed = PendingPacket{};
}
}

uint32_t RbaFsrGetAbiVersion() { return Abi; }
uint32_t RbaFsrCreate(const RbaFsrCreateDesc* description, void** context, char* error, uint32_t capacity) {
    if (context) *context = nullptr;
    try {
        Require(context != nullptr, RbaFsrInvalidArgument, "FSR context output is null");
        ValidateCreate(description);
        *context = new Context(*description);
        CopyText(error, capacity, "");
        return RbaFsrOk;
    } catch (const Failure& failure) { CopyText(error, capacity, failure.what()); return failure.result; }
      catch (const std::exception& failure) { CopyText(error, capacity, failure.what()); return RbaFsrRuntimeUnavailable; }
      catch (...) { CopyText(error, capacity, "Unexpected native FSR initialization failure"); return RbaFsrRuntimeUnavailable; }
}
uint32_t RbaFsrProbe(const RbaFsrCreateDesc* description, void* representativeTexture, RbaFsrStatus* status) {
    if (!status || status->structSize != sizeof(RbaFsrStatus)) return RbaFsrAbiMismatch;
    *status = {}; status->structSize = sizeof(*status); status->abiVersion = Abi;
    try {
        ValidateCreate(description);
        Require(representativeTexture != nullptr, RbaFsrInvalidArgument, "FSR probe needs an existing D3D11 texture");
        ComPtr<ID3D11Texture2D> texture;
        Check(static_cast<IUnknown*>(representativeTexture)->QueryInterface(IID_PPV_ARGS(&texture)), "Query FSR probe texture");
        Context context(*description);
        context.CreateDevice(texture.Get());
        *status = context.status;
        status->state = RbaFsrReady;
        // No commands were submitted, so context destruction needs no GPU wait.
        CheckFfx(context.runtime->functions.DestroyContext(&context.effect, nullptr), "Destroy FSR probe context");
        context.effect = nullptr;
        return RbaFsrOk;
    } catch (const Failure& failure) { status->result = failure.result; CopyText(status->error, sizeof(status->error), failure.what()); }
      catch (const std::exception& failure) { status->result = RbaFsrRuntimeUnavailable; CopyText(status->error, sizeof(status->error), failure.what()); }
      catch (...) { status->result = RbaFsrRuntimeUnavailable; CopyText(status->error, sizeof(status->error), "Unexpected FSR probe failure"); }
    status->state = RbaFsrFailed;
    return status->result;
}
void* RbaFsrGetRenderEventFunc() { return reinterpret_cast<void*>(&RenderEvent); }
uint32_t RbaFsrPrepareDispatch(const RbaFsrDispatchDesc* description, void** packet) {
    if (packet) *packet = nullptr;
    if (!description || !packet || !description->context || description->structSize != sizeof(*description) || description->abiVersion != Abi)
        return RbaFsrAbiMismatch;
    auto* context = static_cast<Context*>(description->context);
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state == RbaFsrDestroyed || context->status.state == RbaFsrFailed) return RbaFsrBusy;
    for (auto& entry : context->packets) {
        if (entry.busy) continue;
        entry.frame = *description;
        const std::array<void*, TextureCount> pointers = { description->color, description->depth, description->motionVectors,
            description->output, description->reactive, description->exposure, description->transparencyAndComposition };
        for (size_t i = 0; i < TextureCount; ++i) entry.references[i] = static_cast<IUnknown*>(pointers[i]);
        entry.busy = true;
        *packet = &entry.frame;
        return RbaFsrOk;
    }
    return RbaFsrBusy;
}
uint32_t RbaFsrGetStatus(void* context, RbaFsrStatus* status) {
    if (!context || !status || status->structSize != sizeof(RbaFsrStatus)) return RbaFsrAbiMismatch;
    auto* native = static_cast<Context*>(context);
    std::lock_guard<std::mutex> lock(native->mutex);
    *status = native->status;
    return RbaFsrOk;
}
uint32_t RbaFsrRelease(void* context) {
    if (!context) return RbaFsrOk;
    auto* native = static_cast<Context*>(context);
    {
        std::lock_guard<std::mutex> lock(native->mutex);
        if (native->status.state != RbaFsrDestroyed) return RbaFsrBusy;
    }
    delete native;
    return RbaFsrOk;
}

static_assert(sizeof(void*) == 8, "Bridge supports Windows x64 only");
static_assert(sizeof(RbaFsrCreateDesc) == 40, "Create ABI layout changed");
static_assert(sizeof(RbaFsrDispatchDesc) == 136, "Dispatch ABI layout changed");
static_assert(sizeof(RbaFsrStatus) == 680, "Status ABI layout changed");
static_assert(offsetof(RbaFsrCreateDesc, runtimeDirectory) == 32, "Create string pointer moved");
static_assert(offsetof(RbaFsrDispatchDesc, context) == 8 && offsetof(RbaFsrDispatchDesc, frameId) == 16,
    "Dispatch context/frame offsets changed");
static_assert(offsetof(RbaFsrDispatchDesc, color) == 24 && offsetof(RbaFsrDispatchDesc, jitterX) == 80 &&
    offsetof(RbaFsrDispatchDesc, reset) == 124, "Dispatch resource/parameter offsets changed");
static_assert(offsetof(RbaFsrStatus, state) == 8 && offsetof(RbaFsrStatus, result) == 12 &&
    offsetof(RbaFsrStatus, lastSubmittedFrameId) == 16 && offsetof(RbaFsrStatus, providerVersionId) == 24 &&
    offsetof(RbaFsrStatus, providerName) == 40 && offsetof(RbaFsrStatus, error) == 168, "Status field offsets changed");
