#include "CompositionPresenter.h"
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <dcomp.h>
#include <wrl/client.h>
#include <array>
#include <memory>

using Microsoft::WRL::ComPtr;
namespace
{
    struct Failure { HRESULT result; };
    void Check(HRESULT result) { if (FAILED(result)) throw Failure{result}; }
    void Require(bool condition) { if (!condition) throw Failure{DXGI_ERROR_UNSUPPORTED}; }
    struct Handle
    {
        HANDLE value = nullptr;
        ~Handle() { if (value != nullptr) CloseHandle(value); }
    };
    DXGI_FORMAT StorageFormat(DXGI_FORMAT value)
    {
        if (value == DXGI_FORMAT_R8G8B8A8_UNORM || value == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || value == DXGI_FORMAT_R8G8B8A8_TYPELESS)
            return DXGI_FORMAT_R8G8B8A8_UNORM;
        if (value == DXGI_FORMAT_B8G8R8A8_UNORM || value == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || value == DXGI_FORMAT_B8G8R8A8_TYPELESS)
            return DXGI_FORMAT_B8G8R8A8_UNORM;
        return DXGI_FORMAT_UNKNOWN;
    }
    template<class Fence> bool Wait(Fence* fence, uint64_t value, HANDLE event)
    {
        if (value == 0) return true;
        const auto deadline = GetTickCount64() + 5000;
        for (;;)
        {
            const auto done = fence->GetCompletedValue();
            if (done == UINT64_MAX) return true; // Removed device has retired access, not valid output.
            if (done >= value) return true;
            if (GetTickCount64() >= deadline) return false;
            Check(ResetEvent(event) ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
            Check(fence->SetEventOnCompletion(value, event));
            const auto now = GetTickCount64();
            if (now >= deadline) return false;
            const auto result = WaitForSingleObject(event, static_cast<DWORD>(deadline - now));
            if (result == WAIT_TIMEOUT) return false;
            Check(result == WAIT_OBJECT_0 ? S_OK : E_FAIL);
            // An older timed-out wait may wake this event: always re-read fence.
        }
    }
    D3D12_RESOURCE_BARRIER Barrier(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = resource; b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = before; b.Transition.StateAfter = after; return b;
    }
}

struct CompositionPresenter::Impl
{
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> context11;
    ComPtr<ID3D11Fence> ready11, retired11;
    ComPtr<ID3D11Texture2D> shared11, retainedSource;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<ID3D12Fence> ready12, retired12;
    ComPtr<ID3D12Resource> shared12;
    std::array<ComPtr<ID3D12Resource>, 2> buffers;
    ComPtr<IDXGISwapChain3> chain;
    ComPtr<IDCompositionDesktopDevice> composition;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual2> visual;
    Handle event;
    DXGI_SWAP_CHAIN_DESC sourceDesc{};
    uintptr_t sourceIdentity = 0;
    uint64_t serial = 0, pending11 = 0, pending12 = 0;
    bool unknown11 = false, unknown12 = false, attached = false;
#ifdef RBA_COMPOSITION_TESTING
    bool failNextDetach = false;
    ComPtr<ID3D12Resource> retainedReadback;
#endif

