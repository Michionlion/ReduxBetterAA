#include "AmdFrameGenerationD3D11.h"
#include "FrameGenerationValidation.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <cstdio>
#include <memory>
#include <mutex>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
namespace {
struct Failure : std::runtime_error {
    uint32_t code;
    Failure(uint32_t value, const char* text) : std::runtime_error(text), code(value) {}
};
void Require(bool condition, uint32_t result, const char* text) { if (!condition) throw Failure(result, text); }
void Check(HRESULT result, const char* text) { Require(SUCCEEDED(result), RbaAmdFgDeviceError, text); }
struct Handle { HANDLE value = nullptr; ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
RbaAmdFgStatus Empty() { RbaAmdFgStatus s{}; s.structSize = sizeof(s); s.abiVersion = 1; return s; }
uint32_t Report(RbaAmdFgStatus* out, RbaAmdFgStatus status, uint32_t result, const char* text) {
    status.result = result; std::snprintf(status.error, sizeof(status.error), "%s", text ? text : "");
    if (out) *out = status; return result;
}
struct SharedTexture {
    ComPtr<ID3D12Resource> dx12;
    ComPtr<ID3D11Texture2D> dx11;
    void Create(ID3D12Device* device12, ID3D11Device5* device11, uint32_t width, uint32_t height, DXGI_FORMAT format) {
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        d.Width = width; d.Height = height; d.DepthOrArraySize = 1; d.MipLevels = 1; d.Format = format; d.SampleDesc.Count = 1;
        d.Flags = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        Check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &d, D3D12_RESOURCE_STATE_COMMON,
            nullptr, IID_PPV_ARGS(&dx12)), "Create shared FG texture failed");
        Handle handle;
        Check(device12->CreateSharedHandle(dx12.Get(), nullptr, GENERIC_ALL, nullptr, &handle.value), "Share FG texture failed");
        Check(device11->OpenSharedResource1(handle.value, IID_PPV_ARGS(&dx11)), "Open FG texture in D3D11 failed");
    }
};
struct Transport {
    enum class Phase { Idle, Computing, Copying, Failed };
    std::mutex mutex;
    RbaAmdFgD3D11CreateDesc description{};
    RbaAmdFgStatus status = Empty();
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> immediate;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence12;
    ComPtr<ID3D11Fence> fence11;
    ComPtr<ID3D11Fence> copyCompletion;
    std::array<SharedTexture, 5> shared;
    std::array<ComPtr<ID3D11Texture2D>, 5> retained;
    Handle completed;
    void* effect = nullptr;
    uint64_t nextFence = 0, retirement = 0, lastFrame = 0;
    bool unknownRetirement = false;
    bool destroying = false;
    DWORD renderThread = 0;
    Phase phase = Phase::Idle;
    explicit Transport(const RbaAmdFgD3D11CreateDesc& desc) : description(desc) {
        description.runtimeDirectory = nullptr; description.d3d11Device = nullptr;
        Check(static_cast<ID3D11Device*>(desc.d3d11Device)->QueryInterface(IID_PPV_ARGS(&device11)), "D3D11 device lacks shared-fence support");
        ComPtr<ID3D11DeviceContext> base; device11->GetImmediateContext(&base);
        Check(base.As(&immediate), "D3D11 immediate context lacks shared-fence support");
        ComPtr<IDXGIDevice> dxgi; Check(device11.As(&dxgi), "Get source DXGI device failed");
        ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter), "Get source GPU adapter failed");
        DXGI_ADAPTER_DESC adapterDesc{}; Check(adapter->GetDesc(&adapterDesc), "Describe source GPU failed");
        Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device12)), "Create same-adapter D3D12 FG device failed");
        const LUID luid = device12->GetAdapterLuid();
        Require(luid.LowPart == adapterDesc.AdapterLuid.LowPart && luid.HighPart == adapterDesc.AdapterLuid.HighPart,
            RbaAmdFgUnsupported, "D3D11/D3D12 FG adapter identities differ");
        D3D12_COMMAND_QUEUE_DESC q{}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device12->CreateCommandQueue(&q, IID_PPV_ARGS(&queue)), "Create FG interop queue failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12)), "Create FG interop fence failed");
        Handle handle; Check(device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle.value), "Share FG interop fence failed");
        Check(device11->OpenSharedFence(handle.value, IID_PPV_ARGS(&fence11)), "Open FG interop fence in D3D11 failed");
        Check(device11->CreateFence(0, D3D11_FENCE_FLAG_NONE, IID_PPV_ARGS(&copyCompletion)), "Create D3D11 FG retirement fence failed");
        completed.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        Require(completed.value != nullptr, RbaAmdFgDeviceError, "Create FG interop completion event failed");
        const std::array<DXGI_FORMAT, 5> formats{DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R32_FLOAT,
            DXGI_FORMAT_R16G16_FLOAT, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM};
        for (size_t i = 0; i < shared.size(); ++i) shared[i].Create(device12.Get(), device11.Get(),
            i == 1 || i == 2 ? desc.renderWidth : desc.displayWidth,
            i == 1 || i == 2 ? desc.renderHeight : desc.displayHeight, formats[i]);
        RbaAmdFgCreateDesc create{sizeof(create), 1, desc.runtimeDirectory, queue.Get(), desc.renderWidth, desc.renderHeight,
            desc.displayWidth, desc.displayHeight, desc.flags, desc.preferCompatibility};
        const uint32_t result = RbaAmdFgCreate(&create, &effect, &status);
        Require(result == RbaAmdFgOk, result, status.error);
    }
    // Only used before submission or after Destroy has proven both API queues retired.
    ~Transport() { if (effect) RbaAmdFgDestroy(effect); }
    uint32_t Fail(RbaAmdFgStatus* out, uint32_t result, const char* text) {
        if (result != RbaAmdFgInvalidArgument && result != RbaAmdFgGpuBusy && retained[0]) {
            // A terminal error after input submission poisons the frame/context.
            // Poll may still prove retirement, but it must never report valid output.
            phase = Phase::Failed; status.lastGeneratedFrames = 0;
            Report(&status, status, result, text);
        }
        return Report(out, status, result, text);
    }
    void CheckThread() {
        if (!renderThread) renderThread = GetCurrentThreadId();
        Require(renderThread == GetCurrentThreadId(), RbaAmdFgInvalidArgument, "FG D3D11 dispatch/poll must use the original render thread");
    }
    bool Removed() const {
        return FAILED(device11->GetDeviceRemovedReason()) || FAILED(device12->GetDeviceRemovedReason()) ||
            fence12->GetCompletedValue() == UINT64_MAX;
    }
    bool CopiesRemoved() const {
        return FAILED(device11->GetDeviceRemovedReason()) || copyCompletion->GetCompletedValue() == UINT64_MAX;
    }
    bool WaitCopies(bool wait) {
        const uint64_t deadline = GetTickCount64() + 5000;
        for (;;) {
            Require(!CopiesRemoved(), RbaAmdFgDeviceError, "D3D11 FG copy device was removed; output is invalid");
            if (unknownRetirement) return false;
            if (!retirement || copyCompletion->GetCompletedValue() >= retirement) return true;
            if (!wait || GetTickCount64() >= deadline) return false;
            Check(ResetEvent(completed.value) ? S_OK : E_FAIL, "Reset FG interop completion event failed");
            Check(copyCompletion->SetEventOnCompletion(retirement, completed.value), "Arm FG interop completion failed");
            const uint64_t now = GetTickCount64();
            if (now >= deadline) return false;
            const DWORD result = WaitForSingleObject(completed.value, static_cast<DWORD>(deadline - now));
            if (result == WAIT_TIMEOUT) return false;
            Check(result == WAIT_OBJECT_0 ? S_OK : E_FAIL, "Wait for FG interop copies failed");
        }
    }
    void SignalCopies() {
        retirement = ++nextFence; unknownRetirement = true;
        Check(immediate->Signal(fence11.Get(), retirement), "Signal D3D11 FG copies failed");
        Check(immediate->Signal(copyCompletion.Get(), retirement), "Signal D3D11 FG retirement failed");
        unknownRetirement = false; immediate->Flush();
    }
    void CopyOutput() {
        // Called only after the FG provider confirms its own D3D12 fence has completed.
        phase = Phase::Copying; unknownRetirement = true;
        immediate->CopyResource(retained[3].Get(), shared[3].dx11.Get());
        SignalCopies();
    }
    bool Complete(bool wait) {
        Require(!destroying, RbaAmdFgGpuBusy, "FG D3D11 transport destruction is in progress");
        Require(!Removed(), RbaAmdFgDeviceError, "FG interop device was removed; generated output is invalid");
        if (phase == Phase::Computing) {
            const uint32_t result = RbaAmdFgPoll(effect, &status);
            if (result == RbaAmdFgGpuBusy) return false;
            Require(result == RbaAmdFgOk && status.result == RbaAmdFgOk,
                result == RbaAmdFgOk ? status.result : result, status.error);
            CopyOutput();
        }
        if (!WaitCopies(wait)) return false;
        if (phase == Phase::Failed) throw Failure(status.result, status.error);
        retained = {}; phase = Phase::Idle; return true;
    }
    std::array<ComPtr<ID3D11Texture2D>, 5> Textures(const RbaAmdFgFrame& frame, void* hudLessColor) {
        Require(frame.structSize == sizeof(frame) && frame.abiVersion == 1 && frame.renderWidth == description.renderWidth &&
            frame.renderHeight == description.renderHeight, RbaAmdFgInvalidArgument, "FG D3D11 frame ABI/extent differs from transport");
        const std::array<void*, 5> pointers{frame.color, frame.depth, frame.motion, frame.output, hudLessColor};
        std::array<ComPtr<ID3D11Texture2D>, 5> textures;
        ComPtr<ID3D11Device> expected; Check(device11.As(&expected), "Get D3D11 device identity failed");
        for (size_t i = 0; i < pointers.size(); ++i) {
            if (i == 4 && !hudLessColor) continue;
            Require(pointers[i] != nullptr, RbaAmdFgInvalidArgument, "Missing D3D11 FG resource");
            Check(static_cast<IUnknown*>(pointers[i])->QueryInterface(IID_PPV_ARGS(&textures[i])), "Query D3D11 FG texture failed");
            ComPtr<ID3D11Device> actual; textures[i]->GetDevice(&actual);
            Require(actual.Get() == expected.Get(), RbaAmdFgInvalidArgument, "FG input/output belongs to another D3D11 device");
            D3D11_TEXTURE2D_DESC d{}; textures[i]->GetDesc(&d);
            Require(d.Width == (i == 1 || i == 2 ? description.renderWidth : description.displayWidth) &&
                d.Height == (i == 1 || i == 2 ? description.renderHeight : description.displayHeight) && d.MipLevels == 1 &&
                d.ArraySize == 1 && d.SampleDesc.Count == 1 && d.Usage == D3D11_USAGE_DEFAULT,
                RbaAmdFgInvalidArgument, "FG D3D11 textures require matching single-mip, single-sample default storage");
            const bool valid = i == 1 ? d.Format == DXGI_FORMAT_R32_FLOAT || d.Format == DXGI_FORMAT_R32_TYPELESS :
                i == 2 ? d.Format == DXGI_FORMAT_R16G16_FLOAT || d.Format == DXGI_FORMAT_R16G16_TYPELESS :
                d.Format == DXGI_FORMAT_R8G8B8A8_UNORM || d.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS || d.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            Require(valid, RbaAmdFgInvalidArgument, "FG D3D11 texture format differs from declared RGBA8/R32/RG16 contract");
        }
        Require(textures[3].Get() != textures[0].Get() && textures[3].Get() != textures[1].Get() && textures[3].Get() != textures[2].Get(),
            RbaAmdFgInvalidArgument, "FG D3D11 output must not alias inputs");
        Require(!hudLessColor || (textures[4].Get() != textures[0].Get() && textures[4].Get() != textures[3].Get()),
            RbaAmdFgInvalidArgument, "FG D3D11 HUD-less color must differ from final color and output");
        return textures;
    }
    uint32_t Dispatch(const RbaAmdFgFrame& frame, void* hudLessColor) {
        CheckThread(); Require(Complete(false), RbaAmdFgGpuBusy, "Previous FG interop frame is still pending");
        const char* invalid = rba_amd_fg::ValidateMetadata(frame, description.renderWidth, description.renderHeight, lastFrame);
        Require(invalid == nullptr, RbaAmdFgInvalidArgument, invalid);
        retained = Textures(frame, hudLessColor);
        // Mark unknown retirement before the first void-returning D3D11 submission.
        unknownRetirement = true;
        for (size_t i = 0; i < 3; ++i) immediate->CopyResource(shared[i].dx11.Get(), retained[i].Get());
        if (retained[4]) immediate->CopyResource(shared[4].dx11.Get(), retained[4].Get());
        SignalCopies();
        Check(queue->Wait(fence12.Get(), retirement), "Order D3D12 FG after D3D11 input copies failed");
        auto mapped = frame; mapped.color = shared[0].dx12.Get(); mapped.depth = shared[1].dx12.Get();
        mapped.motion = shared[2].dx12.Get(); mapped.output = shared[3].dx12.Get();
        phase = Phase::Computing;
        const uint64_t previousCount = status.submittedRealFrames;
        const uint32_t result = RbaAmdFgDispatchWithHudLess(effect, &mapped, hudLessColor ? shared[4].dx12.Get() : nullptr, &status);
        if (status.submittedRealFrames > previousCount) lastFrame = frame.frameId;
        if (result == RbaAmdFgGpuBusy) return result;
        if (result != RbaAmdFgOk) {
            // Metadata and caller resources were validated before any GPU copy;
            // a remaining native failure requires drained destruction/recreation.
            phase = Phase::Failed;
            status.lastGeneratedFrames = 0;
            if (result == RbaAmdFgInvalidArgument) { status.result = RbaAmdFgDispatchError; return RbaAmdFgDispatchError; }
            return result;
        }
        CopyOutput();
        if (!Complete(true)) return Report(&status, status, RbaAmdFgGpuBusy, "D3D11 generated-output copy is still pending");
        return RbaAmdFgOk;
    }
    uint32_t Destroy() {
        destroying = true;
        // Do not call D3D11 immediate-context methods off the render thread.
        // A late result can be discarded: only completion/lifetime is required.
        if (effect) {
            const uint32_t result = RbaAmdFgDestroy(effect);
            if (result != RbaAmdFgOk) return result;
            effect = nullptr;
        }
        if (!CopiesRemoved() && !WaitCopies(true)) return RbaAmdFgGpuBusy;
        retained = {}; return RbaAmdFgOk;
    }
};
}
uint32_t RbaAmdFgD3D11Create(const RbaAmdFgD3D11CreateDesc* desc, void** output, RbaAmdFgStatus* status) {
    if (output) *output = nullptr;
    try {
        Require(desc && output && desc->structSize == sizeof(*desc) && desc->abiVersion == 1 && desc->d3d11Device &&
            desc->reserved == 0 && desc->colorTransfer == RbaAmdFgSrgbEncoded && desc->renderWidth && desc->renderHeight &&
            desc->displayWidth >= desc->renderWidth && desc->displayHeight >= desc->renderHeight && desc->displayWidth <= 16384 &&
            desc->displayHeight <= 16384 && (desc->flags & ~(RbaAmdFgInvertedDepth | RbaAmdFgInfiniteDepth)) == 0 && desc->preferCompatibility <= 1,
            RbaAmdFgInvalidArgument, "Invalid D3D11 FG create ABI, extents or explicit sRGB encoding");
        auto context = std::make_unique<Transport>(*desc);
        if (status) *status = context->status; *output = context.release(); return RbaAmdFgOk;
    } catch (const Failure& e) { return Report(status, Empty(), e.code, e.what()); }
    catch (const std::exception& e) { return Report(status, Empty(), RbaAmdFgDeviceError, e.what()); }
    catch (...) { return Report(status, Empty(), RbaAmdFgDeviceError, "Unexpected FG interop creation failure"); }
}
uint32_t RbaAmdFgD3D11Dispatch(void* pointer, const RbaAmdFgFrame* frame, RbaAmdFgStatus* status) {
    return RbaAmdFgD3D11DispatchWithHudLess(pointer, frame, nullptr, status);
}
uint32_t RbaAmdFgD3D11DispatchWithHudLess(void* pointer, const RbaAmdFgFrame* frame, void* hudLessColor, RbaAmdFgStatus* status) {
    if (!pointer || !frame) return Report(status, Empty(), RbaAmdFgInvalidArgument, "Missing FG interop context/frame");
    auto& transport = *static_cast<Transport*>(pointer); std::lock_guard<std::mutex> lock(transport.mutex);
    try { const uint32_t result = transport.Dispatch(*frame, hudLessColor); if (status) *status = transport.status; return result; }
    catch (const Failure& e) { return transport.Fail(status, e.code, e.what()); }
    catch (const std::exception& e) { return transport.Fail(status, RbaAmdFgDeviceError, e.what()); }
    catch (...) { return transport.Fail(status, RbaAmdFgDeviceError, "Unexpected FG interop dispatch failure"); }
}
uint32_t RbaAmdFgD3D11Poll(void* pointer, RbaAmdFgStatus* status) {
    if (!pointer) return Report(status, Empty(), RbaAmdFgInvalidArgument, "Missing FG interop context");
    auto& transport = *static_cast<Transport*>(pointer); std::lock_guard<std::mutex> lock(transport.mutex);
    try {
        transport.CheckThread();
        if (!transport.Complete(false)) return Report(status, transport.status, RbaAmdFgGpuBusy, "FG interop GPU work is pending");
        transport.status.result = RbaAmdFgOk; transport.status.error[0] = 0;
        if (status) *status = transport.status; return RbaAmdFgOk;
    } catch (const Failure& e) { return transport.Fail(status, e.code, e.what()); }
    catch (...) { return transport.Fail(status, RbaAmdFgDeviceError, "FG interop completion query failed"); }
}
uint32_t RbaAmdFgD3D11Destroy(void* pointer) {
    if (!pointer) return RbaAmdFgOk;
    auto* transport = static_cast<Transport*>(pointer);
    try {
        { std::lock_guard<std::mutex> lock(transport->mutex); const uint32_t result = transport->Destroy(); if (result != RbaAmdFgOk) return result; }
        delete transport; return RbaAmdFgOk;
    } catch (...) { return RbaAmdFgDeviceError; }
}
static_assert(sizeof(RbaAmdFgD3D11CreateDesc) == 56, "D3D11 FG create ABI changed");
