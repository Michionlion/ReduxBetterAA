#pragma once
#include <cstdint>
#if defined(RBA_FG_INPUT_STATIC)
#define RBA_FG_INPUT_API extern "C"
#elif defined(RBA_FG_INPUT_EXPORTS)
#define RBA_FG_INPUT_API extern "C" __declspec(dllexport)
#else
#define RBA_FG_INPUT_API extern "C" __declspec(dllimport)
#endif

enum RbaFgInputResult : uint32_t { RbaFgInputOk, RbaFgInputInvalidArgument, RbaFgInputBusy, RbaFgInputDeviceError, RbaFgInputUnsupported };
enum RbaFgInputTransfer : uint32_t { RbaFgInputSrgbEncoded = 1 };
enum RbaFgInputMotion : uint32_t { RbaFgInputMotionRg16Float = 1, RbaFgInputMotionRg32Float = 2 };
struct RbaFgInputCreateDesc {
    uint32_t structSize, abiVersion; // sizeof(desc), 1.
    void* d3d11Device; // ID3D11Device from Unity.
    void* d3d12Queue; // Caller-owned DIRECT queue; its device owns ALL shared resources.
    uint32_t renderWidth, renderHeight, displayWidth, displayHeight;
    uint32_t colorTransfer, motionFormat; // Explicit encoded sRGB RGBA8; RG16 or RG32 float motion.
};
struct RbaFgInputLease {
    uint64_t poolId, serial, realFrameId;
    uint32_t slot, reserved;
};
struct RbaFgInputResources {
    void* finalColor; // ID3D12Resource*, display-sized RGBA8_UNORM.
    void* hudlessColor; // ID3D12Resource*, display-sized RGBA8_UNORM.
    void* depth; // ID3D12Resource*, render-sized R32_FLOAT raw device depth.
    void* motion; // ID3D12Resource*, render-sized RG16_FLOAT or RG32_FLOAT.
};
struct RbaFgInputStatus {
    uint32_t structSize, abiVersion, result, freeSlots;
    uint32_t sceneCopied, finalCopied, sceneSourcesRetired, finalSourceRetired;
    uint32_t readyQueued, retirementQueued, retired, poisoned;
    char error[256];
};

// Fixed three-slot pool. No private D3D12 device/queue, output round trip, implicit
// format conversion, waiting on the CPU, or presentation. Calls are serialized
// internally except destruction: the caller must exclude every other pool call
// while Destroy executes (a raw handle is not a concurrent-deletion guard).
// CopyScene/CopyFinal must use one original D3D11 render thread; queue
// access must be serialized with provider submissions by the coordinator.
// RG32 sharing is device/runtime-dependent. Unsupported creation returns no pool;
// the caller must explicitly convert its source motion to RG16 and recreate.
RBA_FG_INPUT_API uint32_t RbaFgInputCreate(const RbaFgInputCreateDesc*, void** pool, RbaFgInputStatus*);
RBA_FG_INPUT_API uint32_t RbaFgInputAcquire(void* pool, uint64_t realFrameId, RbaFgInputLease*, RbaFgInputStatus*);
// Accept already converted, same-device, matching single-mip/sample DEFAULT
// D3D11 textures. Compatible typeless storage is accepted; metadata defines bytes.
// Validate every resource before the first CopyResource. On success, preserve the
// source pixels until QueryLease reports their source-retired field. On terminal
// failure they stay borrowed until retirement is PROVEN (possibly only device
// removal/Destroy); keeping COM references is not permission to overwrite pixels.
// Commands enqueued AFTER Copy* on that same D3D11 immediate context retain their
// normal GPU order: copying the final backbuffer before its original Present does
// not require a CPU wait. Cross-queue/unordered writes still require retirement.
RBA_FG_INPUT_API uint32_t RbaFgInputCopyScene(void* pool, const RbaFgInputLease*,
    void* hudlessColor11, void* rawDepth11, void* motion11, RbaFgInputStatus*);
RBA_FG_INPUT_API uint32_t RbaFgInputCopyFinal(void* pool, const RbaFgInputLease*, void* finalColor11, RbaFgInputStatus*);
// Once both copies have been submitted, enqueue the shared-fence wait on the
// supplied D3D12 queue, then return four borrowed resources in COMMON state.
// Success means the GPU dependency is queued, not CPU-visible completion. The
// consumer must return all resources to COMMON and fence every asynchronous user.
// Resources may not be accessed after Retire is called, even while it is pending.
RBA_FG_INPUT_API uint32_t RbaFgInputQueueReady(void* pool, const RbaFgInputLease*, RbaFgInputResources*, RbaFgInputStatus*);
// Mandatory after QueueReady: actual same-device ID3D12Fence and value covering
// every consumer (including vendor asynchronous queues). Enqueues queue Wait and
// a distinct private D3D12 retirement signal. A CPU-signaled fence cannot prove
// GPU retirement. Caller must not falsely advance/reuse the supplied fence.
RBA_FG_INPUT_API uint32_t RbaFgInputRetire(void* pool, const RbaFgInputLease*,
    void* consumerCompletionFence12, uint64_t consumerCompletionValue, RbaFgInputStatus*);
// Before QueueReady only. Already queued input copies still have to retire.
RBA_FG_INPUT_API uint32_t RbaFgInputCancel(void* pool, const RbaFgInputLease*, RbaFgInputStatus*);
// Nonblocking. A retired token remains queryable until its slot is acquired for
// a newer frame. After reuse QueryLease rejects the old token; InvalidArgument
// alone never proves retirement. A successful Acquire with the same poolId/slot
// and a strictly newer nonzero serial positively proves full retirement of all
// older leases on that slot. The coordinator must retain that receipt for old
// managed tickets whose QueryLease acknowledgment raced the slot's reuse.
RBA_FG_INPUT_API uint32_t RbaFgInputQueryLease(void* pool, const RbaFgInputLease*, RbaFgInputStatus*);
// Nonblocking and idempotent for null. Stops acquisition, cancels unconsumed slots.
// Busy leaves the ENTIRE pool alive. Ready leases need Retire before Destroy can
// succeed. Unknown queue retirement quarantines resources until device removal.
RBA_FG_INPUT_API uint32_t RbaFgInputDestroy(void* pool, RbaFgInputStatus*);