    bool Retire()
    {
        // A separate 11 fence is necessary: 12 removal alone does not retire
        // Unity's independent 11 copy queue or allow its backbuffer release.
        const bool removed11 = FAILED(device11->GetDeviceRemovedReason());
        const bool removed12 = FAILED(device12->GetDeviceRemovedReason());
        if (!removed11 && (unknown11 || !Wait(retired11.Get(), pending11, event.value))) return false;
        if (!removed12 && (unknown12 || !Wait(retired12.Get(), pending12, event.value))) return false;
        retainedSource.Reset(); pending11 = pending12 = 0;
#ifdef RBA_COMPOSITION_TESTING
        retainedReadback.Reset();
#endif
        return true;
    }
    bool Detach()
    {
#ifdef RBA_COMPOSITION_TESTING
        if (failNextDetach) { failNextDetach = false; throw Failure{E_FAIL}; }
#endif
        if (attached)
        {
            Check(target->SetRoot(nullptr));
            Check(composition->Commit());
            // This waits for DComp to process the detach, not for displayed FPS.
            Check(composition->WaitForCommitCompletion());
            attached = false;
        }
        return true;
    }
    void Create(ID3D11Device* device, IDXGISwapChain* source, const DXGI_SWAP_CHAIN_DESC& desc, uint32_t& operation)
    {
        sourceDesc = desc; sourceIdentity = reinterpret_cast<uintptr_t>(source);
        operation = 1;
        Check(device->QueryInterface(IID_PPV_ARGS(&device11)));
        ComPtr<ID3D11DeviceContext> immediate; device->GetImmediateContext(&immediate); Check(immediate.As(&context11));
        ComPtr<IDXGIDevice> dxgi; Check(device11.As(&dxgi));
        ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter));
        DXGI_ADAPTER_DESC adapterDesc{}; Check(adapter->GetDesc(&adapterDesc));
        operation = 2;
        Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device12)));
        const auto luid = device12->GetAdapterLuid();
        Require(luid.LowPart == adapterDesc.AdapterLuid.LowPart && luid.HighPart == adapterDesc.AdapterLuid.HighPart);
        D3D12_COMMAND_QUEUE_DESC q{}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        operation = 3;
        Check(device12->CreateCommandQueue(&q, IID_PPV_ARGS(&queue)));
        Check(device12->CreateCommandAllocator(q.Type, IID_PPV_ARGS(&allocator)));
        Check(device12->CreateCommandList(0, q.Type, allocator.Get(), nullptr, IID_PPV_ARGS(&commands)));
        Check(commands->Close());
        operation = 4;
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&ready12)));
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&retired12)));
        Check(device11->CreateFence(0, D3D11_FENCE_FLAG_NONE, IID_PPV_ARGS(&retired11)));
        Handle handle;
        Check(device12->CreateSharedHandle(ready12.Get(), nullptr, GENERIC_ALL, nullptr, &handle.value));
        Check(device11->OpenSharedFence(handle.value, IID_PPV_ARGS(&ready11)));
        event.value = CreateEventW(nullptr, FALSE, FALSE, nullptr); Check(event.value ? S_OK : E_FAIL);
        operation = 5;
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC texture{}; texture.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        texture.Width = desc.BufferDesc.Width; texture.Height = desc.BufferDesc.Height;
        texture.DepthOrArraySize = texture.MipLevels = 1; texture.Format = StorageFormat(desc.BufferDesc.Format);
        texture.SampleDesc.Count = 1;
        // D3D11 sharing requires a bindable texture; a copy-only 12 resource
        // (no RT flag) is rejected by OpenSharedResource1 with E_INVALIDARG.
        texture.Flags = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        Check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &texture, D3D12_RESOURCE_STATE_COMMON,
            nullptr, IID_PPV_ARGS(&shared12)));
        Handle textureHandle; Check(device12->CreateSharedHandle(shared12.Get(), nullptr, GENERIC_ALL, nullptr, &textureHandle.value));
        operation = 6;
        Check(device11->OpenSharedResource1(textureHandle.value, IID_PPV_ARGS(&shared11)));
        ComPtr<IDXGIFactory2> factory; Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
        operation = 7;
        DXGI_SWAP_CHAIN_DESC1 d{}; d.Width = desc.BufferDesc.Width; d.Height = desc.BufferDesc.Height;
        d.Format = texture.Format; d.SampleDesc.Count = 1; d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        d.BufferCount = 2; d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; d.Scaling = DXGI_SCALING_STRETCH;
        d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> base; Check(factory->CreateSwapChainForComposition(queue.Get(), &d, nullptr, &base));
        Check(base.As(&chain));
        for (UINT i = 0; i < buffers.size(); ++i) Check(chain->GetBuffer(i, IID_PPV_ARGS(&buffers[i])));
        operation = 8;
        Check(DCompositionCreateDevice2(nullptr, IID_PPV_ARGS(&composition)));
        operation = 9;
        // Reserves the topmost composition layer. An occupied target must fail;
        // we never replace another component's target or create a second HWND.
        Check(composition->CreateTargetForHwnd(desc.OutputWindow, TRUE, &target));
        operation = 10;
        Check(composition->CreateVisual(&visual)); Check(visual->SetContent(chain.Get()));
        // No SetRoot/Commit yet: preparation must not hide Unity before CAS.
    }
    void Copy(IDXGISwapChain* source)
    {
        Require(Retire());
        // D3D11 uses logical buffer zero. Unity's normal Present rotates its
        // handles; suppressing that Present leaves its render target at zero.
        Check(source->GetBuffer(0, IID_PPV_ARGS(&retainedSource)));
        D3D11_TEXTURE2D_DESC d{}; retainedSource->GetDesc(&d);
        Require(d.Width == sourceDesc.BufferDesc.Width && d.Height == sourceDesc.BufferDesc.Height &&
            d.MipLevels == 1 && d.ArraySize == 1 && d.SampleDesc.Count == 1 &&
            d.Usage == D3D11_USAGE_DEFAULT && StorageFormat(d.Format) == StorageFormat(sourceDesc.BufferDesc.Format));
        ComPtr<ID3D11Device> actual; retainedSource->GetDevice(&actual);
        ComPtr<ID3D11Device> expected; Check(device11.As(&expected)); Require(actual.Get() == expected.Get());
        const auto value = ++serial;
        unknown11 = true;
        context11->CopyResource(shared11.Get(), retainedSource.Get());
        Check(context11->Signal(retired11.Get(), value)); pending11 = value; unknown11 = false;
        const auto signal = context11->Signal(ready11.Get(), value);
        context11->Flush(); Check(signal);
        // Prove original buffer retirement before a return can permit resize.
        Require(Wait(retired11.Get(), pending11, event.value));
        Check(device11->GetDeviceRemovedReason()); retainedSource.Reset(); pending11 = 0;
        Check(queue->Wait(ready12.Get(), value));
        Check(allocator->Reset()); Check(commands->Reset(allocator.Get(), nullptr));
        auto* buffer = buffers[chain->GetCurrentBackBufferIndex()].Get();
        std::array<D3D12_RESOURCE_BARRIER, 2> barriers{
            Barrier(shared12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
            Barrier(buffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST)};
        commands->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
        commands->CopyResource(buffer, shared12.Get());
        barriers = {Barrier(shared12.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
            Barrier(buffer, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT)};
        commands->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data()); Check(commands->Close());
        ID3D12CommandList* lists[]{commands.Get()}; unknown12 = true; queue->ExecuteCommandLists(1, lists);
        Check(queue->Signal(retired12.Get(), value)); pending12 = value; unknown12 = false;
        Require(Wait(retired12.Get(), value, event.value)); Check(device12->GetDeviceRemovedReason()); pending12 = 0;
    }
};

