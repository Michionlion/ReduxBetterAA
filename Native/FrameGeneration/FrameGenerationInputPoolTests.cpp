#include "FrameGenerationInputPool.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <thread>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
uint32_t checks = 0;
void Expect(bool value, const char* text) { ++checks; if (!value) throw std::runtime_error(text); }
void Check(HRESULT result, const char* text) { Expect(SUCCEEDED(result), text); }
struct Readback {
    ComPtr<ID3D12Resource> buffer;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    uint32_t width = 0, height = 0, bytesPerPixel = 0;
};
struct Test {
    ComPtr<ID3D11Device5> device11;
    ComPtr<ID3D11DeviceContext4> context11;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue12, asyncQueue;
    ComPtr<ID3D12Fence> sourceGate12, asyncGate, submitted, consumed;
    ComPtr<ID3D11Fence> sourceGate11;
    std::array<RbaFgInputLease, 3> leases{};
    std::array<std::array<ComPtr<ID3D11Texture2D>, 4>, 3> sources;
    std::array<std::array<std::vector<uint8_t>, 4>, 3> expected;
    std::array<std::array<Readback, 4>, 3> readbacks;
    std::array<ComPtr<ID3D12CommandAllocator>, 3> allocators;
    std::array<ComPtr<ID3D12GraphicsCommandList>, 3> lists;
    void* pool = nullptr;
    RbaFgInputStatus status{};
    RbaFgInputCreateDesc desc{};

