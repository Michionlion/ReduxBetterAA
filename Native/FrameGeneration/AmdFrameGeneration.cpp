#include "AmdFrameGeneration.h"
#include "FrameGenerationValidation.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <ffx_api_loader.h>
#include <dx12/ffx_api_dx12.h>
#include <ffx_framegeneration.h>
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
constexpr DWORD WaitMilliseconds = 5000;
constexpr const wchar_t* RuntimeName = L"amd_fidelityfx_framegeneration_dx12.dll";
constexpr const char* RuntimeHash = "02297beedd285e822d3a64f314cf00faf378dcec0edc47ff0c4dd71b3a8c2f18";
struct Failure : std::runtime_error {
    uint32_t code;
    Failure(uint32_t c, const char* message) : std::runtime_error(message), code(c) {}
};
void Require(bool condition, uint32_t code, const char* message) { if (!condition) throw Failure(code, message); }
void Check(HRESULT result, const char* message) { Require(SUCCEEDED(result), RbaAmdFgDeviceError, message); }
void CheckFfx(ffxReturnCode_t result, const char* operation) {
    if (result == FFX_API_RETURN_OK) return;
    char message[256]{};
    std::snprintf(message, sizeof(message), "%s failed (FSR API %u)", operation, result);
    throw Failure(RbaAmdFgDispatchError, message);
}
void CopyText(char* destination, size_t capacity, const char* source) {
    std::snprintf(destination, capacity, "%s", source ? source : "");
}
RbaAmdFgStatus EmptyStatus() {
    RbaAmdFgStatus result{};
    result.structSize = sizeof(result); result.abiVersion = Abi;
    return result;
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
        Require(directory && *directory, RbaAmdFgInvalidArgument, "AMD FG runtime directory is required");
        const std::filesystem::path dir(directory);
        Require(dir.is_absolute(), RbaAmdFgInvalidArgument, "AMD FG runtime directory must be absolute");
        const auto path = std::filesystem::weakly_canonical(dir / RuntimeName);
        // Hold the verified DLL against replacement for its entire loaded lifetime.
        file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(file.value != INVALID_HANDLE_VALUE, RbaAmdFgRuntimeUnavailable, "Pinned AMD FG DLL is missing or locked");
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0,
            RbaAmdFgRuntimeUnavailable, "Could not initialize runtime SHA256");
        try {
            Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0,
                RbaAmdFgRuntimeUnavailable, "Could not create runtime SHA256");
            std::array<unsigned char, 65536> buffer{};
            DWORD count = 0;
            do {
                Require(ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()), &count, nullptr) != FALSE,
                    RbaAmdFgRuntimeUnavailable, "Could not read AMD FG DLL");
                if (count) Require(BCryptHashData(hash, buffer.data(), count, 0) >= 0,
                    RbaAmdFgRuntimeUnavailable, "Could not hash AMD FG DLL");
            } while (count);
            std::array<unsigned char, 32> digest{};
            Require(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0,
                RbaAmdFgRuntimeUnavailable, "Could not finish runtime SHA256");
            char hex[65]{};
            for (size_t i = 0; i < digest.size(); ++i) std::snprintf(hex + i * 2, 3, "%02x", digest[i]);
            Require(std::strcmp(hex, RuntimeHash) == 0, RbaAmdFgRuntimeUnavailable,
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
                RbaAmdFgRuntimeUnavailable, "Different AMD FG DLL is already loaded");
        }
        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(module != nullptr, RbaAmdFgRuntimeUnavailable, "Pinned AMD FG DLL could not be loaded");
        ffxLoadFunctions(&api, module);
        if (!api.CreateContext || !api.DestroyContext || !api.Query || !api.Dispatch || !api.Configure) {
            FreeLibrary(module); module = nullptr;
            throw Failure(RbaAmdFgRuntimeUnavailable, "Pinned AMD FG DLL lacks the required FSR API exports");
        }
    }
};
void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* texture, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition = {texture, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
    list->ResourceBarrier(1, &barrier);
}
struct Context {
    enum class Completion { Ready, Busy, Removed };
    std::mutex mutex;
    RbaAmdFgCreateDesc description{};
    RbaAmdFgStatus status = EmptyStatus();
    std::unique_ptr<Runtime> runtime;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    Handle completed;
    std::array<ComPtr<ID3D12Resource>, 5> retained;
    uint64_t fenceValue = 0, lastFrame = 0;
    bool pending = false, poisoned = false, submittedWithoutFence = false;
    bool previousHadHudLess = false;
    ffxContext effect = nullptr;
    // AMD requires creation descriptor storage to survive the context lifetime.
    ffxCreateContextDescFrameGeneration create{};
    ffxCreateContextDescFrameGenerationVersion version{};
    ffxCreateBackendDX12Desc backend{};
    ffxOverrideVersion overrideVersion{};