CompositionPresenter::CompositionPresenter()
{
    status_.structSize = sizeof(status_); status_.abiVersion = 1;
}
CompositionPresenter::~CompositionPresenter()
{
    // On terminal driver failure an unretired context is deliberately retained
    // until process teardown. Explicit Stop and status expose this condition.
    Stop();
}
bool CompositionPresenter::Exists() const { return impl_ != nullptr; }
void CompositionPresenter::Observe(IDXGISwapChain* source, UINT interval, UINT flags)
{
    status_.sourceValid = 0;
    DXGI_SWAP_CHAIN_DESC d{};
    if (source == nullptr || FAILED(source->GetDesc(&d))) return;
    status_.sourceValid = 1; status_.sourceIdentity = reinterpret_cast<uintptr_t>(source);
    status_.windowHandle = reinterpret_cast<uintptr_t>(d.OutputWindow);
    status_.width = d.BufferDesc.Width; status_.height = d.BufferDesc.Height; status_.format = d.BufferDesc.Format;
    status_.swapEffect = d.SwapEffect; status_.sourceFlags = d.Flags; status_.windowed = d.Windowed != FALSE;
    status_.sampleCount = d.SampleDesc.Count; status_.bufferCount = d.BufferCount;
    status_.syncInterval = interval; status_.presentFlags = flags;
}
HRESULT CompositionPresenter::Prepare(ID3D11Device* device, IDXGISwapChain* source, UINT interval, UINT flags, bool sourceMaintenance)
{
    try
    {
        status_.reason = RbaCompositionReason_Unsupported;
        DXGI_SWAP_CHAIN_DESC d{}; Check(source->GetDesc(&d));
        BOOL fullscreen = FALSE; Check(source->GetFullscreenState(&fullscreen, nullptr));
        DWORD process = 0; GetWindowThreadProcessId(d.OutputWindow, &process);
        RECT client{}; Require(GetClientRect(d.OutputWindow, &client) != FALSE);
        const UINT allowedSourceFlags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH | (sourceMaintenance ?
            DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT | DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0);
        Require(device && !fullscreen && d.Windowed && IsWindow(d.OutputWindow) && process == GetCurrentProcessId() &&
            d.BufferDesc.Width > 0 && d.BufferDesc.Height > 0 && d.BufferDesc.Width <= 16384 && d.BufferDesc.Height <= 16384 &&
            static_cast<UINT>(client.right - client.left) == d.BufferDesc.Width && static_cast<UINT>(client.bottom - client.top) == d.BufferDesc.Height &&
            d.SampleDesc.Count == 1 && d.SampleDesc.Quality == 0 && StorageFormat(d.BufferDesc.Format) != DXGI_FORMAT_UNKNOWN &&
            (d.Flags & ~allowedSourceFlags) == 0 && interval <= 1 && flags == 0 &&
            (d.SwapEffect == DXGI_SWAP_EFFECT_DISCARD || d.SwapEffect == DXGI_SWAP_EFFECT_SEQUENTIAL ||
             d.SwapEffect == DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL || d.SwapEffect == DXGI_SWAP_EFFECT_FLIP_DISCARD));
        if (impl_ && (impl_->sourceIdentity != reinterpret_cast<uintptr_t>(source) ||
            impl_->sourceDesc.BufferDesc.Width != d.BufferDesc.Width || impl_->sourceDesc.BufferDesc.Height != d.BufferDesc.Height ||
            impl_->sourceDesc.BufferDesc.Format != d.BufferDesc.Format || impl_->sourceDesc.OutputWindow != d.OutputWindow ||
            impl_->sourceDesc.SwapEffect != d.SwapEffect || impl_->sourceDesc.Flags != d.Flags))
        {
            status_.reason = RbaCompositionReason_SourceChanged; throw Failure{DXGI_ERROR_INVALID_CALL};
        }
        if (!impl_)
        {
            status_.reason = RbaCompositionReason_CreateFailed;
            auto created = std::make_unique<Impl>(); created->Create(device, source, d, status_.operation); impl_ = created.release();
            status_.compositionIdentity = reinterpret_cast<uintptr_t>(impl_->chain.Get()); ++status_.preparations;
        }
        status_.reason = RbaCompositionReason_CopyFailed; status_.operation = 11; impl_->Copy(source); ++status_.copiedFrames;
        status_.reason = RbaCompositionReason_Prepared; status_.lastResult = S_OK; return S_OK;
    }
    catch (const Failure& f) { status_.lastResult = f.result; return f.result; }
    catch (...) { status_.lastResult = E_FAIL; return E_FAIL; }
}
HRESULT CompositionPresenter::Present(UINT interval)
{
    try
    {
        Require(impl_ != nullptr);
        if (!impl_->attached)
        {
            Check(impl_->target->SetRoot(impl_->visual.Get()));
            // Once SetRoot succeeds, even a failed Commit needs explicit detach.
            impl_->attached = true; status_.attached = 1; Check(impl_->composition->Commit());
        }
        UINT before = 0, after = 0;
        const HRESULT beforeResult = impl_->chain->GetLastPresentCount(&before);
        ++status_.presentAttempts;
        impl_->unknown12 = true;
        const HRESULT result = impl_->chain->Present(interval, 0);
        const auto retirement = ++impl_->serial;
        const HRESULT signalResult = impl_->queue->Signal(impl_->retired12.Get(), retirement);
        if (SUCCEEDED(signalResult)) { impl_->pending12 = retirement; impl_->unknown12 = false; }
        const HRESULT afterResult = impl_->chain->GetLastPresentCount(&after);
        status_.lastBeforeCount = before; status_.lastAfterCount = after;
        status_.countValid = SUCCEEDED(beforeResult) && SUCCEEDED(afterResult) && SUCCEEDED(result) && after >= before;
        status_.lastOwnedDelta = status_.countValid ? after - before : 0;
        if (status_.countValid)
        {
            if (after - before == 1) ++status_.singleCallSamples; else ++status_.countMismatches;
        }
        else ++status_.countFailures;
        Check(signalResult);
        status_.lastResult = result;
        if (FAILED(result)) { ++status_.presentFailures; status_.reason = RbaCompositionReason_PresentFailed; return result; }
        ++status_.presentSuccesses; status_.active = 1; status_.reason = RbaCompositionReason_Presented; return result;
    }
    catch (const Failure& f) { status_.lastResult = f.result; status_.reason = RbaCompositionReason_PresentFailed; return f.result; }
    catch (...) { status_.lastResult = E_FAIL; status_.reason = RbaCompositionReason_PresentFailed; return E_FAIL; }
}
bool CompositionPresenter::Stop()
{
    if (!impl_) { status_.active = status_.attached = status_.draining = 0; return true; }
    try
    {
        status_.reason = RbaCompositionReason_DetachFailed;
        const bool wasAttached = impl_->attached; impl_->Detach(); status_.attached = 0;
        if (wasAttached) ++status_.detachments;
        status_.reason = RbaCompositionReason_Draining; status_.draining = 1; status_.active = 0;
        if (!impl_->Retire()) { ++status_.retirementFailures; return false; }
        delete impl_; impl_ = nullptr;
        status_.draining = 0; status_.countValid = 0; status_.compositionIdentity = 0;
        status_.reason = RbaCompositionReason_Stopped; return true;
    }
    catch (const Failure& f) { status_.lastResult = f.result; return false; }
    catch (...) { status_.lastResult = E_FAIL; return false; }
}

