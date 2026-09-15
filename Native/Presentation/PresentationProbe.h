#pragma once

#include <stdint.h>

#ifdef RBA_PRESENT_EXPORTS
#define RBA_PRESENT_API __declspec(dllexport)
#else
#define RBA_PRESENT_API __declspec(dllimport)
#endif
#define RBA_PRESENT_CALL __cdecl

// This is a disabled-by-default presentation experiment, never an FG provider.
// Enabling is only legal in an external diagnostic harness that has verified ALL
// contracts below on the exact player. No user setting should call this API.
enum RbaPresentContract
{
    RbaPresentContract_Unity6000_5_8f1 = 1,
    RbaPresentContract_QueryAtPresentOnRenderThread = 2,
    RbaPresentContract_OneFinalTicketPerRealFrame = 4,
    RbaPresentContract_ExclusivePresentOwner = 8,
    RbaPresentContract_All = 15
};

enum RbaPresentReason
{
    RbaPresentReason_Passive = 0,
    RbaPresentReason_NotLoaded = 1,
    RbaPresentReason_UnsupportedRenderer = 2,
    RbaPresentReason_ContractNotVerified = 3,
    RbaPresentReason_NoFinalFrame = 4,
    RbaPresentReason_WrongThread = 5,
    RbaPresentReason_MissingInterface = 6,
    RbaPresentReason_MissingSwapChain = 7,
    RbaPresentReason_InvalidFrame = 8,
    RbaPresentReason_PresentFailed = 9,
    RbaPresentReason_DeviceReset = 10,
    RbaPresentReason_Presented = 11
};

enum RbaPresentCountReason
{
    RbaPresentCountReason_Disabled = 0,
    RbaPresentCountReason_Baseline = 1,
    RbaPresentCountReason_Continuous = 2,
    RbaPresentCountReason_MissingInterface = 3,
    RbaPresentCountReason_MissingSwapChain = 4,
    RbaPresentCountReason_QueryFailed = 5,
    RbaPresentCountReason_Disjoint = 6,
    RbaPresentCountReason_ChainChanged = 7,
    RbaPresentCountReason_DeviceReset = 8,
    RbaPresentCountReason_InvalidFrame = 9,
    RbaPresentCountReason_WrongThread = 10,
    RbaPresentCountReason_PresentFailed = 11
};

#pragma pack(push, 8)
typedef struct RbaPresentFrame
{
    uint32_t structSize;
    uint32_t abiVersion; // 1
    uint64_t realFrameId; // Strictly increasing, nonzero until device reset.
} RbaPresentFrame;

typedef struct RbaPresentStatus
{
    uint32_t structSize;
    uint32_t abiVersion;
    uint32_t loaded;
    uint32_t requested; // 0 passive, 1 original, 2 strict composition, 3 maintained source + composition.
    uint32_t renderer; // UnityGfxRenderer; 2 is D3D11.
    uint32_t reason; // RbaPresentReason
    uint32_t deviceGeneration;
    int32_t renderEventId;
    uint64_t finalFrameId;
    uint64_t presentedFrameId;
    uint64_t finalFrameEvents;
    uint64_t presentQueries;
    uint64_t presentAttempts;
    uint64_t presentSuccesses;
    uint64_t presentFailures;
    uint64_t duplicateQueries;
    uint64_t rejectedFrames;
    uint64_t wrongThreadQueries;
    uint32_t lastSyncInterval;
    uint32_t lastPresentFlags;
    int32_t lastPresentResult; // HRESULT; success does not prove visible display.
    uint32_t renderThreadId;
    uint32_t lastQueryThreadId;
    uint32_t reserved;
    uint64_t lastQueryFrameId; // Final ticket visible to the latest query, also while passive.
    uint64_t queriesWithoutFinalFrame;
    // ABI v2: opt-in API-call observations, never displayed-frame statistics.
    uint32_t observationEnabled;
    uint32_t countValid;
    uint32_t countEpoch;
    uint32_t countReason;
    int32_t lastCountResult;
    uint32_t lastCount;
    uint32_t lastBeforeCount;
    uint32_t lastAfterCount;
    uint32_t lastDelta;
    uint32_t lastBeforeDelta;
    uint32_t lastOwnedDelta;
    uint32_t countResetReason; // Last RbaPresentCountReason that broke continuity.
    uint64_t lastSampleFrameId;
    uint64_t lastOwnedFrameId;
    uint64_t lastDeltaFrameSpan;
    uint64_t countSamples;
    uint64_t countFailures;
    uint64_t countResets;
    uint64_t deltaSamples;
    uint64_t passiveDeltaSamples;
    uint64_t passiveCallDeltas;
    uint64_t ownedDeltaSamples;
    uint64_t ownedSingleCallSamples;
    uint64_t ownedCallMismatches;
    uint64_t unexplainedCallDeltas;
    uint64_t chainIdentity;
} RbaPresentStatus;

