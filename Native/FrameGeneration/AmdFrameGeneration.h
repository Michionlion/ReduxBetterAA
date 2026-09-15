#pragma once
#include <stdint.h>

#ifdef RBA_AMD_FG_EXPORTS
#define RBA_AMD_FG_API extern "C" __declspec(dllexport)
#else
#define RBA_AMD_FG_API extern "C" __declspec(dllimport)
#endif

// Experimental texture-generation ABI. This is not a presentation provider:
// it never replaces a swapchain, generates UI, paces, or presents frames.
enum RbaAmdFgResult : uint32_t {
    RbaAmdFgOk = 0, RbaAmdFgInvalidArgument = 1, RbaAmdFgRuntimeUnavailable = 2,
    RbaAmdFgUnsupported = 3, RbaAmdFgDeviceError = 4, RbaAmdFgDispatchError = 5,
    RbaAmdFgGpuBusy = 6
};
enum RbaAmdFgFlags : uint32_t { RbaAmdFgInvertedDepth = 1, RbaAmdFgInfiniteDepth = 2 };

struct RbaAmdFgCreateDesc {
    uint32_t structSize, abiVersion;
    const wchar_t* runtimeDirectory; // Absolute directory with the pinned AMD DLL.
    void* commandQueue; // ID3D12CommandQueue*, DIRECT queue. Caller submits producer work here first.
    uint32_t maxRenderWidth, maxRenderHeight, displayWidth, displayHeight;
    uint32_t flags;
    uint32_t preferCompatibility; // 0: SDK-selected provider, 1: explicit enumerated FSR 3 provider.
};
struct RbaAmdFgFrame {
    uint32_t structSize, abiVersion;
    uint64_t frameId;
    uint32_t renderWidth, renderHeight, reset;
    // Same D3D12 device as commandQueue; all resources enter and leave COMMON.
    // Color and output: display-sized RGBA8_UNORM, output has ALLOW_UNORDERED_ACCESS.
    // Depth: R32_FLOAT; motion: R16G16_FLOAT, both render-sized. No jitter in motion.
    void* color;
    void* depth;
    void* motion;
    void* output;
    float jitterX, jitterY, motionScaleX, motionScaleY;
    float frameTimeMilliseconds, cameraNear, cameraFar, cameraFovRadians, viewSpaceToMeters;
    float cameraPosition[3], cameraUp[3], cameraRight[3], cameraForward[3];
};
struct RbaAmdFgStatus {
    uint32_t structSize, abiVersion, result;
    uint32_t maximumMultiplier; // 2 only after successful real context creation.
    uint32_t presentationSupported; // Always zero: the separate presenter must establish this.
    uint32_t lastGeneratedFrames; // Vendor dispatch output count, not presents.
    uint64_t providerVersionId, submittedRealFrames;
    char providerName[128], error[256];
};

RBA_AMD_FG_API uint32_t RbaAmdFgCreate(const RbaAmdFgCreateDesc* desc, void** context, RbaAmdFgStatus* status);
// Synchronous bounded prototype: retains inputs and waits up to 5 seconds for completion.
// On GPU-busy/error retain the caller's resources until Poll/Destroy succeeds; never reuse them.
RBA_AMD_FG_API uint32_t RbaAmdFgDispatch(void* context, const RbaAmdFgFrame* frame, RbaAmdFgStatus* status);
// Additive optional UI-compatibility path; existing ABI/layout is unchanged.
// frame.color is the final color including UI. hudLessColor is a distinct,
// same-real-frame display-sized RGBA8_UNORM scene texture on the same device,
// using identical encoded sRGB color and COMMON entry/exit states. It must differ
// from final color and output, and shares their retention/no-overwrite contract.
// Null hudLessColor is equivalent to the original Dispatch. Content freshness and
// complete UI capture are caller obligations; texture identity cannot prove them.
RBA_AMD_FG_API uint32_t RbaAmdFgDispatchWithHudLess(void* context, const RbaAmdFgFrame* frame,
    void* hudLessColor, RbaAmdFgStatus* status);
RBA_AMD_FG_API uint32_t RbaAmdFgPoll(void* context, RbaAmdFgStatus* status);
// Returns GpuBusy without destroying the context while queued work can still reference it.
// Serialize calls, including destroy, for each context. Null is an idempotent success.
RBA_AMD_FG_API uint32_t RbaAmdFgDestroy(void* context);