#ifdef RBA_COMPOSITION_TESTING
void CompositionPresenter::FailNextDetach() { if (impl_) impl_->failNextDetach = true; }
HRESULT CompositionPresenter::ReadPrepared(std::vector<uint8_t>& bytes)
{
    try
    {
        Require(impl_ != nullptr && impl_->Retire());
        auto* buffer = impl_->buffers[impl_->chain->GetCurrentBackBufferIndex()].Get();
        const auto desc = buffer->GetDesc();
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{}; UINT rows = 0; UINT64 rowBytes = 0, total = 0;
        impl_->device12->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, &rows, &rowBytes, &total);
        D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; d.Width = total;
        d.Height = d.DepthOrArraySize = d.MipLevels = 1; d.SampleDesc.Count = 1; d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        auto& readback = impl_->retainedReadback;
        Check(impl_->device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)));
        Check(impl_->allocator->Reset()); Check(impl_->commands->Reset(impl_->allocator.Get(), nullptr));
        auto barrier = Barrier(buffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE);
        impl_->commands->ResourceBarrier(1, &barrier);
        D3D12_TEXTURE_COPY_LOCATION from{}; from.pResource = buffer; from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        D3D12_TEXTURE_COPY_LOCATION to{}; to.pResource = readback.Get(); to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        to.PlacedFootprint = footprint; impl_->commands->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
        barrier = Barrier(buffer, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_PRESENT);
        impl_->commands->ResourceBarrier(1, &barrier); Check(impl_->commands->Close());
        ID3D12CommandList* lists[]{impl_->commands.Get()}; impl_->unknown12 = true; impl_->queue->ExecuteCommandLists(1, lists);
        const auto value = ++impl_->serial; Check(impl_->queue->Signal(impl_->retired12.Get(), value));
        impl_->pending12 = value; impl_->unknown12 = false;
        Require(Wait(impl_->retired12.Get(), value, impl_->event.value)); Check(impl_->device12->GetDeviceRemovedReason());
        void* mapped = nullptr; const D3D12_RANGE range{0, static_cast<SIZE_T>(total)}; Check(readback->Map(0, &range, &mapped));
        bytes.resize(static_cast<size_t>(rowBytes) * rows);
        for (UINT row = 0; row < rows; ++row) memcpy(bytes.data() + row * rowBytes,
            static_cast<const uint8_t*>(mapped) + footprint.Offset + row * footprint.Footprint.RowPitch, static_cast<size_t>(rowBytes));
        const D3D12_RANGE written{0, 0}; readback->Unmap(0, &written); readback.Reset(); impl_->pending12 = 0; return S_OK;
    }
    catch (const Failure& f) { return f.result; }
    catch (...) { return E_FAIL; }
}
#endif
