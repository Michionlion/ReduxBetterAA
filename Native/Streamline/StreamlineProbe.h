#pragma once
#include <stdint.h>

#ifdef RBA_STREAMLINE_EXPORTS
#define RBA_SL_API extern "C" __declspec(dllexport)
#else
#define RBA_SL_API extern "C" __declspec(dllimport)
#endif

// Diagnostic bootstrap only. No presentation interception, interpolation or
// provider registration. This probe must run before another owner loads SL;
// it refuses an existing SL process singleton instead of disturbing it.
enum RbaSlResult : uint32_t {
    RbaSlOk = 0, RbaSlAbiMismatch = 1, RbaSlInvalidArgument = 2,
    RbaSlRuntimeUnavailable = 3, RbaSlAlreadyOwned = 4,
    RbaSlDeviceError = 5, RbaSlUnsupported = 6, RbaSlSdkError = 7
};

struct RbaSlProbeDesc {
    uint32_t structSize;
    uint32_t abiVersion;                 // 1, Windows x64 natural packing
    const wchar_t* runtimeDirectory;     // Absolute path to pinned production bin/x64
    void* d3d11Device;                   // Unity ID3D11Device; only GetAdapter is used
    uint32_t applicationId;              // A legitimately assigned NVIDIA ID, or 0
    uint32_t reserved;                   // 0
    const char* unityVersion;            // With projectId required when applicationId=0
    const char* projectId;               // Real project GUID; never borrowed
};

struct RbaSlProbeStatus {
    uint32_t structSize;
    uint32_t abiVersion;
    uint32_t result;                     // RbaSlResult
    uint32_t sdkResult;                  // sl::Result, diagnostic only
    uint32_t featureSupported;           // Actual SDK result on the source adapter
    uint32_t reflexSupported;
    uint32_t queriedMultiplierMask;      // Bit N = Nx, capped to initial 2x-4x scope
    uint32_t maxGeneratedFrames;         // Raw SDK count; UI multiplier = count + 1
    uint32_t dynamicMfgSupported;        // Raw SDK query, not an exposed option
    uint32_t sdkRuntimeStatus;           // sl::DLSSGStatus, diagnostic only
    uint32_t presentationReady;          // Always 0 in this bootstrap
    uint32_t available;                  // Always 0: a probe cannot present frames
    uint32_t adapterVendorId;
    uint32_t adapterDeviceId;
    uint32_t adapterLuidLow;
    int32_t adapterLuidHigh;
    char runtimeVersion[64];
    char reason[512];
};

// Synchronous startup/standalone diagnostic. Creates and destroys an independent
// same-adapter DX12 device; no D3D11 immediate-context calls or GPU dispatches.
// Never call from DllMain, a render callback, or during another SL integration.
// Successful result means queried, not enabled. No vendor binaries are shipped
// by this target. Device reference must remain valid for the duration of Probe.
RBA_SL_API uint32_t RbaSlProbe(const RbaSlProbeDesc* description, RbaSlProbeStatus* status);
