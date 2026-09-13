#pragma once
#include <stdint.h>

#ifdef RBA_FSR_EXPORTS
#define RBA_FSR_API extern "C" __declspec(dllexport)
#else
#define RBA_FSR_API extern "C" __declspec(dllimport)
#endif

// Windows x64 only. All structures use natural 8-byte packing and fixed-width
// integers. Create/GetStatus/Release are CPU-only. GPU operations are dispatched
// by IssuePluginEventAndData on Unity's rendering thread. No Unity ownership is
// changed; this library never presents, changes cameras, or changes render scale.
enum RbaFsrEvent : int32_t { RbaFsrDispatch = 1, RbaFsrDestroy = 2 };
enum RbaFsrState : uint32_t { RbaFsrPending = 0, RbaFsrReady = 1, RbaFsrFailed = 2, RbaFsrDestroyed = 3 };
enum RbaFsrResult : uint32_t {
    RbaFsrOk = 0, RbaFsrInvalidArgument = 1, RbaFsrRuntimeUnavailable = 2,
    RbaFsrUnsupported = 3, RbaFsrDeviceError = 4, RbaFsrDispatchError = 5,
    RbaFsrGpuTimeout = 6, RbaFsrBusy = 7, RbaFsrAbiMismatch = 8
};
enum RbaFsrFlags : uint32_t {
    RbaFsrHdr = 1u << 0, RbaFsrInvertedDepth = 1u << 3,
    RbaFsrInfiniteDepth = 1u << 4, RbaFsrAutoExposure = 1u << 5
};

struct RbaFsrCreateDesc {
    uint32_t structSize;
    uint32_t abiVersion;                 // 1
    uint32_t renderWidth;
    uint32_t renderHeight;
    uint32_t outputWidth;
    uint32_t outputHeight;
    uint32_t flags;                      // RbaFsrFlags
    uint32_t graphicsApi;                // 11: D3D11 with same-adapter DX12 interop
    const wchar_t* runtimeDirectory;     // Absolute directory of approved SDK DLLs; copied by Create.
};

struct RbaFsrDispatchDesc {
    uint32_t structSize;
    uint32_t abiVersion;                 // 1
    void* context;
    uint64_t frameId;
    void* color;                         // ID3D11Texture2D, linear RGBA16_FLOAT
    void* depth;                         // ID3D11Texture2D, R32_FLOAT (device depth, not linear eye depth)
    void* motionVectors;                 // ID3D11Texture2D, RG16_FLOAT or RG32_FLOAT, unjittered
    void* output;                        // ID3D11Texture2D, RGBA16_FLOAT, display resolution
    void* reactive;                      // Optional ID3D11Texture2D, R8_UNORM at render resolution
    void* exposure;                      // Optional ID3D11Texture2D, R32_FLOAT 1x1 (required unless auto exposure)
    void* transparencyAndComposition;    // Optional ID3D11Texture2D, R8_UNORM at render resolution
    float jitterX;                      // FSR dispatch jitter: negative raster jitter, in render pixels
    float jitterY;
    float motionScaleX;                 // Scales normalized motion to render pixels once
    float motionScaleY;
    float frameTimeMilliseconds;
    float preExposure;
    float cameraNear;
    float cameraFar;
    float verticalFovRadians;
    float viewSpaceToMeters;
    float sharpness;
    uint32_t reset;
    uint32_t enableSharpening;
    uint32_t reserved;                   // Must be zero
};

struct RbaFsrStatus {
    uint32_t structSize;
    uint32_t abiVersion;
    uint32_t state;                      // RbaFsrState
    uint32_t result;                     // RbaFsrResult
    uint64_t lastSubmittedFrameId;       // API submission evidence only; not a quality claim
    uint64_t providerVersionId;          // Opaque value reported by AMD
    uint32_t adapterVendorId;
    uint32_t adapterDeviceId;
    char providerName[128];              // Copied actual provider name, never inferred from GPU name
    char error[512];
};

RBA_FSR_API uint32_t RbaFsrGetAbiVersion();
// Startup capability probe. Creates/destroys an actual same-adapter DX12 FSR
// context, without submission or immediate-context access. reports its provider.
RBA_FSR_API uint32_t RbaFsrProbe(const RbaFsrCreateDesc* description, void* representativeD3D11Texture, RbaFsrStatus* status);
RBA_FSR_API uint32_t RbaFsrCreate(const RbaFsrCreateDesc* description, void** context, char* error, uint32_t errorCapacity);
// Copies a descriptor into a bounded preallocated native ring and AddRefs all
// textures. Pass the returned immutable packet to event 1 exactly once. Returns
// Busy when all eight packets are queued; never overwrite a queued packet.
RBA_FSR_API uint32_t RbaFsrPrepareDispatch(const RbaFsrDispatchDesc* description, void** packet);
RBA_FSR_API void* RbaFsrGetRenderEventFunc();
RBA_FSR_API uint32_t RbaFsrGetStatus(void* context, RbaFsrStatus* status);
// Issue RbaFsrDestroy with data=context first, then poll status until Destroyed.
// Release rejects live GPU contexts. PrepareDispatch owns packet/texture lifetime
// until consumed (and GPU retirement for textures). Unity's own
// command queue orders the GPU output copy before subsequent Unity rendering.
RBA_FSR_API uint32_t RbaFsrRelease(void* context);
