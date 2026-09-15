#include "PresentationProbe.h"
#include "CompositionPresenter.h"

#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <IUnityGraphics.h>
#include <IUnityGraphicsD3D11.h>
#include <IUnityRenderingExtensions.h>
#include <atomic>

namespace
{
    IUnityInterfaces* interfaces = nullptr;
    IUnityGraphics* graphics = nullptr;
    IUnityGraphicsD3D11* d3d11 = nullptr;
    std::atomic<uint32_t> requested{0};
    std::atomic<uint32_t> contracts{0};
    std::atomic<uint32_t> currentGeneration{0};
    std::atomic<uint32_t> requestedGeneration{0};
    std::atomic<uint32_t> renderThread{0};
    std::atomic<uint32_t> lastQueryThread{0};
    std::atomic<uint64_t> publishedFrame{0};
    std::atomic<uint64_t> lastQueryFrame{0};
    std::atomic<uint64_t> queryCount{0};
    std::atomic<uint64_t> wrongThreadCount{0};
    std::atomic<uint64_t> queriesWithoutFrame{0};
    std::atomic<uint32_t> observationEnabled{0};
    std::atomic<uint32_t> observationInvalidation{RbaPresentCountReason_Disabled};
    enum class FrameOwner : uint32_t { Unclaimed, Unity, Probe };
    std::atomic<FrameOwner> frameOwner{FrameOwner::Unclaimed};
    SRWLOCK snapshotLock = SRWLOCK_INIT;
    RbaPresentStatus snapshot{};
    CompositionPresenter composition;
    RbaCompositionStatus compositionSnapshot{};
    std::atomic<uint32_t> compositionAttached{0};
    std::atomic<uint32_t> compositionMaintenance{0};
    bool compositionUsesSourceMaintenance = false;

    // Unity serializes rendering/device callbacks. Main-thread exports only
    // touch atomics and the short snapshot lock, never rendering state or COM.
    RbaPresentStatus state{};
    bool eventRegistered = false;
    DXGI_SWAP_CHAIN_DESC observedDescription{};
    bool observedDescriptionValid = false;
    bool expectedUnityCalls = false;
    uint32_t beforeCountEpoch = 0;
    bool beforeCountValid = false;

    void ResetCountEvidence(RbaPresentCountReason reason)
    {
        state.countValid = 0;
        state.countReason = reason;
        state.countResetReason = reason;
        state.lastDelta = 0;
        state.lastBeforeDelta = 0;
        state.lastOwnedDelta = 0;
        state.lastDeltaFrameSpan = 0;
        ++state.countEpoch;
        ++state.countResets;
        observedDescriptionValid = false;
        beforeCountValid = false;
        expectedUnityCalls = false;
    }

    bool DescriptionMatches(const DXGI_SWAP_CHAIN_DESC& a, const DXGI_SWAP_CHAIN_DESC& b)
    {
        return a.BufferDesc.Width == b.BufferDesc.Width && a.BufferDesc.Height == b.BufferDesc.Height &&
            a.BufferDesc.RefreshRate.Numerator == b.BufferDesc.RefreshRate.Numerator &&
            a.BufferDesc.RefreshRate.Denominator == b.BufferDesc.RefreshRate.Denominator &&
            a.BufferDesc.ScanlineOrdering == b.BufferDesc.ScanlineOrdering && a.BufferDesc.Scaling == b.BufferDesc.Scaling &&
            a.BufferDesc.Format == b.BufferDesc.Format && a.SampleDesc.Count == b.SampleDesc.Count &&
            a.SampleDesc.Quality == b.SampleDesc.Quality && a.BufferCount == b.BufferCount &&
            a.BufferUsage == b.BufferUsage && a.OutputWindow == b.OutputWindow && a.Windowed == b.Windowed &&
            a.SwapEffect == b.SwapEffect && a.Flags == b.Flags;
    }