enum RbaCompositionReason
{
    RbaCompositionReason_Passive = 0,
    RbaCompositionReason_Prepared = 1,
    RbaCompositionReason_Presented = 2,
    RbaCompositionReason_Unsupported = 3,
    RbaCompositionReason_SourceChanged = 4,
    RbaCompositionReason_CreateFailed = 5,
    RbaCompositionReason_CopyFailed = 6,
    RbaCompositionReason_PresentFailed = 7,
    RbaCompositionReason_Draining = 8,
    RbaCompositionReason_DetachFailed = 9,
    RbaCompositionReason_Stopped = 10
};

// Independent ABI v1. Original status v2 remains unchanged. All details are
// observed on the render thread; this getter never invokes D3D/DXGI/DComp.
typedef struct RbaCompositionStatus
{
    uint32_t structSize, abiVersion, requested, active;
    uint32_t reason;
    int32_t lastResult;
    uint32_t sourceValid, width, height, format, swapEffect, sourceFlags;
    uint32_t windowed, sampleCount, bufferCount, syncInterval, presentFlags;
    uint32_t attached, draining, countValid, lastBeforeCount, lastAfterCount;
    uint32_t lastOwnedDelta, operation; // Last native setup/copy stage; see README.
    uint64_t sourceIdentity, windowHandle, compositionIdentity;
    uint64_t preparations, copiedFrames, presentAttempts, presentSuccesses;
    uint64_t presentFailures, singleCallSamples, countMismatches, countFailures;
    uint64_t detachments, retirementFailures;
} RbaCompositionStatus;
#pragma pack(pop)

#ifdef __cplusplus
extern "C" {
#endif

// Main-thread safe control. Returns 1 for accepted request; 0 for missing
// contract/load/renderer. Disable is always accepted. Reset/shutdown clears it.
RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_Request(uint32_t enabled, uint32_t verifiedContracts);
// Opt-in, read-only swapchain count observation. Does not request presentation.
// Enabling requires verifiedUnityAbi == RbaPresentContract_Unity6000_5_8f1.
RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_Observe(uint32_t enabled, uint32_t verifiedUnityAbi);
// Returns 1 on success. The caller supplies its allocated struct size/version.
RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_GetStatus(RbaPresentStatus* status);
// Separate diagnostic mode; requires All contracts plus observation enabled.
// Disable is deferred to the next render query. Keep the frame pump running
// until requested=active=attached=draining=0; do not unload while draining.
RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_RequestComposition(uint32_t enabled, uint32_t verifiedContracts);
// OPT-IN DIAGNOSTIC ONLY: the probe owns one original Present per real ticket to
// maintain Unity's latency budget plus one separate opaque composition Present.
// Legacy status counts original calls; composition status counts overlay calls.
RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_RequestCompositionWithSourceMaintenance(uint32_t enabled, uint32_t verifiedContracts);
RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_GetCompositionStatus(RbaCompositionStatus* status);
// UnityRenderingEventAndData (__stdcall) for CommandBuffer.IssuePluginEventAndData,
// executed once at the verified end-of-frame seam using status.renderEventId. The packet
// must remain allocated until finalFrameId acknowledges consumption. The plugin
// retains no packet pointer. Events and queries must use the same render thread.
RBA_PRESENT_API void* RBA_PRESENT_CALL RbaPresent_GetRenderEventFunc(void);

#ifdef __cplusplus
}
#endif
