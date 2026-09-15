#pragma once
#include <stdint.h>

#ifdef RBA_FG_COORDINATOR_EXPORTS
#define RBA_FG_COORD_API extern "C" __declspec(dllexport)
#else
#define RBA_FG_COORD_API extern "C" __declspec(dllimport)
#endif

// Windows x64, Pack=8, __cdecl, ABI version 1. Optional GfxPlugin DLL only.
// Mode identifiers are native controls, not managed FrameGenerationMode values.
enum RbaFgCoordinatorMode : uint32_t
{ RbaFgOff = 0, RbaFgNvidia2x = 1, RbaFgNvidia3x = 2, RbaFgNvidia4x = 3, RbaFgAmd2x = 4, RbaFgNvidiaProbe = 5 };
enum RbaFgCoordinatorResult : uint32_t
{
    RbaFgOk = 0, RbaFgPending = 1, RbaFgBusy = 2, RbaFgInvalidArgument = 3,
    RbaFgNotLoaded = 4, RbaFgUnsupported = 5, RbaFgWrongThread = 6,
    RbaFgInvalidTicket = 7, RbaFgDeviceError = 8, RbaFgProviderError = 9,
    RbaFgQuarantined = 10
};
enum RbaFgCoordinatorState : uint32_t
{ RbaFgDisabled = 0, RbaFgStarting = 1, RbaFgReady = 2, RbaFgActive = 3, RbaFgDraining = 4, RbaFgBlocked = 5 };

#pragma pack(push, 8)
struct RbaFgCoordinatorConfig
{
    uint32_t structSize, abiVersion;
    uint32_t mode, verifiedContracts; // 15: exact Unity ABI/query/thread/final-ticket ownership.
    const wchar_t* providerDllPath;   // Absolute ReduxBetterAA.StreamlineProvider.dll path; copied.
    const wchar_t* runtimeDirectory;  // Absolute pinned Streamline SDK runtime path; copied.
};

struct RbaFgCoordinatorCamera
{
    // Row-indexed arrays of Unity column-vector matrices, nonjittered projection.
    float projection[16], worldToView[16], viewProjection[16], previousViewProjection[16];
    float jitterRenderPixelsX, jitterRenderPixelsY;
    float nearPlane, farPlane, verticalFovRadians, aspectRatio;
    float viewSpaceToMeters, frameTimeMilliseconds;
    float motionScaleX, motionScaleY; // Explicit stored-component conversion into provider convention.
    uint32_t reversedDepth, resetHistory;
};

struct RbaFgCoordinatorCapture
{
    uint32_t structSize, abiVersion, realFrameId, sceneEligible; // Includes actual Unity main-display HDR disabled.
    uint32_t renderWidth, renderHeight, displayWidth, displayHeight;
    uint32_t colorTransfer, motionFormat; // 1=encoded sRGB; motion 1=RG16_FLOAT, 2=RG32_FLOAT.
    void* hudlessColor11; // Display-sized RGBA8 matching final native swapchain extent.
    void* rawDepth11;     // Render-sized R32_FLOAT raw device depth.
    void* normalizedMotion11; // Stored normalized UV motion, no jitter; camera scales preserve sanitizer signs.
    RbaFgCoordinatorCamera camera;
};

struct RbaFgCoordinatorTicketStatus
{
    uint32_t structSize, abiVersion, result, kind; // kind1 capture, kind2 EOF.
    uint64_t ticket;
    uint32_t realFrameId, eventConsumed, sourcePixelsRetired, fullyRetired;
};

struct RbaFgCoordinatorStatus
{
    uint32_t structSize, abiVersion, loaded, renderer;
    uint32_t requestedMode, state, result, nvidiaMultiplierMask;
    uint32_t actualMultiplier, sceneEligible, captureEventId, endOfFrameEventId;
    uint32_t renderThreadId, lastQueryThreadId, sourceWidth, sourceHeight;
    uint32_t sourceFormat, sourceSwapEffect, sourceFlags, sourceWindowed;
    uint32_t sourceSyncInterval, sourcePresentFlags, childVisible, childLeaseRetained;
    uint32_t childAcceptingPresents, childReason, providerResult, providerPending;
    uint64_t sourceWindow, childWindow, windowEpoch, deviceGeneration;
    uint64_t finalRealFrameId, lastCapturedFrameId, lastSubmittedFrameId, lastRetiredFrameId;
    uint64_t presentQueries, sourcePresentAttempts, sourcePresentSuccesses, sourcePresentFailures;
    uint64_t duplicateQueries, wrongThreadQueries, sourceCountSingleSteps, sourceCountMismatches;
    uint64_t sourceUnexpectedCalls, providerRealPresents, providerSdkPresentations;
    uint32_t generatedPresentationObserved, pendingTickets, inputPoolPending, reserved;
    char reason[512];
};
#pragma pack(pop)

// Configure/Tick are main/window-owner-thread controls. Configure copies strings,
// never loads an SDK on the main thread. Off begins an asynchronous drain.
RBA_FG_COORD_API uint32_t RbaFgConfigure(const RbaFgCoordinatorConfig*);
RBA_FG_COORD_API uint32_t RbaFgTick(uint32_t realFrameId, uint32_t sceneEligible);
RBA_FG_COORD_API uint32_t RbaFgGetStatus(RbaFgCoordinatorStatus*);

RBA_FG_COORD_API uint32_t RbaFgBeginSimulation(uint32_t realFrameId);
RBA_FG_COORD_API uint32_t RbaFgEndSimulation(uint32_t realFrameId);
RBA_FG_COORD_API uint32_t RbaFgBeginRender(uint32_t realFrameId);
// Normally called internally at the actual EOF render event, after BeginRender.
RBA_FG_COORD_API uint32_t RbaFgEndRender(uint32_t realFrameId);

