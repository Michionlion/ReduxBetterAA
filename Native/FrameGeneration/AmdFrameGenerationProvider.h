#pragma once
#include <stdint.h>

#ifdef RBA_AMD_FG_EXPORTS
#define RBA_AMD_PROVIDER_API extern "C" __declspec(dllexport)
#else
#define RBA_AMD_PROVIDER_API extern "C" __declspec(dllimport)
#endif

// Independent Windows x64 ABI1. The SDK owns child swapchain interpolation and
// pacing; the host owns the actual disabled child and maintains the original
// Unity/source swapchain once per real frame. This provider installs no hooks.
enum RbaAmdProviderResult : uint32_t {
    RbaAmdProviderOk = 0, RbaAmdProviderInvalidArgument = 1,
    RbaAmdProviderRuntimeUnavailable = 2, RbaAmdProviderUnsupported = 3,
    RbaAmdProviderDeviceError = 4, RbaAmdProviderSdkError = 5,
    RbaAmdProviderBusy = 6, RbaAmdProviderWindowChanged = 7,
    RbaAmdProviderPoisoned = 8
};
struct RbaAmdProviderCreateDesc {
    uint32_t structSize, abiVersion;
    const wchar_t* runtimeDirectory;
    void* sourceD3D11Device;
    void* childHwnd;
    uint64_t windowEpoch;
    uint32_t renderWidth, renderHeight, displayWidth, displayHeight;
    uint32_t reversedDepth, preferCompatibility; // 0/1; compatibility enumerates FSR3.1.
    void* optionalQueue12; // Native DIRECT queue on the source adapter, or null to create one.
};
// Exact 304-byte layout/convention shared with the NVIDIA camera ABI. Matrices
// are row-indexed Unity column-vector matrices. V is rigid, view forward is -Z.
// Motion: previousUV = currentUV + storedMotion * motionScale, without jitter.
struct RbaAmdProviderCamera {
    float projection[16], worldToView[16], viewProjection[16], previousViewProjection[16];
    float jitterRenderPixelsX, jitterRenderPixelsY;
    float nearPlane, farPlane, verticalFovRadians, aspectRatio;
    float viewSpaceToMeters, frameTimeMilliseconds, motionScaleX, motionScaleY;
    uint32_t reversedDepth, resetHistory;
};
struct RbaAmdProviderFrame {
    uint32_t structSize, abiVersion;
    uint32_t realFrameId, reserved;
    uint64_t windowEpoch;
    // Distinct same-device textures, COMMON entry/exit. Final/HUD-less are full
    // display RGBA8_UNORM in matching encoded SDR sRGB; depth is render R32_FLOAT;
    // motion is render RG16_FLOAT or RG32_FLOAT. HUD-less is mandatory here.
    void* finalColor;
    void* hudlessColor;
    void* rawDepth;
    void* normalizedMotion;
    // Optional actual same-device producer fence. Null requires all producer
    // work already queued on GetInterfaces.queue12 before Submit.
    void* inputReadyFence;
    uint64_t inputReadyValue;
    RbaAmdProviderCamera camera;
};
struct RbaAmdProviderInterfaces {
    uint32_t structSize, abiVersion;
    void* device12;
    void* queue12; // Borrowed native DIRECT queue; lifetime ends at successful Destroy.
};
struct RbaAmdProviderStatus {
    uint32_t structSize, abiVersion, result, sdkResult;
    uint32_t initialized, supportedMultiplierMask, activeMultiplier, providerFamily;
    uint32_t inputBatchPending, poisoned, destroying, nativePresentCountValid;
    uint64_t windowEpoch, providerVersionId, acceptedRealFrames, realPresentCalls;
    uint64_t generatedDispatches, nativePresentCountDelta;
    uint32_t lastAcceptedFrameId, lastRetiredFrameId, lastPresentHresult, reserved;
    char providerName[128], reason[256];
};
// All host API calls, including Destroy, must be externally serialized for a
// context. The persistent worker isolates the SDK's unbounded waits. Create
// returns Busy with a valid context while initializing; Poll reports its result.
// Any non-null context, including after failure, retains its HWND/device lease.
// Keep this child HWND, its parent/extent/epoch and the provider DLL alive until
// Destroy returns Ok. Never interpret an SDK timeout/error as retirement.
RBA_AMD_PROVIDER_API uint32_t RbaAmdProviderCreate(const RbaAmdProviderCreateDesc*, void**, RbaAmdProviderStatus*);
RBA_AMD_PROVIDER_API uint32_t RbaAmdProviderGetInterfaces(void*, RbaAmdProviderInterfaces*);
// Ok accepts one batch; it does not claim presentation has already occurred.
// Busy does not consume the incoming batch. Caller pixels must stay immutable
// until lastRetiredFrameId reaches their ID or Destroy returns Ok. COM retention
// alone cannot protect pixels from overwrite. Submission failure poisons and
// retains the batch until successful cleanup, even if its ID was not retired.
RBA_AMD_PROVIDER_API uint32_t RbaAmdProviderSubmit(void*, const RbaAmdProviderFrame*, RbaAmdProviderStatus*);
// Nonblocking. A successful retirement includes SDK game/interpolation/present
// fences AND restoration of caller textures to COMMON on the exposed queue.
RBA_AMD_PROVIDER_API uint32_t RbaAmdProviderPoll(void*, RbaAmdProviderStatus*);
// Nonblocking and null-idempotent. Busy/error leaves the context/leases alive;
// retry, or quarantine until process exit if vendor cleanup cannot finish.
RBA_AMD_PROVIDER_API uint32_t RbaAmdProviderDestroy(void*, RbaAmdProviderStatus*);