    bool SampleCount(IDXGISwapChain* swapChain, bool afterOwnedPresent, bool certifyOwnedPresent = true)
    {
        DXGI_SWAP_CHAIN_DESC desc{};
        UINT count = 0;
        HRESULT result = swapChain->GetDesc(&desc);
        if (SUCCEEDED(result)) result = swapChain->GetLastPresentCount(&count);
        state.lastCountResult = result;
        if (FAILED(result))
        {
            ++state.countFailures;
            ResetCountEvidence(result == DXGI_ERROR_FRAME_STATISTICS_DISJOINT
                ? RbaPresentCountReason_Disjoint : RbaPresentCountReason_QueryFailed);
            return false;
        }
        const uint64_t identity = reinterpret_cast<uintptr_t>(swapChain);
        bool reset = false;
        if (state.countValid != 0 && (state.chainIdentity != identity || !observedDescriptionValid ||
            !DescriptionMatches(desc, observedDescription)))
        {
            ResetCountEvidence(RbaPresentCountReason_ChainChanged);
            reset = true;
        }
        if (state.countValid != 0 && count < state.lastCount)
        {
            // Count rollback and UINT wrap are both explicit evidence breaks.
            ResetCountEvidence(RbaPresentCountReason_Disjoint);
            reset = true;
        }
        const bool continuous = state.countValid != 0;
        const uint32_t delta = continuous ? count - state.lastCount : 0;
        state.lastDelta = delta;
        state.lastDeltaFrameSpan = continuous ? state.finalFrameId - state.lastSampleFrameId : 0;
        if (continuous)
        {
            ++state.deltaSamples;
            state.countReason = RbaPresentCountReason_Continuous;
            if (!afterOwnedPresent)
            {
                if (expectedUnityCalls)
                {
                    ++state.passiveDeltaSamples;
                    state.passiveCallDeltas += delta;
                }
                else
                    state.unexplainedCallDeltas += delta;
            }
        }
        else if (!reset)
            state.countReason = RbaPresentCountReason_Baseline;
        ++state.countSamples;
        state.countValid = 1;
        state.lastCount = count;
        state.lastSampleFrameId = state.finalFrameId;
        state.chainIdentity = identity;
        observedDescription = desc;
        observedDescriptionValid = true;
        if (afterOwnedPresent)
        {
            state.lastAfterCount = count;
            state.lastOwnedFrameId = state.finalFrameId;
            state.lastOwnedDelta = 0;
            if (certifyOwnedPresent && beforeCountValid && beforeCountEpoch == state.countEpoch && continuous)
            {
                state.lastOwnedDelta = count - state.lastBeforeCount;
                ++state.ownedDeltaSamples;
                if (state.lastOwnedDelta == 1) ++state.ownedSingleCallSamples;
                else ++state.ownedCallMismatches;
            }
            expectedUnityCalls = false;
        }
        else
        {
            state.lastBeforeCount = count;
            state.lastBeforeDelta = delta;
            beforeCountEpoch = state.countEpoch;
            beforeCountValid = true;
        }
        return true;
    }

    void ObserveBeforeQuery()
    {
        const auto invalidation = observationInvalidation.exchange(0);
        if (invalidation != 0) ResetCountEvidence(static_cast<RbaPresentCountReason>(invalidation));
        if (observationEnabled.load() == 0)
        {
            if (state.countValid != 0) ResetCountEvidence(RbaPresentCountReason_Disabled);
            return;
        }
        if (state.loaded == 0 || state.renderer != kUnityGfxRendererD3D11 || interfaces == nullptr)
        {
            ResetCountEvidence(RbaPresentCountReason_MissingInterface);
            return;
        }
        auto* api = interfaces->Get<IUnityGraphicsD3D11>();
        if (api == nullptr || api->GetSwapChain == nullptr)
        {
            ResetCountEvidence(RbaPresentCountReason_MissingInterface);
            return;
        }
        auto* swapChain = api->GetSwapChain();
        if (swapChain == nullptr)
        {
            ResetCountEvidence(RbaPresentCountReason_MissingSwapChain);
            return;
        }
        swapChain->AddRef();
        if (api->GetSyncInterval != nullptr && api->GetPresentFlags != nullptr)
            composition.Observe(swapChain, api->GetSyncInterval(), api->GetPresentFlags());
        SampleCount(swapChain, false);
        swapChain->Release();
    }

    bool YieldToUnity()
    {
        // A query's ownership decision is final for its ticket. In particular,
        // an unexpected-thread query must not undo an existing probe Present,
        // or race a later render-thread query into presenting the ticket twice.
        auto expected = FrameOwner::Unclaimed;
        // An unexpected-thread query cannot issue D3D itself. In maintenance
        // mode let Unity perform this unclaimed source call so its latency
        // budget continues; an already-owned ticket still returns true.
        const auto fallback = compositionAttached.load() != 0 && compositionMaintenance.load() == 0
            ? FrameOwner::Probe : FrameOwner::Unity;
        if (frameOwner.compare_exchange_strong(expected, fallback)) expected = fallback;
        return expected == FrameOwner::Probe;
    }

