#include "FrameGenerationInputPool.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <cstdio>
#include <memory>
#include <mutex>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
static_assert(sizeof(RbaFgInputCreateDesc) == 48 && sizeof(RbaFgInputLease) == 32 &&
    sizeof(RbaFgInputResources) == 32 && sizeof(RbaFgInputStatus) == 304, "Input pool ABI1 layout changed");
namespace {
struct Failure : std::runtime_error {
    uint32_t code;
    Failure(uint32_t result, const char* text) : std::runtime_error(text), code(result) {}
};
void Require(bool value, uint32_t result, const char* text) { if (!value) throw Failure(result, text); }
void Check(HRESULT result, const char* text) { Require(SUCCEEDED(result), RbaFgInputDeviceError, text); }
struct Handle { HANDLE value = nullptr; ~Handle() { if (value) CloseHandle(value); } };
std::atomic<uint64_t> nextPool{0};
bool SameDevice(IUnknown* first, IUnknown* second) {
    ComPtr<IUnknown> a, b;
    Check(first->QueryInterface(IID_PPV_ARGS(&a)), "Query first device identity failed");
    Check(second->QueryInterface(IID_PPV_ARGS(&b)), "Query second device identity failed");
    return a.Get() == b.Get();
}
struct SharedTexture {
    ComPtr<ID3D12Resource> dx12;
    ComPtr<ID3D11Texture2D> dx11;
    void Create(ID3D12Device* device12, ID3D11Device5* device11, UINT width, UINT height, DXGI_FORMAT format) {
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc{}; desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width; desc.Height = height; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = format; desc.SampleDesc.Count = 1;
        // ALLOW_RENDER_TARGET is required by D3D11 OpenSharedResource1 on this
        // host; simultaneous access alone is not an importable copy-only texture.
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        Check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &desc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&dx12)), "Create shared input texture failed");
        Handle handle;
        Check(device12->CreateSharedHandle(dx12.Get(), nullptr, GENERIC_ALL, nullptr, &handle.value), "Share input texture failed");
        const HRESULT imported = device11->OpenSharedResource1(handle.value, IID_PPV_ARGS(&dx11));
        Require(!(format == DXGI_FORMAT_R32G32_FLOAT && imported == E_INVALIDARG), RbaFgInputUnsupported,
            "RG32 motion sharing is unsupported; convert source motion to RG16 before creating the pool");
        Check(imported, "Import input texture in D3D11 failed");
    }
};
struct Slot {
    std::array<SharedTexture, 4> textures;
    std::array<ComPtr<ID3D11Texture2D>, 4> sources;
    ComPtr<ID3D12Fence> consumerFence;
    RbaFgInputLease lease{};
    uint64_t sceneFence = 0, finalFence = 0, retirementFence = 0;
    bool sceneCopied = false, finalCopied = false, unknownScene = false, unknownFinal = false;
    bool sceneRetired = true, finalRetired = true, ready = false, readyDependencyQueued = false, retiring = false;
    bool unknown12 = false, cancelled = false, retired = true;
    void Reset(const RbaFgInputLease& token) {
        lease = token; sources = {}; consumerFence.Reset();
        sceneFence = finalFence = retirementFence = 0;
        sceneCopied = finalCopied = unknownScene = unknownFinal = ready = readyDependencyQueued = retiring = unknown12 = cancelled = false;
        sceneRetired = finalRetired = true; retired = false;
    }
};
struct Pool {
    std::mutex mutex;
    RbaFgInputCreateDesc desc{};
    uint64_t id = ++nextPool, nextSerial = 0, lastFrame = 0, next11 = 0, next12 = 0;
    DWORD copyThread = 0;
    bool stopping = false, poisoned = false;
    char error[256]{};
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> immediate;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> shared12, retired12;
    ComPtr<ID3D11Fence> shared11, retired11;
    std::array<Slot, 3> slots;
    explicit Pool(const RbaFgInputCreateDesc& description) : desc(description) {
        desc.d3d11Device = desc.d3d12Queue = nullptr;
        Check(static_cast<IUnknown*>(description.d3d11Device)->QueryInterface(IID_PPV_ARGS(&device11)), "D3D11 device lacks shared fences");
        ComPtr<ID3D11DeviceContext> base; device11->GetImmediateContext(&base);
        Check(base.As(&immediate), "D3D11 immediate context lacks fences");
        Check(static_cast<IUnknown*>(description.d3d12Queue)->QueryInterface(IID_PPV_ARGS(&queue)), "Invalid caller D3D12 queue");
        Require(queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT, RbaFgInputInvalidArgument, "Caller must supply a DIRECT queue");
        Check(queue->GetDevice(IID_PPV_ARGS(&device12)), "Get caller queue's D3D12 device failed");
        ComPtr<IDXGIDevice> dxgi; Check(device11.As(&dxgi), "Get source DXGI device failed");
        ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter), "Get source adapter failed");
        DXGI_ADAPTER_DESC adapterDesc{}; Check(adapter->GetDesc(&adapterDesc), "Describe source adapter failed");
        const auto luid = device12->GetAdapterLuid();
        Require(luid.LowPart == adapterDesc.AdapterLuid.LowPart && luid.HighPart == adapterDesc.AdapterLuid.HighPart,
            RbaFgInputInvalidArgument, "Source and caller D3D12 device must use the same actual adapter");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&shared12)), "Create shared copy fence failed");
        Handle handle;
        Check(device12->CreateSharedHandle(shared12.Get(), nullptr, GENERIC_ALL, nullptr, &handle.value), "Share copy fence failed");
        Check(device11->OpenSharedFence(handle.value, IID_PPV_ARGS(&shared11)), "Import D3D11 copy fence failed");
        Check(device11->CreateFence(0, D3D11_FENCE_FLAG_NONE, IID_PPV_ARGS(&retired11)), "Create private D3D11 retirement fence failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&retired12)), "Create private D3D12 retirement fence failed");
        const DXGI_FORMAT motion = desc.motionFormat == RbaFgInputMotionRg16Float ? DXGI_FORMAT_R16G16_FLOAT : DXGI_FORMAT_R32G32_FLOAT;
        const std::array<DXGI_FORMAT, 4> formats{DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R32_FLOAT, motion};
        for (auto& slot : slots) for (size_t i = 0; i < formats.size(); ++i)
            slot.textures[i].Create(device12.Get(), device11.Get(), i < 2 ? desc.displayWidth : desc.renderWidth,
                i < 2 ? desc.displayHeight : desc.renderHeight, formats[i]);
    }
    void Poison(const char* text) { poisoned = true; std::snprintf(error, sizeof(error), "%s", text); }
    bool Removed11() const { return FAILED(device11->GetDeviceRemovedReason()) || retired11->GetCompletedValue() == UINT64_MAX; }
    bool Removed12() const { return FAILED(device12->GetDeviceRemovedReason()) || retired12->GetCompletedValue() == UINT64_MAX; }
    void Poll() {
        const bool removed11 = Removed11(), removed12 = Removed12();
        if (removed11 || removed12) Poison("A source or provider device was removed; inputs are invalid");
        const auto completed11 = removed11 ? UINT64_MAX : retired11->GetCompletedValue();
        const auto completed12 = removed12 ? UINT64_MAX : retired12->GetCompletedValue();
        for (auto& slot : slots) {
            if (slot.retired) continue;
            if (removed11 || (!slot.unknownScene && completed11 >= slot.sceneFence)) {
                slot.sceneRetired = true;
                for (size_t i = 1; i < 4; ++i) slot.sources[i].Reset();
            }
            if (removed11 || (!slot.unknownFinal && completed11 >= slot.finalFence)) {
                slot.finalRetired = true; slot.sources[0].Reset();
            }
            const bool consumerDone = !slot.ready ? slot.cancelled : removed12 ||
                (slot.retiring && !slot.unknown12 && completed12 >= slot.retirementFence);
            // Private API-specific fences matter: loss of one shared-fence device
            // does not prove work on the other device has stopped using resources.
            if (slot.sceneRetired && slot.finalRetired && consumerDone) {
                slot.retired = true; slot.consumerFence.Reset();
            }
        }
    }
    uint32_t Report(RbaFgInputStatus* output, uint32_t result, const char* text = nullptr, const Slot* slot = nullptr) const {
        RbaFgInputStatus status{}; status.structSize = sizeof(status); status.abiVersion = 1; status.result = result;
        for (const auto& item : slots) if (item.retired) ++status.freeSlots;
        status.poisoned = poisoned;
        if (slot) {
            status.sceneCopied = slot->sceneCopied; status.finalCopied = slot->finalCopied;
            status.sceneSourcesRetired = slot->sceneRetired; status.finalSourceRetired = slot->finalRetired;
            status.readyQueued = slot->readyDependencyQueued;
            status.retirementQueued = slot->retiring && !slot->unknown12; status.retired = slot->retired;
        }
        std::snprintf(status.error, sizeof(status.error), "%s", text ? text : poisoned ? error : "");
        if (output) *output = status; return result;
    }
    Slot& Find(const RbaFgInputLease* token) {
        Require(token && token->poolId == id && token->slot < slots.size() && token->reserved == 0,
            RbaFgInputInvalidArgument, "Invalid input-pool lease owner or slot");
        auto& slot = slots[token->slot];
        Require(token->serial != 0 && token->serial == slot.lease.serial && token->realFrameId == slot.lease.realFrameId,
            RbaFgInputInvalidArgument, "Expired or foreign input-pool lease");
        return slot;
    }
    void Operational() {
        Poll(); Require(!poisoned, RbaFgInputDeviceError, error);
        Require(!stopping, RbaFgInputBusy, "Input pool is stopping");
    }
    void CheckThread() {
        if (!copyThread) copyThread = GetCurrentThreadId();
        Require(copyThread == GetCurrentThreadId(), RbaFgInputInvalidArgument, "Input copies must use the original D3D11 render thread");
    }
    ComPtr<ID3D11Texture2D> Validate(void* pointer, size_t index) {
        Require(pointer != nullptr, RbaFgInputInvalidArgument, "Missing source texture");
        ComPtr<ID3D11Texture2D> texture;
        Require(SUCCEEDED(static_cast<IUnknown*>(pointer)->QueryInterface(IID_PPV_ARGS(&texture))), RbaFgInputInvalidArgument, "Source is not a D3D11 texture");
        ComPtr<ID3D11Device> sourceDevice; texture->GetDevice(&sourceDevice);
        Require(SameDevice(sourceDevice.Get(), device11.Get()), RbaFgInputInvalidArgument, "Source texture belongs to another D3D11 device");
        D3D11_TEXTURE2D_DESC source{}; texture->GetDesc(&source);
        Require(source.Width == (index < 2 ? desc.displayWidth : desc.renderWidth) &&
            source.Height == (index < 2 ? desc.displayHeight : desc.renderHeight) && source.MipLevels == 1 && source.ArraySize == 1 &&
            source.SampleDesc.Count == 1 && source.SampleDesc.Quality == 0 && source.Usage == D3D11_USAGE_DEFAULT && source.CPUAccessFlags == 0,
            RbaFgInputInvalidArgument, "Source requires matching single-mip/sample DEFAULT texture storage");
        const auto format = source.Format;
        const bool compatible = index < 2 ? format == DXGI_FORMAT_R8G8B8A8_UNORM || format == DXGI_FORMAT_R8G8B8A8_TYPELESS || format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB :
            index == 2 ? format == DXGI_FORMAT_R32_FLOAT || format == DXGI_FORMAT_R32_TYPELESS :
            desc.motionFormat == RbaFgInputMotionRg16Float ? format == DXGI_FORMAT_R16G16_FLOAT || format == DXGI_FORMAT_R16G16_TYPELESS :
            format == DXGI_FORMAT_R32G32_FLOAT || format == DXGI_FORMAT_R32G32_TYPELESS;
        Require(compatible, RbaFgInputInvalidArgument, "Source format differs from the declared input contract");
        return texture;
    }
    void SignalCopies(uint64_t value) {
        Check(immediate->Signal(shared11.Get(), value), "Signal shared input-copy fence failed");
        Check(immediate->Signal(retired11.Get(), value), "Signal private source-retirement fence failed");
        immediate->Flush();
    }
};
uint32_t Empty(RbaFgInputStatus* output, uint32_t result, const char* text) {
    RbaFgInputStatus status{}; status.structSize = sizeof(status); status.abiVersion = 1; status.result = result;
    std::snprintf(status.error, sizeof(status.error), "%s", text); if (output) *output = status; return result;
}
template<class F> uint32_t Call(void* pointer, const RbaFgInputLease* token, RbaFgInputStatus* status, F&& action) {
    if (!pointer) return Empty(status, RbaFgInputInvalidArgument, "Missing input pool");
    auto& pool = *static_cast<Pool*>(pointer); std::lock_guard<std::mutex> lock(pool.mutex); Slot* slot = nullptr;
    try {
        if (token) slot = &pool.Find(token);
        auto result = action(pool, slot); pool.Poll();
        if (result == RbaFgInputOk && pool.poisoned) result = RbaFgInputDeviceError;
        return pool.Report(status, result, nullptr, slot);
    }
    catch (const Failure& failure) {
        if (failure.code == RbaFgInputDeviceError) pool.Poison(failure.what());
        pool.Poll(); return pool.Report(status, failure.code, failure.what(), slot);
    }
    catch (...) { pool.Poison("Unexpected input transport failure"); return pool.Report(status, RbaFgInputDeviceError, nullptr, slot); }
}
}