    explicit Context(const RbaAmdFgCreateDesc& desc) : description(desc) {
        description.runtimeDirectory = nullptr; description.commandQueue = nullptr;
        runtime = std::make_unique<Runtime>(desc.runtimeDirectory);
        queue = static_cast<ID3D12CommandQueue*>(desc.commandQueue);
        Check(queue->GetDevice(IID_PPV_ARGS(&device)), "Get FG queue device failed");
        Require(queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT, RbaAmdFgUnsupported, "AMD FG prototype requires a DIRECT queue");
        D3D12_FEATURE_DATA_SHADER_MODEL shader{D3D_SHADER_MODEL_6_2};
        Check(device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &shader, sizeof(shader)), "Shader model query failed");
        Require(shader.HighestShaderModel >= D3D_SHADER_MODEL_6_2, RbaAmdFgUnsupported, "AMD FG requires shader model 6.2 or newer");
        D3D12_FEATURE_DATA_FORMAT_SUPPORT format{DXGI_FORMAT_R16G16B16A16_UNORM};
        Check(device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &format, sizeof(format)), "Typed UAV query failed");
        Require((format.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD) != 0,
            RbaAmdFgUnsupported, "AMD FG requires R16G16B16A16_UNORM typed UAV loads");
        Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create FG allocator failed");
        Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)), "Create FG list failed");
        Check(list->Close(), "Close initial FG list failed");
        Check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)), "Create FG fence failed");
        completed.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        Require(completed.value != nullptr, RbaAmdFgDeviceError, "Create FG completion event failed");
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
        create.header.pNext = &version.header;
        create.displaySize = {desc.displayWidth, desc.displayHeight};
        create.maxRenderSize = {desc.maxRenderWidth, desc.maxRenderHeight};
        create.backBufferFormat = FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
        if (desc.flags & RbaAmdFgInvertedDepth) create.flags |= FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED;
        if (desc.flags & RbaAmdFgInfiniteDepth) create.flags |= FFX_FRAMEGENERATION_ENABLE_DEPTH_INFINITE;
        version.header = {FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION, &backend.header};
        version.version = FFX_FRAMEGENERATION_VERSION;
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = device.Get();
        if (desc.preferCompatibility) {
            uint64_t count = 0;
            ffxQueryDescGetVersions query{};
            query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
            query.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
            query.device = device.Get(); query.outputCount = &count;
            CheckFfx(runtime->api.Query(nullptr, &query.header), "Enumerate AMD FG providers");
            Require(count && count <= 64, RbaAmdFgUnsupported, "No bounded AMD FG provider enumeration");
            std::vector<uint64_t> ids(static_cast<size_t>(count));
            std::vector<const char*> names(static_cast<size_t>(count));
            query.versionIds = ids.data(); query.versionNames = names.data();
            CheckFfx(runtime->api.Query(nullptr, &query.header), "Read AMD FG providers");
            bool found = false;
            for (size_t i = 0; i < ids.size() && i < count; ++i) {
                if (names[i] && std::strstr(names[i], "3.1.")) {
                    overrideVersion.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
                    overrideVersion.versionId = ids[i]; backend.header.pNext = &overrideVersion.header;
                    found = true; break;
                }
            }
            Require(found, RbaAmdFgUnsupported, "SDK did not enumerate a supported FSR 3.1 FG provider");
        }
        try {
            CheckFfx(runtime->api.CreateContext(&effect, &create.header, nullptr), "Create AMD FG context");
            ffxQueryGetProviderVersion provider{};
            provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
            CheckFfx(runtime->api.Query(&effect, &provider.header), "Read active AMD FG provider");
            Require(provider.versionName && provider.versionId, RbaAmdFgUnsupported, "AMD FG provider identity unavailable");
            status.providerVersionId = provider.versionId;
            CopyText(status.providerName, sizeof(status.providerName), provider.versionName);
            status.maximumMultiplier = 2;
        } catch (...) { if (effect) runtime->api.DestroyContext(&effect, nullptr); throw; }
    }
    ~Context() { if (effect) runtime->api.DestroyContext(&effect, nullptr); }
    Completion Drain(bool wait) {
        const uint64_t deadline = GetTickCount64() + WaitMilliseconds;
        for (;;) {
            const auto value = fence->GetCompletedValue();
            if (value == UINT64_MAX || FAILED(device->GetDeviceRemovedReason())) {
                // Removal makes destruction safe, but never makes generated output valid.
                pending = false; retained = {}; poisoned = true;
                status.lastGeneratedFrames = 0;
                Fail(RbaAmdFgDeviceError, "AMD FG device was removed; generated output is invalid");
                return Completion::Removed;
            }
            if (!pending) return Completion::Ready;
            if (submittedWithoutFence) return Completion::Busy; // Cannot prove completion on a live device.
            if (value >= fenceValue) {
                pending = false; retained = {}; return Completion::Ready;
            }
            if (!wait) return Completion::Busy;
            const uint64_t now = GetTickCount64();
            if (now >= deadline) return Completion::Busy;
            // A prior timed-out submission may leave this event signaled after Poll
            // retires it. The event is only a wake-up hint; actual fence completion
            // must be checked again before releasing any current-frame resources.
            Check(ResetEvent(completed.value) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "Reset FG completion event failed");
            Check(fence->SetEventOnCompletion(fenceValue, completed.value), "Arm FG fence completion failed");
            const DWORD result = WaitForSingleObject(completed.value, static_cast<DWORD>(deadline - now));
            if (result == WAIT_TIMEOUT) return Completion::Busy;
            Check(result == WAIT_OBJECT_0 ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "Wait for FG completion failed");
        }
    }
    void Fail(uint32_t code, const char* error) {
        status.result = code; CopyText(status.error, sizeof(status.error), error);
    }
    void ValidateTexture(void* pointer, uint32_t width, uint32_t height, DXGI_FORMAT format, bool output) {
        Require(pointer != nullptr, RbaAmdFgInvalidArgument, "Missing AMD FG texture");
        auto* texture = static_cast<ID3D12Resource*>(pointer);
        ComPtr<ID3D12Device> owner;
        Check(texture->GetDevice(IID_PPV_ARGS(&owner)), "Get texture device failed");
        Require(owner.Get() == device.Get(), RbaAmdFgInvalidArgument, "AMD FG texture belongs to another D3D12 device");
        const auto d = texture->GetDesc();
        Require(d.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D && d.Width == width && d.Height == height &&
            d.DepthOrArraySize == 1 && d.MipLevels == 1 && d.SampleDesc.Count == 1 && d.Format == format,
            RbaAmdFgInvalidArgument, "AMD FG texture format/extent must match the frame contract");
        Require(!(d.Flags & D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE), RbaAmdFgInvalidArgument, "AMD FG inputs require shader access");
        Require(!output || (d.Flags & D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS),
            RbaAmdFgInvalidArgument, "AMD FG output requires UAV support");
    }
    void Dispatch(const RbaAmdFgFrame& f, void* hudLessColor) {
        const Completion prior = Drain(false);
        Require(prior != Completion::Removed, RbaAmdFgDeviceError, "AMD FG device was removed; recreate the context");
        Require(!poisoned, RbaAmdFgDispatchError, "Recreate AMD FG context after a native dispatch failure");
        Require(prior == Completion::Ready, RbaAmdFgGpuBusy, "Previous AMD FG work has not completed");
        const char* invalid = rba_amd_fg::ValidateMetadata(f, description.maxRenderWidth, description.maxRenderHeight, lastFrame);
        Require(invalid == nullptr, RbaAmdFgInvalidArgument, invalid);
        Require(f.color != f.output && f.depth != f.output && f.motion != f.output, RbaAmdFgInvalidArgument, "AMD FG output must not alias inputs");
        ValidateTexture(f.color, description.displayWidth, description.displayHeight, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        ValidateTexture(f.depth, f.renderWidth, f.renderHeight, DXGI_FORMAT_R32_FLOAT, false);
        ValidateTexture(f.motion, f.renderWidth, f.renderHeight, DXGI_FORMAT_R16G16_FLOAT, false);
        ValidateTexture(f.output, description.displayWidth, description.displayHeight, DXGI_FORMAT_R8G8B8A8_UNORM, true);
        if (hudLessColor) {
            Require(hudLessColor != f.color && hudLessColor != f.output, RbaAmdFgInvalidArgument,
                "AMD FG HUD-less color must differ from final color and output");
            ValidateTexture(hudLessColor, description.displayWidth, description.displayHeight, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        }
        const bool reset = f.reset || !status.submittedRealFrames || f.frameId != lastFrame + 1 ||
            previousHadHudLess != (hudLessColor != nullptr);
        ffxConfigureDescFrameGeneration config{};
        config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.frameGenerationEnabled = true;
        config.flags = FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
        config.frameID = f.frameId;
        config.generationRect = {0, 0, static_cast<int32_t>(description.displayWidth), static_cast<int32_t>(description.displayHeight)};
        if (hudLessColor) config.HUDLessColor = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(hudLessColor));
        try {
            // Required order for FSR 4: configure, prepare V2, generate. No swapchain notifications.
            CheckFfx(runtime->api.Configure(&effect, &config.header), "Configure AMD FG");
            Check(allocator->Reset(), "Reset AMD FG allocator failed");
            Check(list->Reset(allocator.Get(), nullptr), "Reset AMD FG list failed");
            retained = {static_cast<ID3D12Resource*>(f.color), static_cast<ID3D12Resource*>(f.depth),
                static_cast<ID3D12Resource*>(f.motion), static_cast<ID3D12Resource*>(f.output), static_cast<ID3D12Resource*>(hudLessColor)};
            for (size_t i = 0; i < 3; ++i) Transition(list.Get(), retained[i].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            if (retained[4]) Transition(list.Get(), retained[4].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            Transition(list.Get(), retained[3].Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            ffxDispatchDescFrameGenerationPrepareV2 prepare{};
            prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
            prepare.commandList = list.Get(); prepare.frameID = f.frameId;
            prepare.renderSize = {f.renderWidth, f.renderHeight};
            prepare.jitterOffset = {f.jitterX, f.jitterY}; prepare.motionVectorScale = {f.motionScaleX, f.motionScaleY};
            prepare.frameTimeDelta = f.frameTimeMilliseconds; prepare.reset = reset;
            prepare.cameraNear = f.cameraNear; prepare.cameraFar = f.cameraFar; prepare.cameraFovAngleVertical = f.cameraFovRadians;
            prepare.viewSpaceToMetersFactor = f.viewSpaceToMeters;
            prepare.depth = ffxApiGetResourceDX12(retained[1].Get()); prepare.motionVectors = ffxApiGetResourceDX12(retained[2].Get());
            std::memcpy(prepare.cameraPosition, f.cameraPosition, sizeof(f.cameraPosition));
            std::memcpy(prepare.cameraUp, f.cameraUp, sizeof(f.cameraUp));
            std::memcpy(prepare.cameraRight, f.cameraRight, sizeof(f.cameraRight));
            std::memcpy(prepare.cameraForward, f.cameraForward, sizeof(f.cameraForward));
            CheckFfx(runtime->api.Dispatch(&effect, &prepare.header), "Prepare AMD FG inputs");
            ffxDispatchDescFrameGeneration dispatch{};
            dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
            dispatch.commandList = list.Get(); dispatch.presentColor = ffxApiGetResourceDX12(retained[0].Get());
            dispatch.outputs[0] = ffxApiGetResourceDX12(retained[3].Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
            dispatch.numGeneratedFrames = 1; dispatch.reset = reset; dispatch.frameID = f.frameId;
            dispatch.backbufferTransferFunction = FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SRGB;
            dispatch.minMaxLuminance[0] = 0; dispatch.minMaxLuminance[1] = 100;
            dispatch.generationRect = config.generationRect;
            CheckFfx(runtime->api.Dispatch(&effect, &dispatch.header), "Generate AMD FG texture");
            for (size_t i = 0; i < 3; ++i) Transition(list.Get(), retained[i].Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
            if (retained[4]) Transition(list.Get(), retained[4].Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
            Transition(list.Get(), retained[3].Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
            Check(list->Close(), "Close AMD FG list failed");
            ID3D12CommandList* commands[] = {list.Get()};
            pending = true; submittedWithoutFence = true;
            queue->ExecuteCommandLists(1, commands);
            Check(queue->Signal(fence.Get(), ++fenceValue), "Signal AMD FG completion failed");
            submittedWithoutFence = false;
            status.lastGeneratedFrames = dispatch.numGeneratedFrames;
            ++status.submittedRealFrames; lastFrame = f.frameId;
            previousHadHudLess = hudLessColor != nullptr;
            const Completion completion = Drain(true);
            Require(completion != Completion::Removed, RbaAmdFgDeviceError, "AMD FG device was removed; generated output is invalid");
            Require(completion == Completion::Ready, RbaAmdFgGpuBusy, "AMD FG completion timed out; resources remain retained");
            status.result = RbaAmdFgOk; status.error[0] = 0;
        } catch (const Failure& failure) {
            if (failure.code != RbaAmdFgGpuBusy) { poisoned = true; status.lastGeneratedFrames = 0; }
            if (!pending) retained = {};
            throw;
        }
    }
};
uint32_t Report(RbaAmdFgStatus* out, RbaAmdFgStatus status, uint32_t result, const char* error) {
    status.result = result; CopyText(status.error, sizeof(status.error), error);
    if (out) *out = status;
    return result;
}
}

uint32_t RbaAmdFgCreate(const RbaAmdFgCreateDesc* desc, void** output, RbaAmdFgStatus* status) {
    if (output) *output = nullptr;
    try {
        Require(output && desc && desc->structSize == sizeof(*desc) && desc->abiVersion == Abi && desc->commandQueue,
            RbaAmdFgInvalidArgument, "Invalid AMD FG create ABI or queue");
        Require(desc->maxRenderWidth && desc->maxRenderHeight && desc->displayWidth >= desc->maxRenderWidth &&
            desc->displayHeight >= desc->maxRenderHeight && desc->displayWidth <= 16384 && desc->displayHeight <= 16384 &&
            (desc->flags & ~(RbaAmdFgInvertedDepth | RbaAmdFgInfiniteDepth)) == 0 && desc->preferCompatibility <= 1,
            RbaAmdFgInvalidArgument, "Invalid AMD FG create dimensions or flags");
        auto context = std::make_unique<Context>(*desc);
        if (status) *status = context->status;
        *output = context.release(); return RbaAmdFgOk;
    } catch (const Failure& error) { return Report(status, EmptyStatus(), error.code, error.what()); }
    catch (const std::exception& error) { return Report(status, EmptyStatus(), RbaAmdFgRuntimeUnavailable, error.what()); }
    catch (...) { return Report(status, EmptyStatus(), RbaAmdFgRuntimeUnavailable, "Unknown AMD FG creation failure"); }
}
uint32_t RbaAmdFgDispatch(void* pointer, const RbaAmdFgFrame* frame, RbaAmdFgStatus* status) {
    return RbaAmdFgDispatchWithHudLess(pointer, frame, nullptr, status);
}
uint32_t RbaAmdFgDispatchWithHudLess(void* pointer, const RbaAmdFgFrame* frame, void* hudLessColor, RbaAmdFgStatus* status) {
    if (!pointer) return Report(status, EmptyStatus(), RbaAmdFgInvalidArgument, "AMD FG context is null");
    auto& context = *static_cast<Context*>(pointer);
    std::lock_guard<std::mutex> lock(context.mutex);
    try {
        Require(frame && frame->structSize == sizeof(*frame) && frame->abiVersion == Abi,
            RbaAmdFgInvalidArgument, "Invalid AMD FG frame ABI");
        context.Dispatch(*frame, hudLessColor);
        if (status) *status = context.status;
        return RbaAmdFgOk;
    } catch (const Failure& error) { context.Fail(error.code, error.what()); }
    catch (const std::exception& error) { context.Fail(RbaAmdFgDispatchError, error.what()); }
    catch (...) { context.Fail(RbaAmdFgDispatchError, "Unknown AMD FG dispatch failure"); }
    if (status) *status = context.status;
    return context.status.result;
}
uint32_t RbaAmdFgPoll(void* pointer, RbaAmdFgStatus* status) {
    if (!pointer) return Report(status, EmptyStatus(), RbaAmdFgInvalidArgument, "AMD FG context is null");
    auto& context = *static_cast<Context*>(pointer);
    std::lock_guard<std::mutex> lock(context.mutex);
    try {
        const auto completion = context.Drain(false);
        if (completion == Context::Completion::Removed) return Report(status, context.status,
            RbaAmdFgDeviceError, "AMD FG device was removed; generated output is invalid");
        if (completion == Context::Completion::Busy) return Report(status, context.status, RbaAmdFgGpuBusy, "AMD FG GPU work remains pending");
        if (context.poisoned) return Report(status, context.status,
            context.status.result == RbaAmdFgOk ? RbaAmdFgDispatchError : context.status.result,
            "AMD FG context was poisoned by a native failure; destroy it after retirement");
        context.status.result = RbaAmdFgOk; context.status.error[0] = 0;
        if (status) *status = context.status;
        return RbaAmdFgOk;
    } catch (...) { return Report(status, context.status, RbaAmdFgDeviceError, "Could not query AMD FG completion"); }
}
uint32_t RbaAmdFgDestroy(void* pointer) {
    if (!pointer) return RbaAmdFgOk;
    auto* context = static_cast<Context*>(pointer);
    try {
        { std::lock_guard<std::mutex> lock(context->mutex);
          if (context->Drain(true) == Context::Completion::Busy) return RbaAmdFgGpuBusy; }
        delete context; return RbaAmdFgOk;
    } catch (...) { return RbaAmdFgDeviceError; }
}