    void Publish()
    {
        state.requested = requested.load();
        compositionAttached.store(composition.Status().attached);
        compositionMaintenance.store(compositionUsesSourceMaintenance);
        AcquireSRWLockExclusive(&snapshotLock);
        snapshot = state;
        compositionSnapshot = composition.Status();
        ReleaseSRWLockExclusive(&snapshotLock);
    }

    void Stop(RbaPresentReason reason)
    {
        requested.store(0);
        state.reason = reason;
    }

    void ClearFrame()
    {
        state.finalFrameId = 0;
        state.presentedFrameId = 0;
        state.renderThreadId = 0;
        renderThread.store(0);
        publishedFrame.store(0);
        frameOwner.store(FrameOwner::Unclaimed);
        ResetCountEvidence(RbaPresentCountReason_DeviceReset);
    }

    void UNITY_INTERFACE_API OnDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        Stop(RbaPresentReason_DeviceReset);
        composition.Stop();
        d3d11 = nullptr;
        ClearFrame();
        ++state.deviceGeneration;
        currentGeneration.store(state.deviceGeneration);
        if (eventType == kUnityGfxDeviceEventInitialize || eventType == kUnityGfxDeviceEventAfterReset)
        {
            state.renderer = graphics != nullptr && graphics->GetRenderer != nullptr
                ? static_cast<uint32_t>(graphics->GetRenderer()) : static_cast<uint32_t>(kUnityGfxRendererNull);
            // Do not read the unversioned D3D11 interface's appended functions
            // until the harness attests the exact player in RbaPresent_Request.
            state.reason = state.renderer == kUnityGfxRendererD3D11
                ? RbaPresentReason_Passive : RbaPresentReason_UnsupportedRenderer;
        }
        else
        {
            state.renderer = kUnityGfxRendererNull;
        }
        Publish();
    }

    void UNITY_INTERFACE_API OnFinalFrame(int eventId, void* data)
    {
        if (state.loaded == 0 || eventId != state.renderEventId)
            return;
        ++state.finalFrameEvents;
        // Unity promises a valid event payload until this callback returns.
        const auto* frame = static_cast<const RbaPresentFrame*>(data);
        if (frame == nullptr || frame->structSize != sizeof(RbaPresentFrame) || frame->abiVersion != 1 ||
            frame->realFrameId == 0 || frame->realFrameId <= state.finalFrameId)
        {
            ++state.rejectedFrames;
            Stop(RbaPresentReason_InvalidFrame);
            ResetCountEvidence(RbaPresentCountReason_InvalidFrame);
            // Keep the existing ticket/attempt: returning ownership to Unity
            // in the same frame after our Present would present it twice.
            Publish();
            return;
        }
        state.finalFrameId = frame->realFrameId;
        state.renderThreadId = GetCurrentThreadId();
        renderThread.store(state.renderThreadId);
        publishedFrame.store(state.finalFrameId);
        frameOwner.store(FrameOwner::Unclaimed);
        state.reason = requested.load() != 0 ? RbaPresentReason_NoFinalFrame : RbaPresentReason_Passive;
        Publish();
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    // Idempotent even if a diagnostic harness accidentally repeats the load.
    if (state.loaded != 0)
        return;
    requested.store(0);
    contracts.store(0);
    queryCount.store(0);
    wrongThreadCount.store(0);
    queriesWithoutFrame.store(0);
    lastQueryThread.store(0);
    lastQueryFrame.store(0);
    observationEnabled.store(0);
    observationInvalidation.store(0);
    state = {};
    state.structSize = sizeof(state);
    state.abiVersion = 2;
    state.renderEventId = -1;
    state.reason = RbaPresentReason_NotLoaded;
    interfaces = unityInterfaces;
    graphics = interfaces != nullptr && interfaces->GetInterface != nullptr ? interfaces->Get<IUnityGraphics>() : nullptr;
    if (graphics == nullptr || graphics->RegisterDeviceEventCallback == nullptr ||
        graphics->UnregisterDeviceEventCallback == nullptr || graphics->ReserveEventIDRange == nullptr)
    {
        Publish();
        return;
    }
    state.renderEventId = graphics->ReserveEventIDRange(1);
    if (state.renderEventId < 0)
    {
        state.reason = RbaPresentReason_MissingInterface;
        Publish();
        return;
    }
    state.loaded = 1;
    graphics->RegisterDeviceEventCallback(OnDeviceEvent);
    eventRegistered = true;
    // GetRenderer is thread safe; no device/COM calls during plugin load.
    OnDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    Stop(RbaPresentReason_NotLoaded);
    composition.Stop();
    if (eventRegistered && graphics != nullptr)
        graphics->UnregisterDeviceEventCallback(OnDeviceEvent);
    eventRegistered = false;
    state.loaded = 0;
    state.renderEventId = -1;
    state.renderer = kUnityGfxRendererNull;
    ++state.deviceGeneration;
    currentGeneration.store(state.deviceGeneration);
    ClearFrame();
    d3d11 = nullptr;
    graphics = nullptr;
    interfaces = nullptr;
    contracts.store(0);
    observationEnabled.store(0);
    Publish();
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityRenderingExtEvent(UnityRenderingExtEventType, void*)
{
    // No generic rendering extension event is a documented Present callback.
}

extern "C" bool UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityRenderingExtQuery(UnityRenderingExtQueryType query)
{
    if (query != kUnityRenderingExtQueryOverridePresentFrame)
        return false;
    ++queryCount;
    const uint32_t queryThread = GetCurrentThreadId();
    lastQueryThread.store(queryThread);
    lastQueryFrame.store(publishedFrame.load());
    // An unsupported or startup-only query cannot invoke D3D from a wrong
    // thread. Exact-player query cadence must be established before arming.
    const uint32_t expectedThread = renderThread.load();
    if (expectedThread == 0)
    {
        ++queriesWithoutFrame;
        return false;
    }
    if (expectedThread != queryThread)
    {
        // No writes to render-thread state on an unexpected query thread.
        requested.store(0);
        ++wrongThreadCount;
        observationInvalidation.store(RbaPresentCountReason_WrongThread);
        return YieldToUnity();
    }
    ObserveBeforeQuery();
    // A control change may complete one in-flight ticket; use one coherent mode.
    const uint32_t requestedMode = requested.load();
    const bool wantsComposition = requestedMode == 2 || requestedMode == 3;
    const auto owner = frameOwner.load();
    if (owner == FrameOwner::Probe)
    {
        if (!wantsComposition && composition.Exists()) composition.Stop();
        ++state.duplicateQueries;
        Publish();
        return true;
    }
    if (owner == FrameOwner::Unity)
    {
        if (!wantsComposition && composition.Exists()) composition.Stop();
        expectedUnityCalls = true;
        Publish();
        return false;
    }
    const bool modeChange = composition.Exists() && wantsComposition &&
        ((requestedMode == 3) != compositionUsesSourceMaintenance);
    if ((!wantsComposition || modeChange) && composition.Exists()) composition.Stop();
    compositionAttached.store(composition.Status().attached);
    bool maintenanceOnly = (!wantsComposition || modeChange) && compositionAttached.load() != 0 &&
        compositionUsesSourceMaintenance;
    if ((!wantsComposition || modeChange) && compositionAttached.load() != 0 && !maintenanceOnly)
    {
        Stop(RbaPresentReason_PresentFailed);
        expectedUnityCalls = false;
        Publish();
        return YieldToUnity();
    }
    // Never replace an old context's maintenance identity before its detach and
    // GPU retirement have succeeded. A detached pending context can yield Unity.
    if (modeChange && composition.Exists() && !maintenanceOnly)
    {
        expectedUnityCalls = true;
        Publish();
        return YieldToUnity();
    }
    if (state.loaded == 0 || (requestedMode == 0 && !maintenanceOnly) ||
        requestedGeneration.load() != currentGeneration.load())
    {
        expectedUnityCalls = true;
        Publish();
        return YieldToUnity();
    }
    if (state.finalFrameId == 0 || state.renderThreadId == 0)
    {
        state.reason = RbaPresentReason_NoFinalFrame;
        expectedUnityCalls = true;
        Publish();
        return YieldToUnity();
    }
    if (state.renderer != kUnityGfxRendererD3D11 || contracts.load() != RbaPresentContract_All)
    {
        Stop(RbaPresentReason_ContractNotVerified);
        expectedUnityCalls = true;
        Publish();
        return YieldToUnity();
    }
    d3d11 = interfaces->Get<IUnityGraphicsD3D11>();
    if (d3d11 == nullptr || d3d11->GetDevice == nullptr || d3d11->GetSwapChain == nullptr ||
        d3d11->GetSyncInterval == nullptr || d3d11->GetPresentFlags == nullptr || d3d11->GetDevice() == nullptr)
    {
        Stop(RbaPresentReason_MissingInterface);
        expectedUnityCalls = true;
        Publish();
        return YieldToUnity();
    }
    IDXGISwapChain* swapChain = d3d11->GetSwapChain();
    if (swapChain == nullptr)
    {
        Stop(RbaPresentReason_MissingSwapChain);
        expectedUnityCalls = true;
        Publish();
        return YieldToUnity();
    }
    // Never cache a swapchain/backbuffer across Present, resize or device loss.
    // Present can wait for the window thread: never hold the snapshot lock here.
    swapChain->AddRef();
    state.lastSyncInterval = d3d11->GetSyncInterval();
    state.lastPresentFlags = d3d11->GetPresentFlags();
    bool useComposition = wantsComposition && !maintenanceOnly;
    const bool sourceMaintenance = (useComposition && requestedMode == 3) || maintenanceOnly;
    if (useComposition && !composition.Exists())
    {
        compositionUsesSourceMaintenance = sourceMaintenance;
        compositionMaintenance.store(sourceMaintenance);
    }
    if (useComposition && FAILED(composition.Prepare(d3d11->GetDevice(), swapChain,
        state.lastSyncInterval, state.lastPresentFlags, sourceMaintenance)))
    {
        Stop(RbaPresentReason_PresentFailed);
        composition.Stop();
        compositionAttached.store(composition.Status().attached);
        if (composition.Status().attached != 0 && compositionUsesSourceMaintenance)
        {
            // Detach retry must still release one source latency token per
            // new real ticket, or Unity can stop pumping the retry itself.
            useComposition = false;
            maintenanceOnly = true;
        }
        else
        {
            swapChain->Release();
            expectedUnityCalls = true;
            Publish();
            return YieldToUnity();
        }
    }
    auto expectedOwner = FrameOwner::Unclaimed;
    if (!frameOwner.compare_exchange_strong(expectedOwner, FrameOwner::Probe))
    {
        // Another query may have committed normal Unity ownership while the
        // render thread was obtaining its swapchain. Never steal that ticket.
        swapChain->Release();
        if (useComposition) composition.Stop();
        compositionAttached.store(composition.Status().attached);
        expectedUnityCalls = expectedOwner == FrameOwner::Unity;
        Publish();
        return expectedOwner == FrameOwner::Probe;
    }
    ++state.presentAttempts;
    const bool originalCall = !useComposition || sourceMaintenance || maintenanceOnly;
    const HRESULT result = originalCall ? swapChain->Present(state.lastSyncInterval, state.lastPresentFlags) :
        composition.Present(state.lastSyncInterval);
    // Both transports are complete before original Present rotates logical
    // source buffer zero. A failed original attempt is never retried via Unity
    // or followed by another overlay Present for that ticket.
    HRESULT overlayResult = S_OK;
    if (originalCall && useComposition && SUCCEEDED(result))
        overlayResult = composition.Present(state.lastSyncInterval);
    compositionAttached.store(composition.Status().attached);
    if (observationEnabled.load() != 0)
    {
        const auto invalidation = observationInvalidation.exchange(0);
        if (invalidation != 0) ResetCountEvidence(static_cast<RbaPresentCountReason>(invalidation));
        SampleCount(swapChain, true, originalCall && SUCCEEDED(result));
        if (FAILED(result)) ResetCountEvidence(RbaPresentCountReason_PresentFailed);
    }
    swapChain->Release();
    state.lastPresentResult = result;
    state.presentedFrameId = state.finalFrameId;
    if (FAILED(result))
    {
        ++state.presentFailures;
        Stop(RbaPresentReason_PresentFailed);
    }
    else
    {
        ++state.presentSuccesses;
        state.reason = RbaPresentReason_Presented;
    }
    if (FAILED(overlayResult)) Stop(RbaPresentReason_PresentFailed);
    Publish();
    // This query owns the attempt even on failure. Retrying via Unity here
    // would perform a second Present; normal ownership resumes next ticket.
    return true;
}

extern "C" RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_Request(uint32_t enabled, uint32_t verifiedContracts)
{
    if (enabled == 0)
    {
        requested.store(0);
        return 1;
    }
    RbaPresentStatus status{};
    status.structSize = sizeof(status);
    status.abiVersion = 2;
    RbaPresent_GetStatus(&status);
    if (enabled != 1 || verifiedContracts != RbaPresentContract_All ||
        status.loaded == 0 || status.renderer != kUnityGfxRendererD3D11)
        return 0;
    contracts.store(verifiedContracts);
    requestedGeneration.store(status.deviceGeneration);
    requested.store(1);
    return 1;
}

extern "C" RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_Observe(uint32_t enabled, uint32_t verifiedUnityAbi)
{
    if (enabled > 1 || (enabled != 0 && verifiedUnityAbi != RbaPresentContract_Unity6000_5_8f1))
        return 0;
    observationEnabled.store(enabled);
    if (enabled == 0 && (requested.load() == 2 || requested.load() == 3)) requested.store(0);
    observationInvalidation.store(enabled != 0 ? RbaPresentCountReason_Baseline : RbaPresentCountReason_Disabled);
    return 1;
}

extern "C" RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_RequestComposition(uint32_t enabled, uint32_t verifiedContracts)
{
    if (enabled == 0) { requested.store(0); return 1; }
    if (enabled != 1 || verifiedContracts != RbaPresentContract_All || observationEnabled.load() == 0) return 0;
    RbaPresentStatus status{}; status.structSize = sizeof(status); status.abiVersion = 2;
    if (!RbaPresent_GetStatus(&status) || status.loaded == 0 || status.renderer != kUnityGfxRendererD3D11 ||
        status.countValid == 0 || status.renderThreadId == 0 || status.renderThreadId != status.lastQueryThreadId) return 0;
    contracts.store(verifiedContracts);
    requestedGeneration.store(status.deviceGeneration);
    requested.store(2);
    return 1;
}

extern "C" RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_GetCompositionStatus(RbaCompositionStatus* status)
{
    if (status == nullptr || status->structSize != sizeof(RbaCompositionStatus) || status->abiVersion != 1) return 0;
    AcquireSRWLockShared(&snapshotLock);
    *status = compositionSnapshot;
    ReleaseSRWLockShared(&snapshotLock);
    status->structSize = sizeof(*status); status->abiVersion = 1;
    status->requested = requested.load() == 2 || requested.load() == 3;
    return 1;
}

extern "C" RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_RequestCompositionWithSourceMaintenance(uint32_t enabled, uint32_t verifiedContracts)
{
    if (enabled == 0) { requested.store(0); return 1; }
    if (enabled != 1 || verifiedContracts != RbaPresentContract_All || observationEnabled.load() == 0) return 0;
    RbaPresentStatus status{}; status.structSize = sizeof(status); status.abiVersion = 2;
    if (!RbaPresent_GetStatus(&status) || status.loaded == 0 || status.renderer != kUnityGfxRendererD3D11 ||
        status.countValid == 0 || status.renderThreadId == 0 || status.renderThreadId != status.lastQueryThreadId) return 0;
    contracts.store(verifiedContracts); requestedGeneration.store(status.deviceGeneration); requested.store(3); return 1;
}

#ifdef RBA_COMPOSITION_TESTING
extern "C" __declspec(dllexport) void __cdecl RbaPresent_TestFailNextDetach()
{
    composition.FailNextDetach();
}
#endif

extern "C" RBA_PRESENT_API int32_t RBA_PRESENT_CALL RbaPresent_GetStatus(RbaPresentStatus* status)
{
    if (status == nullptr || status->structSize != sizeof(RbaPresentStatus) || status->abiVersion != 2)
        return 0;
    AcquireSRWLockShared(&snapshotLock);
    *status = snapshot;
    ReleaseSRWLockShared(&snapshotLock);
    status->structSize = sizeof(RbaPresentStatus);
    status->abiVersion = 2;
    status->observationEnabled = observationEnabled.load();
    if (status->observationEnabled == 0) status->countValid = 0;
    const auto pendingInvalidation = observationInvalidation.load();
    if (pendingInvalidation != 0)
    {
        status->countValid = 0;
        status->countReason = pendingInvalidation;
    }
    status->requested = requested.load();
    status->presentQueries = queryCount.load();
    status->wrongThreadQueries = wrongThreadCount.load();
    status->queriesWithoutFinalFrame = queriesWithoutFrame.load();
    status->lastQueryThreadId = lastQueryThread.load();
    status->lastQueryFrameId = lastQueryFrame.load();
    if (status->lastQueryThreadId != 0 && status->renderThreadId != 0 &&
        status->lastQueryThreadId != status->renderThreadId)
        status->reason = RbaPresentReason_WrongThread;
    return 1;
}

extern "C" RBA_PRESENT_API void* RBA_PRESENT_CALL RbaPresent_GetRenderEventFunc()
{
    return reinterpret_cast<void*>(&OnFinalFrame);
}