uint32_t RbaFgInputCreate(const RbaFgInputCreateDesc* desc, void** output, RbaFgInputStatus* status) {
    if (output) *output = nullptr;
    try {
        Require(desc && output && desc->structSize == sizeof(*desc) && desc->abiVersion == 1 && desc->d3d11Device && desc->d3d12Queue &&
            desc->renderWidth && desc->renderHeight && desc->displayWidth >= desc->renderWidth && desc->displayHeight >= desc->renderHeight &&
            desc->displayWidth <= 16384 && desc->displayHeight <= 16384 && desc->colorTransfer == RbaFgInputSrgbEncoded &&
            (desc->motionFormat == RbaFgInputMotionRg16Float || desc->motionFormat == RbaFgInputMotionRg32Float),
            RbaFgInputInvalidArgument, "Invalid input-pool ABI, devices, extents or format metadata");
        auto pool = std::make_unique<Pool>(*desc);
        pool->Report(status, RbaFgInputOk); *output = pool.release(); return RbaFgInputOk;
    }
    catch (const Failure& failure) { return Empty(status, failure.code, failure.what()); }
    catch (...) { return Empty(status, RbaFgInputDeviceError, "Input-pool creation failed"); }
}
uint32_t RbaFgInputAcquire(void* pointer, uint64_t frame, RbaFgInputLease* lease, RbaFgInputStatus* status) {
    if (lease) *lease = {};
    return Call(pointer, nullptr, status, [&](Pool& pool, Slot*) {
        pool.Operational(); Require(lease && frame > pool.lastFrame, RbaFgInputInvalidArgument, "Acquisition requires a new increasing real-frame ID");
        for (uint32_t i = 0; i < pool.slots.size(); ++i) if (pool.slots[i].retired) {
            *lease = {pool.id, ++pool.nextSerial, frame, i, 0}; pool.slots[i].Reset(*lease); pool.lastFrame = frame; return RbaFgInputOk;
        }
        return RbaFgInputBusy;
    });
}
uint32_t RbaFgInputCopyScene(void* pointer, const RbaFgInputLease* lease, void* color, void* depth, void* motion, RbaFgInputStatus* status) {
    if (!lease) return Empty(status, RbaFgInputInvalidArgument, "Missing input lease");
    return Call(pointer, lease, status, [&](Pool& pool, Slot* slot) {
        pool.Operational(); pool.CheckThread();
        Require(!slot->retired && !slot->cancelled && !slot->ready && !slot->sceneCopied, RbaFgInputInvalidArgument, "Scene inputs already copied or lease closed");
        std::array<ComPtr<ID3D11Texture2D>, 3> sources{pool.Validate(color, 1), pool.Validate(depth, 2), pool.Validate(motion, 3)};
        for (size_t i = 0; i < 3; ++i) slot->sources[i + 1] = sources[i];
        slot->sceneCopied = true; slot->sceneRetired = false; slot->unknownScene = true; slot->sceneFence = ++pool.next11;
        for (size_t i = 0; i < 3; ++i) pool.immediate->CopyResource(slot->textures[i + 1].dx11.Get(), sources[i].Get());
        pool.SignalCopies(slot->sceneFence); slot->unknownScene = false; return RbaFgInputOk;
    });
}
uint32_t RbaFgInputCopyFinal(void* pointer, const RbaFgInputLease* lease, void* color, RbaFgInputStatus* status) {
    if (!lease) return Empty(status, RbaFgInputInvalidArgument, "Missing input lease");
    return Call(pointer, lease, status, [&](Pool& pool, Slot* slot) {
        pool.Operational(); pool.CheckThread();
        Require(!slot->retired && !slot->cancelled && !slot->ready && !slot->finalCopied, RbaFgInputInvalidArgument, "Final input already copied or lease closed");
        auto source = pool.Validate(color, 0);
        slot->sources[0] = source; slot->finalCopied = true; slot->finalRetired = false; slot->unknownFinal = true; slot->finalFence = ++pool.next11;
        pool.immediate->CopyResource(slot->textures[0].dx11.Get(), source.Get());
        pool.SignalCopies(slot->finalFence); slot->unknownFinal = false; return RbaFgInputOk;
    });
}
uint32_t RbaFgInputQueueReady(void* pointer, const RbaFgInputLease* lease, RbaFgInputResources* resources, RbaFgInputStatus* status) {
    if (resources) *resources = {};
    if (!lease) return Empty(status, RbaFgInputInvalidArgument, "Missing input lease");
    return Call(pointer, lease, status, [&](Pool& pool, Slot* slot) {
        pool.Operational();
        Require(resources && !slot->retired && !slot->cancelled && !slot->ready && slot->sceneCopied && slot->finalCopied,
            RbaFgInputInvalidArgument, "Ready requires both copied inputs and an unconsumed lease");
        slot->ready = true; slot->unknown12 = true;
        const auto value = slot->sceneFence > slot->finalFence ? slot->sceneFence : slot->finalFence;
        Check(pool.queue->Wait(pool.shared12.Get(), value), "Queue caller device after D3D11 copies failed");
        // No consumer retirement is claimed here: provider work has not happened.
        slot->unknown12 = false; slot->readyDependencyQueued = true;
        *resources = {slot->textures[0].dx12.Get(), slot->textures[1].dx12.Get(), slot->textures[2].dx12.Get(), slot->textures[3].dx12.Get()};
        return RbaFgInputOk;
    });
}
uint32_t RbaFgInputRetire(void* pointer, const RbaFgInputLease* lease, void* fence, uint64_t value, RbaFgInputStatus* status) {
    if (!lease) return Empty(status, RbaFgInputInvalidArgument, "Missing input lease");
    return Call(pointer, lease, status, [&](Pool& pool, Slot* slot) {
        pool.Poll();
        Require(!slot->retired && slot->ready && !slot->retiring && fence && value && value != UINT64_MAX,
            RbaFgInputInvalidArgument, "Retirement requires a consumed lease and actual completion fence/value");
        ComPtr<ID3D12Fence> completion;
        Require(SUCCEEDED(static_cast<IUnknown*>(fence)->QueryInterface(IID_PPV_ARGS(&completion))), RbaFgInputInvalidArgument, "Completion is not a D3D12 fence");
        ComPtr<ID3D12Device> actual; Check(completion->GetDevice(IID_PPV_ARGS(&actual)), "Get consumer fence device failed");
        Require(SameDevice(actual.Get(), pool.device12.Get()), RbaFgInputInvalidArgument, "Consumer fence belongs to another D3D12 device");
        slot->consumerFence = completion; slot->retiring = true; slot->unknown12 = true; slot->retirementFence = ++pool.next12;
        Check(pool.queue->Wait(completion.Get(), value), "Wait for all provider input consumers failed");
        Check(pool.queue->Signal(pool.retired12.Get(), slot->retirementFence), "Signal private D3D12 retirement failed");
        slot->unknown12 = false; return RbaFgInputOk;
    });
}
uint32_t RbaFgInputCancel(void* pointer, const RbaFgInputLease* lease, RbaFgInputStatus* status) {
    if (!lease) return Empty(status, RbaFgInputInvalidArgument, "Missing input lease");
    return Call(pointer, lease, status, [](Pool& pool, Slot* slot) {
        Require(!slot->ready, RbaFgInputInvalidArgument, "Consumed input lease requires provider retirement");
        slot->cancelled = true; pool.Poll(); return slot->retired ? RbaFgInputOk : RbaFgInputBusy;
    });
}
uint32_t RbaFgInputQueryLease(void* pointer, const RbaFgInputLease* lease, RbaFgInputStatus* status) {
    if (!lease) return Empty(status, RbaFgInputInvalidArgument, "Missing input lease");
    return Call(pointer, lease, status, [](Pool& pool, Slot* slot) {
        pool.Poll(); return pool.poisoned ? RbaFgInputDeviceError : slot->retired ? RbaFgInputOk : RbaFgInputBusy;
    });
}
uint32_t RbaFgInputDestroy(void* pointer, RbaFgInputStatus* status) {
    if (!pointer) return Empty(status, RbaFgInputOk, "");
    auto* pool = static_cast<Pool*>(pointer);
    std::unique_lock<std::mutex> lock(pool->mutex);
    pool->stopping = true;
    for (auto& slot : pool->slots) if (!slot.ready) slot.cancelled = true;
    pool->Poll();
    for (const auto& slot : pool->slots) if (!slot.retired) return pool->Report(status, RbaFgInputBusy, "Input leases or uncertain GPU work remain alive");
    pool->Report(status, RbaFgInputOk);
    lock.unlock(); delete pool; return RbaFgInputOk;
}