    explicit Test(uint32_t motionFormat) {
        ComPtr<ID3D11Device> d; ComPtr<ID3D11DeviceContext> c;
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &d, nullptr, &c), "Create actual D3D11 device failed");
        Check(d.As(&device11), "Query D3D11Device5 failed"); Check(c.As(&context11), "Query D3D11 context4 failed");
        ComPtr<IDXGIDevice> dxgi; Check(device11.As(&dxgi), "Query source DXGI device failed");
        ComPtr<IDXGIAdapter> adapter; Check(dxgi->GetAdapter(&adapter), "Get actual source adapter failed");
        Check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device12)), "Create caller-owned D3D12 device failed");
        D3D12_COMMAND_QUEUE_DESC q{}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device12->CreateCommandQueue(&q, IID_PPV_ARGS(&queue12)), "Create caller queue failed");
        Check(device12->CreateCommandQueue(&q, IID_PPV_ARGS(&asyncQueue)), "Create asynchronous consumer queue failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&sourceGate12)), "Create source gate failed");
        HANDLE shared = nullptr;
        Check(device12->CreateSharedHandle(sourceGate12.Get(), nullptr, GENERIC_ALL, nullptr, &shared), "Share source gate failed");
        const HRESULT opened = device11->OpenSharedFence(shared, IID_PPV_ARGS(&sourceGate11)); CloseHandle(shared);
        Check(opened, "Import source gate failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&asyncGate)), "Create consumer gate failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&submitted)), "Create submitted fence failed");
        Check(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&consumed)), "Create GPU consumer completion fence failed");
        desc = {sizeof(desc), 1, device11.Get(), queue12.Get(), 32, 16, 64, 32, RbaFgInputSrgbEncoded, motionFormat};
        auto invalid = desc; invalid.colorTransfer = 0;
        Expect(RbaFgInputCreate(&invalid, &pool, &status) == RbaFgInputInvalidArgument && pool == nullptr, "Implicit encoding must reject");
        q.Type = D3D12_COMMAND_LIST_TYPE_COPY; ComPtr<ID3D12CommandQueue> copyQueue;
        Check(device12->CreateCommandQueue(&q, IID_PPV_ARGS(&copyQueue)), "Create invalid queue fixture failed");
        invalid = desc; invalid.d3d12Queue = copyQueue.Get();
        Expect(RbaFgInputCreate(&invalid, &pool, &status) == RbaFgInputInvalidArgument && pool == nullptr, "COPY queue must reject");
        ComPtr<IDXGIFactory4> factory; Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "Create WARP fixture factory failed");
        ComPtr<IDXGIAdapter> warp; Check(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp)), "Find a different software adapter failed");
        ComPtr<ID3D12Device> warpDevice; Check(D3D12CreateDevice(warp.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&warpDevice)), "Create WARP fixture device failed");
        q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT; ComPtr<ID3D12CommandQueue> warpQueue;
        Check(warpDevice->CreateCommandQueue(&q, IID_PPV_ARGS(&warpQueue)), "Create WARP DIRECT queue failed");
        invalid = desc; invalid.d3d12Queue = warpQueue.Get();
        Expect(RbaFgInputCreate(&invalid, &pool, &status) == RbaFgInputInvalidArgument && pool == nullptr,
            "A caller-owned D3D12 queue on a different actual adapter must reject before allocation");
        const auto result = RbaFgInputCreate(&desc, &pool, &status);
        if (result != RbaFgInputOk) std::printf("CREATE motion=%u result=%u reason=%s\n", motionFormat, result, status.error);
        if (motionFormat == RbaFgInputMotionRg32Float && result == RbaFgInputUnsupported) {
            Expect(pool == nullptr && std::strstr(status.error, "RG32") != nullptr,
                "Unsupported RG32 sharing must explicitly reject without leaving a pool");
            return;
        }
        Expect(result == RbaFgInputOk && status.freeSlots == 3, "Create actual three-slot input pool failed");
    }
    ComPtr<ID3D11Texture2D> Texture(ID3D11Device* device, UINT width, UINT height, DXGI_FORMAT format, const void* data, UINT rowBytes) {
        D3D11_TEXTURE2D_DESC d{}; d.Width = width; d.Height = height; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = format; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA initial{}; initial.pSysMem = data; initial.SysMemPitch = rowBytes;
        ComPtr<ID3D11Texture2D> texture; Check(device->CreateTexture2D(&d, data ? &initial : nullptr, &texture), "Create immutable-input fixture failed"); return texture;
    }
    void PrepareInputs() {
        for (size_t slot = 0; slot < 3; ++slot) for (size_t input = 0; input < 4; ++input) {
            const UINT width = input < 2 ? desc.displayWidth : desc.renderWidth;
            const UINT height = input < 2 ? desc.displayHeight : desc.renderHeight;
            const UINT bpp = input == 3 && desc.motionFormat == RbaFgInputMotionRg32Float ? 8 : 4;
            auto& data = expected[slot][input]; data.resize(width * height * bpp);
            // Byte-preserving transport is tested independently from SDK numeric
            // interpretation. Each slot/input has a different deterministic image.
            for (size_t i = 0; i < data.size(); ++i) data[i] = static_cast<uint8_t>((i * 13 + slot * 47 + input * 29) & 255);
            const DXGI_FORMAT format = input < 2 ? DXGI_FORMAT_R8G8B8A8_TYPELESS : input == 2 ? DXGI_FORMAT_R32_FLOAT :
                desc.motionFormat == RbaFgInputMotionRg16Float ? DXGI_FORMAT_R16G16_FLOAT : DXGI_FORMAT_R32G32_FLOAT;
            sources[slot][input] = Texture(device11.Get(), width, height, format, data.data(), width * bpp);
        }
    }
    void CopyForReadback(size_t slot, const RbaFgInputResources& resources) {
        const std::array<void*, 4> inputs{resources.finalColor, resources.hudlessColor, resources.depth, resources.motion};
        Check(device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocators[slot])), "Create readback allocator failed");
        Check(device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[slot].Get(), nullptr,
            IID_PPV_ARGS(&lists[slot])), "Create readback list failed");
        auto* list = lists[slot].Get();
        for (size_t input = 0; input < 4; ++input) {
            auto* resource = static_cast<ID3D12Resource*>(inputs[input]);
            ComPtr<ID3D12Device> actual; Check(resource->GetDevice(IID_PPV_ARGS(&actual)), "Get shared-resource device failed");
            Expect(actual.Get() == device12.Get(), "Shared texture must belong to caller's exact D3D12 device");
            const auto d = resource->GetDesc(); auto& read = readbacks[slot][input];
            read.width = static_cast<UINT>(d.Width); read.height = d.Height;
            read.bytesPerPixel = input == 3 && desc.motionFormat == RbaFgInputMotionRg32Float ? 8 : 4;
            UINT64 bytes = 0; device12->GetCopyableFootprints(&d, 0, 1, 0, &read.footprint, nullptr, nullptr, &bytes);
            D3D12_HEAP_PROPERTIES heap{}; heap.Type = D3D12_HEAP_TYPE_READBACK;
            D3D12_RESOURCE_DESC buffer{}; buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            buffer.Width = bytes; buffer.Height = 1; buffer.DepthOrArraySize = 1; buffer.MipLevels = 1;
            buffer.SampleDesc.Count = 1; buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            Check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_COPY_DEST,
                nullptr, IID_PPV_ARGS(&read.buffer)), "Create readback resource failed");
            D3D12_RESOURCE_BARRIER barrier{}; barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE};
            list->ResourceBarrier(1, &barrier);
            D3D12_TEXTURE_COPY_LOCATION source{}; source.pResource = resource; source.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            D3D12_TEXTURE_COPY_LOCATION dest{}; dest.pResource = read.buffer.Get(); dest.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dest.PlacedFootprint = read.footprint; list->CopyTextureRegion(&dest, 0, 0, 0, &source, nullptr);
            barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE; barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
            list->ResourceBarrier(1, &barrier);
        }
        Check(list->Close(), "Close readback list failed"); ID3D12CommandList* commands[] = {list}; queue12->ExecuteCommandLists(1, commands);
    }
    void AssertPixels() {
        for (size_t slot = 0; slot < 3; ++slot) for (size_t input = 0; input < 4; ++input) {
            auto& read = readbacks[slot][input]; void* mappedPointer = nullptr;
            Check(read.buffer->Map(0, nullptr, &mappedPointer), "Map exact readback failed");
            const auto* mapped = static_cast<const uint8_t*>(mappedPointer);
            bool same = true;
            for (UINT y = 0; y < read.height; ++y)
                same &= std::memcmp(mapped + read.footprint.Offset + y * read.footprint.Footprint.RowPitch,
                    expected[slot][input].data() + y * read.width * read.bytesPerPixel, read.width * read.bytesPerPixel) == 0;
            read.buffer->Unmap(0, nullptr); Expect(same, "Input pool changed/crossed frame bytes");
        }
    }
    void Run() {
        if (!pool) { std::printf("INPUT_POOL_RG32_UNSUPPORTED RG16 fallback verified; RG32 copy coverage unavailable\n"); return; }
        PrepareInputs();
        for (uint32_t i = 0; i < 3; ++i) Expect(RbaFgInputAcquire(pool, i + 1, &leases[i], &status) == RbaFgInputOk, "Acquire independent slot failed");
        RbaFgInputLease unavailable{};
        Expect(RbaFgInputAcquire(pool, 4, &unavailable, &status) == RbaFgInputBusy && unavailable.serial == 0, "Busy pool must not overwrite any slot");
        Check(context11->Wait(sourceGate11.Get(), 1), "Block original D3D11 producer failed");
        const uint64_t start = GetTickCount64();
        Expect(RbaFgInputCopyScene(pool, &leases[0], sources[0][1].Get(), sources[0][2].Get(), sources[0][1].Get(), &status) ==
            RbaFgInputInvalidArgument && !status.sceneCopied && status.sceneSourcesRetired, "Invalid metadata/resource must reject before any copy");
        ComPtr<ID3D11Device> foreign; Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &foreign, nullptr, nullptr), "Create foreign D3D11 fixture failed");
        auto foreignColor = Texture(foreign.Get(), desc.displayWidth, desc.displayHeight, DXGI_FORMAT_R8G8B8A8_UNORM, nullptr, 0);
        Expect(RbaFgInputCopyFinal(pool, &leases[0], foreignColor.Get(), &status) == RbaFgInputInvalidArgument && !status.finalCopied,
            "Same-adapter foreign D3D11 texture must reject");
        for (size_t i = 0; i < 3; ++i) {
            Expect(RbaFgInputCopyScene(pool, &leases[i], sources[i][1].Get(), sources[i][2].Get(), sources[i][3].Get(), &status) == RbaFgInputOk,
                "Submit borrowed scene copy failed");
            Expect(!status.sceneSourcesRetired, "Blocked producer must retain all scene inputs");
            RbaFgInputResources premature{};
            Expect(RbaFgInputQueueReady(pool, &leases[i], &premature, &status) == RbaFgInputInvalidArgument &&
                !premature.finalColor && !status.readyQueued, "Scene-only capture cannot become a final-with-UI input batch");
            Expect(RbaFgInputCopyFinal(pool, &leases[i], sources[i][0].Get(), &status) == RbaFgInputOk && !status.finalSourceRetired,
                "Submit borrowed final copy failed");
            RbaFgInputResources resources{};
            Expect(RbaFgInputQueueReady(pool, &leases[i], &resources, &status) == RbaFgInputOk && status.readyQueued, "Queue same-device input dependency failed");
            CopyForReadback(i, resources);
        }
        Expect(GetTickCount64() - start < 1500, "Capture/ready unexpectedly waited for a blocked GPU");
        uint32_t otherThread = 0;
        std::thread wrong([&]() { otherThread = RbaFgInputCopyFinal(pool, &leases[0], sources[0][0].Get(), nullptr); }); wrong.join();
        Expect(otherThread == RbaFgInputInvalidArgument, "Wrong-thread copy must reject before immediate-context access");
        Expect(RbaFgInputCancel(pool, &leases[0], &status) == RbaFgInputInvalidArgument, "Consumed lease cannot be cancelled without consumer retirement");
        Expect(RbaFgInputRetire(pool, &leases[0], consumed.Get(), 0, &status) == RbaFgInputInvalidArgument && !status.retirementQueued,
            "A missing completion value must reject before queueing retirement");
        Expect(RbaFgInputRetire(pool, &leases[0], nullptr, 1, &status) == RbaFgInputInvalidArgument && !status.retirementQueued,
            "A missing completion fence must reject before queueing retirement");
        Check(queue12->Signal(submitted.Get(), 1), "Signal submitted reads failed");
        Check(asyncQueue->Wait(submitted.Get(), 1), "Order asynchronous consumer failed");
        Check(asyncQueue->Wait(asyncGate.Get(), 1), "Block asynchronous consumer failed");
        Check(asyncQueue->Signal(consumed.Get(), 1), "Signal actual GPU consumer completion failed");
        for (const auto& lease : leases)
            Expect(RbaFgInputRetire(pool, &lease, consumed.Get(), 1, &status) == RbaFgInputOk && !status.retired, "Queue provider retirement failed");
        Expect(RbaFgInputRetire(pool, &leases[0], consumed.Get(), 1, &status) == RbaFgInputInvalidArgument && status.retirementQueued,
            "Repeated retirement must preserve the already queued lease");
        for (auto& frame : sources) for (auto& source : frame) source.Reset(); // Pool must hold all twelve source references.
        Expect(RbaFgInputDestroy(pool, &status) == RbaFgInputBusy, "Destroy must preserve blocked inputs and consumers");
        Check(sourceGate12->Signal(1), "Release producer test gate failed");
        const uint64_t deadline = GetTickCount64() + 5000;
        for (;;) {
            bool retiredSources = true;
            for (const auto& lease : leases) {
                Expect(RbaFgInputQueryLease(pool, &lease, &status) == RbaFgInputBusy, "Consumer gate must keep slots borrowed");
                retiredSources &= status.sceneSourcesRetired && status.finalSourceRetired;
                Expect(!status.retired, "Input-copy completion is not provider retirement");
            }
            if (retiredSources) break;
            Expect(GetTickCount64() < deadline, "Source retirement timed out"); Sleep(1);
        }
        Expect(RbaFgInputDestroy(pool, &status) == RbaFgInputBusy, "Completed source copies must not retire the provider");
        Check(asyncGate->Signal(1), "Release consumer test gate failed");
        for (const auto& lease : leases) {
            while (RbaFgInputQueryLease(pool, &lease, &status) == RbaFgInputBusy) {
                Expect(GetTickCount64() < deadline, "Provider retirement timed out"); Sleep(1);
            }
            Expect(status.result == RbaFgInputOk && status.retired && status.sceneSourcesRetired && status.finalSourceRetired,
                "Provider completion must retire exactly this input slot");
        }
        AssertPixels();
        Expect(RbaFgInputDestroy(pool, &status) == RbaFgInputOk, "Destroy after both API queues retired failed"); pool = nullptr;
        Expect(RbaFgInputCreate(&desc, &pool, &status) == RbaFgInputOk, "Recreate immutable pool failed");
        RbaFgInputLease first{}, next{};
        Expect(RbaFgInputAcquire(pool, 1, &first, &status) == RbaFgInputOk, "Acquire cancellation fixture failed");
        Expect(RbaFgInputCancel(pool, &first, &status) == RbaFgInputOk && status.retired, "Unused lease cancellation failed");
        Expect(RbaFgInputAcquire(pool, 2, &next, &status) == RbaFgInputOk && next.serial != first.serial, "Reuse must create a new lease identity");
        Expect(RbaFgInputQueryLease(pool, &first, &status) == RbaFgInputInvalidArgument, "Stale slot token must not inherit retirement");
        Expect(RbaFgInputQueryLease(pool, &leases[0], &status) == RbaFgInputInvalidArgument, "Prior pool generation must reject");
        Expect(RbaFgInputDestroy(pool, &status) == RbaFgInputOk, "Destroy must cancel an unconsumed empty lease"); pool = nullptr;
        std::printf("INPUT_POOL_PASS motion=%u exactFrames=3 exactTextures=12 checks=%u\n", desc.motionFormat, checks);
    }
};
}
int main() {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    try {
        // Keep fixtures alive until successful queue retirement or process exit;
        // an assertion never destructs an in-flight command allocator/resource.
        (new Test(RbaFgInputMotionRg16Float))->Run();
        (new Test(RbaFgInputMotionRg32Float))->Run();
        return 0;
    }
    catch (const std::exception& error) { std::printf("INPUT_POOL_FAILURE %s\n", error.what()); ExitProcess(2); }
}
