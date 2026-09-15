#pragma once
#include <stdint.h>

#ifdef RBA_SL_PROVIDER_EXPORTS
#define RBA_SL_FG_API extern "C" __declspec(dllexport)
#else
#define RBA_SL_FG_API extern "C" __declspec(dllimport)
#endif

// Windows x64, natural packing. This ABI is independent of StreamlineProbe.h.
enum RbaSlFgResult : uint32_t {
    RbaSlFgOk = 0, RbaSlFgAbiMismatch = 1, RbaSlFgInvalidArgument = 2,
    RbaSlFgRuntimeUnavailable = 3, RbaSlFgAlreadyOwned = 4,
    RbaSlFgDeviceError = 5, RbaSlFgUnsupported = 6, RbaSlFgSdkError = 7,
    RbaSlFgBusy = 8, RbaSlFgInvalidOrder = 9, RbaSlFgInvalidHandle = 10,
    RbaSlFgWindowChanged = 11, RbaSlFgPoisoned = 12
};
enum RbaSlFgUiStrategy : uint32_t {
    // Valid basic input path, without a claim of exact HUD preservation.
    RbaSlFgHudlessAndFinal = 0,
    // Actual separately captured alpha, not inferred from color differences.
    RbaSlFgHudlessAndRealUiAlpha = 1
};

struct RbaSlFgCreateDesc {
    uint32_t structSize, abiVersion;     // sizeof, 1
    const wchar_t* runtimeDirectory;   // Absolute pinned runtime path; copied.
    void* sourceD3D11Device;           // ID3D11Device on the presentation adapter.
    void* childHwnd;                   // Actual same-process WS_CHILD|WS_DISABLED.
    uint64_t windowEpoch;              // Host's lease epoch, nonzero; not a lease itself.
    uint32_t width, height;            // Full child/parent client extent, SDR RGBA8.
    uint32_t multiplier;               // Fixed 2, 3 or 4, checked against actual SDK.
    uint32_t reflexFrameLimitUs;       // 0 = no additional Reflex cap.
};

struct RbaSlFgInterfaces {
    uint32_t structSize, abiVersion;
    void* device12;                    // Borrowed native ID3D12Device.
    void* queue12;                     // Borrowed DIRECT queue, same device.
};

struct RbaSlFgCamera {
    // Row-indexed storage of Unity COLUMN-vector matrices: clip=P*V*world.
    // Projection is nonjittered, perspective device depth [0,1], view forward -Z.
    // Y-flipped and off-center projections are supported. V is rigid/affine.
    float projection[16], worldToView[16], viewProjection[16], previousViewProjection[16];
    float jitterRenderPixelsX, jitterRenderPixelsY;
    float nearPlane, farPlane, verticalFovRadians, aspectRatio;
    float viewSpaceToMeters;
    float frameTimeMilliseconds;       // Real elapsed time; zero only with reset.
    // Explicit conversion of stored normalized UV motion into SL's convention:
    // previousUV = currentUV + storedMotion * motionScale. Derive this from the
    // actual stored component signs, rather than assuming every AA backend uses
    // the same sign. No jitter is contained in the motion vectors.
    float motionScaleX, motionScaleY;
    uint32_t reversedDepth, resetHistory; // Each 0 or 1.
};

struct RbaSlFgFrame {
    uint32_t structSize, abiVersion;
    uint32_t realFrameId, uiStrategy;
    uint64_t windowEpoch;
    // Same-device ID3D12Resource textures, COMMON on entry and exit. Final and
    // HUD-less are distinct RGBA8_UNORM display-sized surfaces in matching SDR
    // encoding/tonemapping. Depth is raw R32_FLOAT; motion is R16G16_FLOAT or
    // R32G32_FLOAT at depth extent. Alpha, when selected, is display-sized R8_UNORM.
    void* finalColor;
    void* hudlessColor;
    void* rawDepth;
    void* normalizedMotion;
    void* realUiAlpha;
    // Optional same-device ID3D12Fence, retained by the provider. Value zero is
    // valid. If null, value must be zero and all producer writes must already be
    // ordered on the borrowed queue before Submit. Never signal a false completion.
    void* inputReadyFence;
    uint64_t inputReadyValue;
    RbaSlFgCamera camera;
};