// PrepareCapture copies only immutable CPU metadata; no COM calls on main thread.
// Issue the returned eventData with captureEventId after Unity's input conversion
// commands. Retain managed source textures/pixels until sourcePixelsRetired=1.
RBA_FG_COORD_API uint32_t RbaFgPrepareCapture(const RbaFgCoordinatorCapture*, uint64_t* ticket, void** eventData);
// Enqueue ONE EOF packet every real frame, after all cameras/UI, even without a
// scene capture (captureTicket=0). This is required to advance Present ownership.
// The native EOF callback ends that frame's actual render marker before Submit.
RBA_FG_COORD_API uint32_t RbaFgPrepareEndOfFrame(uint32_t realFrameId, uint64_t captureTicket, uint64_t* ticket, void** eventData);
RBA_FG_COORD_API void* RbaFgGetRenderEventFunc(); // UnityRenderingEventAndData, __stdcall.
RBA_FG_COORD_API uint32_t RbaFgQueryTicket(uint64_t ticket, RbaFgCoordinatorTicketStatus*);
// Release succeeds only once its queued event and all retained uses have retired.
// Disable never frees an eventData pointer that Unity has not consumed yet.
RBA_FG_COORD_API uint32_t RbaFgReleaseTicket(uint64_t ticket);

// Additive ABI2 entry points. All ABI1 layouts/exports remain available.
// Mode4 selects AMD best 2x; 6 discovers best, 7 selects compatibility 2x,
// and 8 discovers compatibility. Hidden discovery never presents a child image.
enum RbaFgCoordinatorAmdMode : uint32_t
{ RbaFgAmdProbeBest = 6, RbaFgAmdCompatibility2x = 7, RbaFgAmdProbeCompatibility = 8 };
#pragma pack(push, 8)
struct RbaFgCoordinatorConfigV2
{
    uint32_t structSize, abiVersion, mode, verifiedContracts;
    const wchar_t* nvidiaProviderDllPath;
    const wchar_t* nvidiaRuntimeDirectory;
    const wchar_t* amdProviderDllPath;
    const wchar_t* amdRuntimeDirectory;
    uint32_t amdRenderWidth, amdRenderHeight, amdReversedDepth, amdPreferCompatibility;
    // Active AMD requires real render extents; 0/0 discovery uses native output
    // extents. Compatibility must explicitly match modes7/8; best modes4/6 use0.
};
struct RbaFgCoordinatorStatusV2
{
    uint32_t structSize, abiVersion;
    RbaFgCoordinatorStatus base; // Its own size/version remain792/1.
    uint32_t amdBestMultiplierMask, amdCompatibilityMultiplierMask;
    uint32_t amdBestFamily, amdCompatibilityFamily, amdActualFamily;
    uint32_t completedDiscoveryMask; // Bit1 NVIDIA, bit2 AMD best, bit4 AMD compatibility.
    uint64_t amdBestVersionId, amdCompatibilityVersionId, amdActualVersionId;
    uint64_t amdAcceptedRealFrames, amdGeneratedDispatches, amdNativePresentCountDelta;
    uint64_t amdLastAcceptedFrameId, amdLastRetiredFrameId;
    uint32_t amdWorkerPending, amdPresentPermitHeld, amdSdkResult, amdNativePresentCountValid;
};
#pragma pack(pop)
RBA_FG_COORD_API uint32_t RbaFgConfigureV2(const RbaFgCoordinatorConfigV2*); //64bytes.
RBA_FG_COORD_API uint32_t RbaFgGetStatusV2(RbaFgCoordinatorStatusV2*); //904bytes.

// Additive read-only diagnostics ABI1. Counters are process-lifetime totals,
// independent of provider context resets. Drop counts are per real Present
// query while an active mode was requested, not displayed frame measurements.
enum RbaFgDropReason : uint32_t
{
    RbaFgDropNone = 0, RbaFgDropNoCapture = 1, RbaFgDropCaptureRejected = 2,
    RbaFgDropGate = 3, RbaFgDropProviderBusy = 4, RbaFgDropWaitTimeout = 5,
    RbaFgDropTransport = 6, RbaFgDropWindow = 7, RbaFgDropProviderFailure = 8
};
#pragma pack(push, 8)
struct RbaFgCoordinatorDiagnostics
{
    uint32_t structSize, abiVersion, result, lastDropReason;
    uint32_t lastDropFrame, providerMode, lastWaitBudgetMilliseconds, windowRecovering;
    uint64_t realEofFrames, activeQueries;
    uint64_t captureEvents, captureAccepted, captureUnavailable, capturePoolUnavailable, captureCopyFailures;
    uint64_t dropNoCapture, dropCaptureRejected, dropGate, dropProviderBusy;
    uint64_t dropWaitTimeout, dropTransport, dropWindow, dropProviderFailure;
    uint64_t waitCalls, waitCompleted, waitTimeouts, waitTotalMilliseconds, waitLongestMilliseconds, lastWaitMilliseconds;
    uint64_t successfulSubmissions, resetSubmissions, gapSubmissions;
    uint64_t lastMainFrame, finalRealFrame, lastCapturedFrame, lastSubmittedFrame, lastRetiredFrame;
    uint64_t windowEpoch, readyRealFrame, readyAgeMilliseconds; // Age UINT64_MAX when no eligible ready image.
    uint64_t beginSimulationFailures, endSimulationFailures, beginRenderFailures, endRenderFailures;
    uint64_t windowRecoveries;
    uint32_t childVisible, childReason;
};
#pragma pack(pop)
RBA_FG_COORD_API uint32_t RbaFgGetDiagnostics(RbaFgCoordinatorDiagnostics*); //336bytes, abi1.