struct RbaSlFgStatus {
    uint32_t structSize, abiVersion;
    uint32_t result, sdkResult;
    uint32_t supportedMultiplierMask;   // Actual SDK count, restricted to bits2..4.
    uint32_t maxGeneratedFrames;        // Raw queried SDK maximum.
    uint32_t multiplier, sdkRuntimeStatus;
    uint32_t inputBatchPending, poisoned;
    uint32_t lastSubmittedFrameId, lastRetiredFrameId;
    uint32_t lastSdkPresented, nativePresentCountValid;
    uint64_t realPresentCalls, sdkReportedPresentations, nativePresentCountDelta;
    uint32_t asyncApiErrors, lastPresentHresult;
    uint32_t generatedPresentationObserved; // Observed counters, not image quality.
    uint32_t reserved;
    uint64_t windowEpoch;
    char reason[512];
};

// Create/Submit/Poll/Destroy run on one presentation thread. GetStatus and marker
// APIs may run on other threads. The host owns the actual child HWND and must keep
// it and its parent/extent stable through Destroy(Ok). No HWND/focus spoofing.
// The host maintains exactly one unhooked source Present per real frame. This
// provider presents only the child. It does not install Unity or window hooks.
// Identity is pinned internally to this checkout's real Unity project/version.
// If Create returns a nonzero handle even on failure, cleanup is uncertain: keep
// the HWND lease and call Destroy. Never discard that handle or close the window
// merely because initialization reported an error.
// Healthy Destroy frees viewport/chain/inputs, while an initialized SDK/device
// session (no HWNDs or caller inputs) remains cached until process exit. The same
// adapter and presentation thread can create a new child provider without slInit.
// Device removal or terminal session errors require a process restart.
RBA_SL_FG_API uint32_t RbaSlFgCreate(const RbaSlFgCreateDesc*, uint64_t* handle, RbaSlFgStatus*);
RBA_SL_FG_API uint32_t RbaSlFgGetInterfaces(uint64_t handle, RbaSlFgInterfaces*);
RBA_SL_FG_API uint32_t RbaSlFgGetStatus(uint64_t handle, RbaSlFgStatus*);

// Host must call these at the actual engine boundaries for the same real frame.
// BeginSimulation creates a token and calls Reflex sleep before SimulationStart.
// Submit supplies only markers surrounding its actual child Present. It will
// reject incomplete/duplicate marker sequences; it never invents simulation work.
RBA_SL_FG_API uint32_t RbaSlFgBeginSimulation(uint64_t handle, uint32_t realFrameId);
RBA_SL_FG_API uint32_t RbaSlFgEndSimulation(uint64_t handle, uint32_t realFrameId);
RBA_SL_FG_API uint32_t RbaSlFgBeginRender(uint64_t handle, uint32_t realFrameId);
RBA_SL_FG_API uint32_t RbaSlFgEndRender(uint64_t handle, uint32_t realFrameId);
// Drops a host-rejected frame without manufacturing missing markers or Present.
// The host must hide/yield its child when it cannot submit a fresh eligible frame.
RBA_SL_FG_API uint32_t RbaSlFgDiscardFrame(uint64_t handle, uint32_t realFrameId);

// One GPU batch at a time. Busy BEFORE enqueue preserves the previous batch and
// does not consume the new frame. Caller may retry after Poll retires that batch.
// Submit retains COM inputs/fences, but cannot protect caller pixels from overwrite.
// Do not modify submitted pixels until lastRetiredFrameId reaches that frame.
// A post-enqueue failure poisons the provider and retains every uncertain lease.
RBA_SL_FG_API uint32_t RbaSlFgSubmit(uint64_t handle, const RbaSlFgFrame*, RbaSlFgStatus*);
// timeoutMs: 0..5000. Busy retains all live objects. Failed device/SDK cleanup does
// not make leases reusable. Destroy invalidates the handle only when it returns Ok;
// otherwise retry after retirement or quarantine until process exit.
RBA_SL_FG_API uint32_t RbaSlFgPoll(uint64_t handle, uint32_t timeoutMs, RbaSlFgStatus*);
RBA_SL_FG_API uint32_t RbaSlFgDestroy(uint64_t handle, uint32_t timeoutMs, RbaSlFgStatus*);
