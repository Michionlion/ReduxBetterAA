#include "FrameGenerationCoordinator.h"
#include "ChildWindowHost.h"
#include "../Streamline/StreamlineProvider.h"
#include "../FrameGeneration/FrameGenerationInputPool.h"
#include "../FrameGeneration/AmdFrameGenerationProvider.h"
#include <Windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <IUnityGraphics.h>
#include <IUnityGraphicsD3D11.h>
#include <IUnityRenderingExtensions.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>

using Microsoft::WRL::ComPtr;
static_assert(sizeof(RbaFgCoordinatorConfig) == 32 && sizeof(RbaFgCoordinatorCamera) == 304 &&
    sizeof(RbaFgCoordinatorCapture) == 368 && sizeof(RbaFgCoordinatorTicketStatus) == 40 &&
    sizeof(RbaFgCoordinatorStatus) == 792, "Coordinator ABI1 layout");
static_assert(sizeof(RbaFgCoordinatorCamera) == sizeof(RbaSlFgCamera), "Camera forwarding layout");
static_assert(sizeof(RbaFgCoordinatorConfigV2) == 64 && sizeof(RbaFgCoordinatorStatusV2) == 904 &&
    sizeof(RbaFgCoordinatorCamera) == sizeof(RbaAmdProviderCamera), "Additive AMD ABI2 layout");
static_assert(sizeof(RbaFgCoordinatorDiagnostics) == 336, "Read-only diagnostics ABI1 layout");

namespace
{
    struct Guard { SRWLOCK& lock; explicit Guard(SRWLOCK& l) : lock(l) { AcquireSRWLockExclusive(&lock); } ~Guard() { ReleaseSRWLockExclusive(&lock); } };
    enum class Owner : uint32_t { Unclaimed, Unity, Coordinator };
    struct Ticket
    {
        uint64_t id = 0, captureId = 0;
        uint32_t kind = 0, frame = 0, references = 0, result = RbaFgPending;
        bool consumed = false, sourceRetired = false, retired = false;
        bool inputAcquired = false, readyQueued = false, retireQueued = false, submitted = false;
        bool inputBusy = false;
        RbaFgCoordinatorCapture capture{};
        RbaFgInputLease input{};
    };
    struct ProviderApi
    {
        HMODULE module = nullptr;
        decltype(&RbaSlFgCreate) create = nullptr;
        decltype(&RbaSlFgGetInterfaces) interfaces = nullptr;
        decltype(&RbaSlFgGetStatus) status = nullptr;
        decltype(&RbaSlFgBeginSimulation) beginSimulation = nullptr;
        decltype(&RbaSlFgEndSimulation) endSimulation = nullptr;
        decltype(&RbaSlFgBeginRender) beginRender = nullptr;
        decltype(&RbaSlFgEndRender) endRender = nullptr;
        decltype(&RbaSlFgDiscardFrame) discard = nullptr;
        decltype(&RbaSlFgSubmit) submit = nullptr;
        decltype(&RbaSlFgPoll) poll = nullptr;
        decltype(&RbaSlFgDestroy) destroy = nullptr;
    } api;
    struct AmdApi
    {
        HMODULE module = nullptr;
        decltype(&RbaAmdProviderCreate) create = nullptr;
        decltype(&RbaAmdProviderGetInterfaces) interfaces = nullptr;
        decltype(&RbaAmdProviderSubmit) submit = nullptr;
        decltype(&RbaAmdProviderPoll) poll = nullptr;
        decltype(&RbaAmdProviderDestroy) destroy = nullptr;
    } amdApi;
    struct Control
    {
        std::wstring providerPath, runtimePath;
        std::wstring amdProviderPath, amdRuntimePath;
        uint64_t epoch = 1;
        uint32_t mode = 0, contracts = 0;
        uint32_t amdRenderWidth = 0, amdRenderHeight = 0, amdReversedDepth = 0, amdCompatibility = 0;
    } control;
    SRWLOCK lock = SRWLOCK_INIT;
    std::array<Ticket, 24> tickets{};
    std::array<uint32_t, 8> renderingFrames{};
    uint64_t nextTicket = 0;
    RbaFgCoordinatorStatus snapshot{}, state{};
    RbaFgCoordinatorStatusV2 details{}, detailsSnapshot{};
    RbaFgCoordinatorDiagnostics diagnostics{}, diagnosticsSnapshot{};
    std::atomic<uint32_t> requested{0}, eligible{0}, renderThread{0}, mainThread{0};
    std::atomic<uint64_t> providerPublished{0}, controlEpoch{1}, finalFramePublished{0};
    std::atomic<uint64_t> queryCount{0}, wrongThreadCount{0};
    std::atomic<uint32_t> queryThread{0};
    std::atomic<Owner> frameOwner{Owner::Unclaimed};
    std::atomic<bool> blocked{false}, nativeDraining{false}, windowRecovery{false};
    std::atomic<uint32_t> completedProbes{0};
    std::atomic<uint64_t> lastMainFrame{0};
    std::atomic<uint64_t> softFallbackEpoch{0};
    std::array<std::atomic<uint64_t>, 4> markerFailures{};
    std::atomic<HMODULE> providerModulePublished{nullptr}, amdModulePublished{nullptr};
    IUnityInterfaces* unity = nullptr;
    IUnityGraphics* graphics = nullptr;
    IUnityGraphicsD3D11* unity11 = nullptr;
    bool registered = false;
    ChildWindowHost host;
    ChildWindowTarget hostLease{};
    uint64_t provider = 0, providerEpoch = 0, currentCapture = 0, completionValue = 0;
    uint64_t providerSourceChain = 0, providerSourceWindow = 0;
    uint32_t providerMode = 0;
    void* amdProvider = nullptr;
    bool amdPresentPermit = false;
    uint32_t amdContextRenderWidth = 0, amdContextRenderHeight = 0, amdContextReversedDepth = 0;
    uint64_t providerRetiredFrame = 0;
    void* pool = nullptr;
    uint32_t poolRenderWidth = 0, poolRenderHeight = 0, poolMotion = 0;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue12;
    ComPtr<ID3D12Fence> completionFence;
    RbaSlFgStatus providerStatus{};
    RbaAmdProviderStatus amdStatus{};
    uint64_t countChain = 0;
    uint32_t lastCount = 0;
    bool countValid = false, previousOwned = false;
    bool outputObserved = false;
    uint32_t outputColorSpace = 0, outputBitsPerColor = 0;
    constexpr const char* SoftFallbackReason =
        "Frame generation paused after 250 ms without a completed image. Switch Frame generation Off, then select it again to retry.";
    bool SoftFallback() { const auto epoch = softFallbackEpoch.load(); return epoch != 0 && epoch == controlEpoch.load(); }

    Ticket* Find(uint64_t id) { for (auto& t : tickets) if (t.id == id && id != 0) return &t; return nullptr; }
    RbaSlFgStatus ProviderStatus() { RbaSlFgStatus s{}; s.structSize = sizeof(s); s.abiVersion = 1; return s; }
    RbaAmdProviderStatus AmdStatus() { RbaAmdProviderStatus s{}; s.structSize = sizeof(s); s.abiVersion = 1; return s; }
    RbaFgInputStatus PoolStatus() { RbaFgInputStatus s{}; s.structSize = sizeof(s); s.abiVersion = 1; return s; }
    void Publish()
    {
        Guard guard(lock); snapshot = state; detailsSnapshot = details; diagnosticsSnapshot = diagnostics;
        diagnosticsSnapshot.providerMode = provider != 0 || amdProvider != nullptr ? providerMode : 0;
        if (outputObserved) snprintf(snapshot.reason, sizeof(snapshot.reason),
            "[monitor ColorSpace=%u bits=%u] %.450s", outputColorSpace, outputBitsPerColor, state.reason);
        if (provider != 0)
        {
            char telemetry[160]{};
            snprintf(telemetry, sizeof(telemetry), " [NV status=%u SDKlast=%u native=%llu valid=%u hr=0x%08X async=%u]",
                providerStatus.sdkRuntimeStatus, providerStatus.lastSdkPresented,
                static_cast<unsigned long long>(providerStatus.nativePresentCountDelta), providerStatus.nativePresentCountValid,
                providerStatus.lastPresentHresult, providerStatus.asyncApiErrors);
            const size_t length = std::strlen(snapshot.reason), extra = std::strlen(telemetry);
            // Preserve the first SDK error. Long errors take precedence over
            // optional numeric telemetry; never truncate them to make room.
            if (length + extra < sizeof(snapshot.reason)) std::memcpy(snapshot.reason + length, telemetry, extra + 1);
        }
    }
    void Reason(uint32_t result, const char* reason, bool terminal = false)
    {
        state.result = result; snprintf(state.reason, sizeof(state.reason), "%s", reason);
        if (terminal) { blocked.store(true); state.state = RbaFgBlocked; }
    }
    void RequestWindowRecovery(const char* reason)
    {
        // A rejected, unborrowed frame can race an ordinary minimize/resize.
        // Stop future work, but retain all existing retirement obligations.
        if (!windowRecovery.exchange(true)) ++diagnostics.windowRecoveries;
        nativeDraining.store(true); providerPublished.store(0);
        if (hostLease.leaseId != 0) host.ClearFrameReady(hostLease);
        state.state = RbaFgDraining;
        char message[512]{};
        snprintf(message, sizeof(message), "Window changed; draining before owner-thread rearm: %.430s", reason);
        Reason(RbaFgPending, message);
    }
    void NvidiaFailure(uint32_t result, const RbaSlFgStatus& s)
    {
        if (result == RbaSlFgWindowChanged && s.poisoned == 0 && s.inputBatchPending == 0 &&
            s.asyncApiErrors == 0 && s.sdkRuntimeStatus == 0 && !blocked.load())
            RequestWindowRecovery(s.reason);
        else Reason(result == RbaSlFgUnsupported ? RbaFgUnsupported : RbaFgProviderError, s.reason, true);
    }
    void AmdFailure(uint32_t result, const RbaAmdProviderStatus& s)
    {
        if (result == RbaAmdProviderWindowChanged && s.poisoned == 0 && s.inputBatchPending == 0 && !blocked.load())
            RequestWindowRecovery(s.reason);
        else Reason(result == RbaAmdProviderUnsupported ? RbaFgUnsupported : RbaFgProviderError, s.reason, true);
    }
    void RecordDrop(uint32_t reason)
    {
        diagnostics.lastDropReason = reason; diagnostics.lastDropFrame = static_cast<uint32_t>(state.finalRealFrameId);
        switch (reason)
        {
        case RbaFgDropNoCapture: ++diagnostics.dropNoCapture; break;
        case RbaFgDropCaptureRejected: ++diagnostics.dropCaptureRejected; break;
        case RbaFgDropGate: ++diagnostics.dropGate; break;
        case RbaFgDropProviderBusy: ++diagnostics.dropProviderBusy; break;
        case RbaFgDropWaitTimeout: ++diagnostics.dropWaitTimeout; break;
        case RbaFgDropTransport: ++diagnostics.dropTransport; break;
        case RbaFgDropWindow: ++diagnostics.dropWindow; break;
        case RbaFgDropProviderFailure: ++diagnostics.dropProviderFailure; break;
        default: break;
        }
    }
    void RecordWait(uint64_t started, bool retired)
    {
        const uint64_t elapsed = GetTickCount64() - started;
        ++diagnostics.waitCalls; diagnostics.lastWaitBudgetMilliseconds = 50;
        diagnostics.lastWaitMilliseconds = elapsed; diagnostics.waitTotalMilliseconds += elapsed;
        if (elapsed > diagnostics.waitLongestMilliseconds) diagnostics.waitLongestMilliseconds = elapsed;
        if (retired) ++diagnostics.waitCompleted;
    }
    void RecordSubmission(uint32_t frame, uint64_t previous, uint32_t reset)
    {
        ++diagnostics.successfulSubmissions;
        if (reset) ++diagnostics.resetSubmissions;
        if (previous != 0 && frame != previous + 1) ++diagnostics.gapSubmissions;
    }
    Control ReadControl() { Guard guard(lock); return control; }
    bool IsNvidia(uint32_t mode) { return mode >= RbaFgNvidia2x && mode <= RbaFgNvidia4x; }
    bool IsAmd(uint32_t mode) { return mode == RbaFgAmd2x || mode == RbaFgAmdCompatibility2x; }
    bool IsAmdProbe(uint32_t mode) { return mode == RbaFgAmdProbeBest || mode == RbaFgAmdProbeCompatibility; }
    bool IsProbe(uint32_t mode) { return mode == RbaFgNvidiaProbe || IsAmdProbe(mode); }
    bool HasProvider() { return provider != 0 || amdProvider != nullptr; }
    uint32_t ProbeBit(uint32_t mode) { return mode == RbaFgNvidiaProbe ? 1u : mode == RbaFgAmdProbeBest ? 2u : mode == RbaFgAmdProbeCompatibility ? 4u : 0u; }
    uint32_t Multiplier(uint32_t mode) { return mode == RbaFgNvidiaProbe ? 2 : mode + 1; }
    constexpr bool SubmissionOwnsInput(uint32_t result, uint32_t inputBatchPending)
    { return result == RbaSlFgOk || inputBatchPending != 0; }
    // The provider publishes pending before its first resource tag. An earlier
    // error can poison a context without borrowing this batch at all.
    static_assert(!SubmissionOwnsInput(RbaSlFgPoisoned, 0) && SubmissionOwnsInput(RbaSlFgPoisoned, 1) &&
        SubmissionOwnsInput(RbaSlFgOk, 0) && !SubmissionOwnsInput(RbaSlFgBusy, 0), "Pre-tag failure must retire unused pool input");
    bool YieldUnity()
    {
        auto owner = Owner::Unclaimed;
        frameOwner.compare_exchange_strong(owner, Owner::Unity);
        return frameOwner.load() == Owner::Coordinator;
    }
    void DropMarker(uint32_t frame)
    {
        if (provider != 0 && api.discard != nullptr) api.discard(provider, frame);
        Guard guard(lock); for (auto& id : renderingFrames) if (id == frame) id = 0;
    }
    void UpdateProvider(const RbaSlFgStatus& s)
    {
        providerStatus = s;
        state.providerResult = s.result; state.providerPending = s.inputBatchPending;
        if (s.supportedMultiplierMask != 0) state.nvidiaMultiplierMask = s.supportedMultiplierMask;
        state.actualMultiplier = providerMode == RbaFgNvidiaProbe ? 0 : s.multiplier;
        state.providerRealPresents = s.realPresentCalls;
        state.providerSdkPresentations = s.sdkReportedPresentations;
        state.generatedPresentationObserved = s.generatedPresentationObserved;
        if (s.lastRetiredFrameId > state.lastRetiredFrameId) state.lastRetiredFrameId = s.lastRetiredFrameId;
        providerRetiredFrame = s.lastRetiredFrameId;
    }
    void UpdateAmd(const RbaAmdProviderStatus& s)
    {
        amdStatus = s; providerRetiredFrame = s.lastRetiredFrameId;
        state.providerResult = s.result; state.providerPending = s.inputBatchPending;
        state.actualMultiplier = IsAmdProbe(providerMode) ? 0 : s.activeMultiplier;
        state.providerRealPresents = s.realPresentCalls; state.providerSdkPresentations = 0;
        state.generatedPresentationObserved = s.nativePresentCountValid && s.generatedDispatches != 0 && s.nativePresentCountDelta > s.realPresentCalls;
        if (s.lastRetiredFrameId > state.lastRetiredFrameId) state.lastRetiredFrameId = s.lastRetiredFrameId;
        details.amdActualFamily = s.providerFamily; details.amdActualVersionId = s.providerVersionId;
        details.amdAcceptedRealFrames = s.acceptedRealFrames; details.amdGeneratedDispatches = s.generatedDispatches;
        details.amdNativePresentCountDelta = s.nativePresentCountDelta; details.amdNativePresentCountValid = s.nativePresentCountValid;
        details.amdLastAcceptedFrameId = s.lastAcceptedFrameId; details.amdLastRetiredFrameId = s.lastRetiredFrameId;
        details.amdWorkerPending = s.inputBatchPending; details.amdSdkResult = s.sdkResult;
        if (s.initialized && s.supportedMultiplierMask != 0)
        {
            if (providerMode == RbaFgAmdCompatibility2x || providerMode == RbaFgAmdProbeCompatibility)
            { details.amdCompatibilityMultiplierMask = s.supportedMultiplierMask; details.amdCompatibilityFamily = s.providerFamily; details.amdCompatibilityVersionId = s.providerVersionId; }
            else { details.amdBestMultiplierMask = s.supportedMultiplierMask; details.amdBestFamily = s.providerFamily; details.amdBestVersionId = s.providerVersionId; }
        }
    }
    bool LoadAmd(const Control& desired)
    {
        if (amdApi.module != nullptr) return true;
        if (desired.amdProviderPath.empty() || desired.amdRuntimePath.empty()) return false;
        HMODULE module = LoadLibraryExW(desired.amdProviderPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (module == nullptr) return false;
        wchar_t actual[32768]{};
        if (!GetModuleFileNameW(module, actual, static_cast<DWORD>(std::size(actual))) ||
            _wcsicmp(std::filesystem::path(actual).lexically_normal().c_str(), std::filesystem::path(desired.amdProviderPath).lexically_normal().c_str()) != 0)
        { FreeLibrary(module); return false; }
        AmdApi loaded{}; loaded.module = module;
#define RBA_AMD_LOAD(member, name) loaded.member = reinterpret_cast<decltype(loaded.member)>(GetProcAddress(module, name)); if (!loaded.member) { FreeLibrary(module); return false; }
        RBA_AMD_LOAD(create, "RbaAmdProviderCreate") RBA_AMD_LOAD(interfaces, "RbaAmdProviderGetInterfaces")
        RBA_AMD_LOAD(submit, "RbaAmdProviderSubmit") RBA_AMD_LOAD(poll, "RbaAmdProviderPoll") RBA_AMD_LOAD(destroy, "RbaAmdProviderDestroy")
#undef RBA_AMD_LOAD
        amdApi = loaded; amdModulePublished.store(module); return true;
    }
    bool LoadProvider(const Control& desired)
    {
        if (api.module != nullptr) return true;
        if (desired.providerPath.empty() || desired.runtimePath.empty()) return false;
        HMODULE module = LoadLibraryExW(desired.providerPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (module == nullptr) return false;
        wchar_t actual[32768]{};
        if (!GetModuleFileNameW(module, actual, static_cast<DWORD>(std::size(actual))) ||
            _wcsicmp(std::filesystem::path(actual).lexically_normal().c_str(), std::filesystem::path(desired.providerPath).lexically_normal().c_str()) != 0)
        { FreeLibrary(module); return false; }
        ProviderApi loaded{}; loaded.module = module;
#define RBA_LOAD(member, name) loaded.member = reinterpret_cast<decltype(loaded.member)>(GetProcAddress(module, name)); if (!loaded.member) { FreeLibrary(module); return false; }
        RBA_LOAD(create, "RbaSlFgCreate") RBA_LOAD(interfaces, "RbaSlFgGetInterfaces") RBA_LOAD(status, "RbaSlFgGetStatus")
        RBA_LOAD(beginSimulation, "RbaSlFgBeginSimulation") RBA_LOAD(endSimulation, "RbaSlFgEndSimulation")
        RBA_LOAD(beginRender, "RbaSlFgBeginRender") RBA_LOAD(endRender, "RbaSlFgEndRender") RBA_LOAD(discard, "RbaSlFgDiscardFrame")
        RBA_LOAD(submit, "RbaSlFgSubmit") RBA_LOAD(poll, "RbaSlFgPoll") RBA_LOAD(destroy, "RbaSlFgDestroy")
#undef RBA_LOAD
        // This module is pinned for the native plugin lifetime. Marker readers
        // never race a FreeLibrary; the provider separately owns SDK lifetimes.
        api = loaded; providerModulePublished.store(module); return true;
    }
    void RetireUnused(Ticket& t)
    {
        if (!t.inputAcquired || t.retireQueued) return;
        auto s = PoolStatus();
        if (!t.readyQueued)
        {
            RbaFgInputCancel(pool, &t.input, &s); return;
        }
        // Queue signal is ordered after QueueReady's source-copy dependency.
        // For submitted input this runs only AFTER provider Poll proves all SDK
        // and caller-queue users finished; no CPU-signaled fake completion.
        if (!queue12 || !completionFence || FAILED(queue12->Signal(completionFence.Get(), ++completionValue)))
        { Reason(RbaFgQuarantined, "Input retirement queue signal failed", true); return; }
        if (RbaFgInputRetire(pool, &t.input, completionFence.Get(), completionValue, &s) == RbaFgInputOk)
            t.retireQueued = true;
        else Reason(RbaFgQuarantined, s.error, true);
    }
    void PollInputs()
    {
        if (pool == nullptr) return;
        uint32_t pending = 0;
        for (auto& t : tickets)
        {
            bool acquired = false;
            { Guard guard(lock); acquired = t.id != 0 && t.inputAcquired && !t.retired; }
            if (!acquired) continue;
            if (t.readyQueued && !t.retireQueued && (!t.submitted || providerRetiredFrame >= t.frame)) RetireUnused(t);
            auto s = PoolStatus(); const auto result = RbaFgInputQueryLease(pool, &t.input, &s);
            {
                Guard guard(lock);
                if (result == RbaFgInputOk || result == RbaFgInputBusy)
                    t.sourceRetired = t.sourceRetired || s.sceneSourcesRetired != 0;
                if (result == RbaFgInputOk && s.retired != 0) { t.retired = true; t.result = RbaFgOk; }
                else ++pending;
            }
            if (result == RbaFgInputDeviceError || s.poisoned != 0) Reason(RbaFgQuarantined, s.error, true);
        }
        state.inputPoolPending = pending;
    }
    void DropCapture(uint64_t id, bool softBackpressure = false)
    {
        Ticket* t = nullptr;
        bool releaseExisting = false;
        { Guard guard(lock); t = Find(id); if (t != nullptr) { releaseExisting = t->references != 0; ++t->references; } }
        if (t == nullptr) return;
        if (t->inputAcquired && !t->submitted) RetireUnused(*t);
        DropMarker(t->frame);
        if (hostLease.leaseId != 0)
        {
            // SDK reset can mean an input gap, not a changed scene. Keep its
            // semantics separate from hard presentation eligibility gates.
            if (softBackpressure) host.HoldFrameReady(hostLease);
            else host.ClearFrameReady(hostLease);
        }
        { Guard guard(lock); --t->references; if (releaseExisting) --t->references; }
    }
    bool PollProvider(uint32_t nvidiaTimeoutMilliseconds = 0)
    {
        if (amdProvider != nullptr)
        {
            auto s = AmdStatus(); const auto result = amdApi.poll(amdProvider, &s); UpdateAmd(s);
            if (amdPresentPermit && s.inputBatchPending == 0 && s.lastRetiredFrameId >= s.lastAcceptedFrameId)
            {
                if (host.EndPresent(hostLease)) amdPresentPermit = false;
                else Reason(RbaFgQuarantined, "AMD retired on a different presentation thread; retain HWND lease", true);
            }
            details.amdPresentPermitHeld = amdPresentPermit;
            if (result != RbaAmdProviderOk && result != RbaAmdProviderBusy) AmdFailure(result, s);
            if (result == RbaAmdProviderOk && s.poisoned == 0 && s.lastRetiredFrameId != 0 &&
                IsAmd(providerMode) && !amdPresentPermit && !blocked.load() && !nativeDraining.load())
            { host.MarkFrameReady(hostLease, s.lastRetiredFrameId); state.state = RbaFgActive; Reason(RbaFgOk, "AMD child real frame completed and inputs retired"); }
            PollInputs(); return result == RbaAmdProviderOk && s.inputBatchPending == 0 && !s.poisoned;
        }
        if (provider == 0) { PollInputs(); return true; }
        auto s = ProviderStatus(); const auto result = api.poll(provider, nvidiaTimeoutMilliseconds, &s); UpdateProvider(s);
        if (result != RbaSlFgOk && result != RbaSlFgBusy) NvidiaFailure(result, s);
        if (result == RbaSlFgOk && s.poisoned == 0 && s.lastRetiredFrameId != 0 &&
            providerMode != RbaFgNvidiaProbe && hostLease.leaseId != 0 && !blocked.load() && !nativeDraining.load())
            host.MarkFrameReady(hostLease, s.lastRetiredFrameId);
        PollInputs();
        return result == RbaSlFgOk && s.inputBatchPending == 0 && !s.poisoned;
    }
    bool DestroyInputPool()
    {
        if (pool == nullptr) return true;
        auto s = PoolStatus(); const auto result = RbaFgInputDestroy(pool, &s);
        if (result != RbaFgInputOk)
        { if (result != RbaFgInputBusy) Reason(RbaFgQuarantined, s.error, true); return false; }
        pool = nullptr; poolRenderWidth = poolRenderHeight = poolMotion = 0; state.inputPoolPending = 0;
        // Destroy itself polls fences. They can retire after the preceding
        // PollInputs snapshot; preserve that positive proof before deleting
        // the last place from which a managed ticket could query retirement.
        Guard guard(lock);
        for (auto& t : tickets) if (t.id != 0 && t.inputAcquired)
        { t.sourceRetired = t.retired = true; t.result = RbaFgOk; }
        return true;
    }
    bool Drain()
    {
        nativeDraining.store(true); state.state = RbaFgDraining; providerPublished.store(0);
        if (currentCapture != 0) { DropCapture(currentCapture); currentCapture = 0; }
        PollProvider();
        for (auto& t : tickets)
        {
            bool cancel = false;
            { Guard guard(lock); cancel = t.id != 0 && t.consumed && t.inputAcquired && !t.submitted && !t.retired; }
            if (cancel) RetireUnused(t);
        }
        PollInputs();
        if (state.providerPending != 0 &&
            ((provider != 0 && !providerStatus.poisoned) || (amdProvider != nullptr && !amdStatus.poisoned)))
            return false; // Healthy input completes through Poll before SDK shutdown.
        if (state.providerPending == 0)
        {
            // Normal reconfiguration retires every pool copy/queue tail and
            // releases its D3D12 resources before slShutdown/worker teardown.
            // Poisoned pending input may instead need provider DestroyOk below
            // before its pool retirement can be proven.
            if (!DestroyInputPool()) return false;
            completionFence.Reset();
        }
        if (amdProvider != nullptr)
        {
            auto s = AmdStatus(); const auto result = amdApi.destroy(amdProvider, &s); UpdateAmd(s);
            if (result != RbaAmdProviderOk)
            { if (result != RbaAmdProviderBusy) Reason(RbaFgQuarantined, s.reason, true); return false; }
            amdProvider = nullptr;
            // DestroyOk proves restoration/retirement even for a poisoned batch
            // that never reached the ordinary successful lastRetired watermark.
            { Guard guard(lock); for (const auto& t : tickets) if (t.inputAcquired && t.submitted && t.frame > providerRetiredFrame) providerRetiredFrame = t.frame; }
            if (amdPresentPermit)
            {
                if (!host.EndPresent(hostLease)) { Reason(RbaFgQuarantined, "AMD cleanup cannot acknowledge an old-thread Present permit", true); return false; }
                amdPresentPermit = false;
            }
            details.amdPresentPermitHeld = 0; details.amdWorkerPending = 0;
            PollInputs();
        }
        if (provider != 0)
        {
            auto s = ProviderStatus(); const auto result = api.destroy(provider, 0, &s); UpdateProvider(s);
            if (result != RbaSlFgOk)
            { if (result != RbaSlFgBusy) Reason(RbaFgQuarantined, s.reason, true); return false; }
            provider = 0;
        }
        if (!DestroyInputPool()) return false;
        completionFence.Reset(); queue12.Reset(); device12.Reset(); completionValue = 0;
        if (hostLease.leaseId != 0)
        {
            if (!host.ReleaseRenderLease(hostLease)) return false;
            hostLease = {};
        }
        amdContextRenderWidth = amdContextRenderHeight = 0;
        nativeDraining.store(false); state.providerPending = 0; state.inputPoolPending = 0; state.actualMultiplier = 0;
        state.state = requested.load() != RbaFgOff && (state.nvidiaMultiplierMask != 0 ||
            details.amdBestMultiplierMask != 0 || details.amdCompatibilityMultiplierMask != 0) ? RbaFgReady : RbaFgDisabled;
        return true;
    }
    bool EnsureAmdProvider(ID3D11Device* source, const Control& desired)
    {
        if (amdProvider == nullptr)
        {
            uint32_t width = desired.amdRenderWidth, height = desired.amdRenderHeight;
            if (IsAmdProbe(desired.mode) && width == 0 && height == 0) { width = state.sourceWidth; height = state.sourceHeight; }
            if (width == 0 || height == 0 || width > state.sourceWidth || height > state.sourceHeight)
            { state.state = RbaFgStarting; Reason(RbaFgPending, "AMD initialization is waiting for actual render extents"); return false; }
            if (!LoadAmd(desired)) { Reason(RbaFgProviderError, "Pinned AMD provider DLL could not be loaded", true); return false; }
            if (hostLease.leaseId == 0 && !host.AcquireRenderLease(hostLease)) return false;
            providerEpoch = desired.epoch; providerMode = desired.mode; providerRetiredFrame = 0;
            providerSourceChain = countChain; providerSourceWindow = state.sourceWindow;
            RbaAmdProviderCreateDesc create{}; create.structSize = sizeof(create); create.abiVersion = 1;
            create.runtimeDirectory = desired.amdRuntimePath.c_str(); create.sourceD3D11Device = source;
            create.childHwnd = hostLease.window; create.windowEpoch = hostLease.epoch;
            create.renderWidth = width; create.renderHeight = height;
            create.displayWidth = hostLease.width; create.displayHeight = hostLease.height;
            create.reversedDepth = desired.amdReversedDepth; create.preferCompatibility = desired.amdCompatibility;
            amdContextRenderWidth = width; amdContextRenderHeight = height; amdContextReversedDepth = desired.amdReversedDepth;
            auto s = AmdStatus(); const auto result = amdApi.create(&create, &amdProvider, &s); UpdateAmd(s);
            if (result != RbaAmdProviderOk && result != RbaAmdProviderBusy)
            { AmdFailure(result, s); Drain(); return false; }
            state.state = RbaFgStarting; Reason(RbaFgPending, "AMD provider worker is initializing");
        }
        if (amdProvider == nullptr || !amdStatus.initialized || amdStatus.poisoned) return false;
        if (!queue12)
        {
            RbaAmdProviderInterfaces interfaces{}; interfaces.structSize = sizeof(interfaces); interfaces.abiVersion = 1;
            if (amdApi.interfaces(amdProvider, &interfaces) != RbaAmdProviderOk || !interfaces.device12 || !interfaces.queue12)
            { Reason(RbaFgProviderError, "AMD provider did not expose initialized native D3D12 interfaces", true); Drain(); return false; }
            device12 = static_cast<ID3D12Device*>(interfaces.device12); queue12 = static_cast<ID3D12CommandQueue*>(interfaces.queue12);
            if (FAILED(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&completionFence))))
            { Reason(RbaFgDeviceError, "AMD coordinator completion fence creation failed", true); Drain(); return false; }
            state.state = RbaFgReady; Reason(RbaFgOk, "AMD worker initialized; waiting for eligible real frame");
        }
        return true;
    }
    bool EnsureProvider(ID3D11Device* source, const Control& desired)
    {
        if (provider != 0) return true;
        if (!LoadProvider(desired)) { Reason(RbaFgProviderError, "Pinned provider DLL could not be loaded", true); return false; }
        if (hostLease.leaseId == 0 && !host.AcquireRenderLease(hostLease)) return false;
        RbaSlFgCreateDesc create{}; create.structSize = sizeof(create); create.abiVersion = 1;
        create.runtimeDirectory = desired.runtimePath.c_str(); create.sourceD3D11Device = source;
        create.childHwnd = hostLease.window; create.windowEpoch = hostLease.epoch;
        create.width = hostLease.width; create.height = hostLease.height; create.multiplier = Multiplier(desired.mode);
        providerEpoch = desired.epoch; providerMode = desired.mode; providerRetiredFrame = 0;
        providerSourceChain = countChain; providerSourceWindow = state.sourceWindow;
        auto s = ProviderStatus(); const auto result = api.create(&create, &provider, &s); UpdateProvider(s);
        if (result != RbaSlFgOk)
        { NvidiaFailure(result, s); Drain(); return false; }
        RbaSlFgInterfaces interfaces{}; interfaces.structSize = sizeof(interfaces); interfaces.abiVersion = 1;
        if (api.interfaces(provider, &interfaces) != RbaSlFgOk || interfaces.device12 == nullptr || interfaces.queue12 == nullptr)
        { Reason(RbaFgProviderError, "Provider did not expose native D3D12 interfaces", true); Drain(); return false; }
        device12 = static_cast<ID3D12Device*>(interfaces.device12); queue12 = static_cast<ID3D12CommandQueue*>(interfaces.queue12);
        if (FAILED(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&completionFence))))
        { Reason(RbaFgDeviceError, "Coordinator completion fence creation failed", true); Drain(); return false; }
        state.state = RbaFgReady; Reason(RbaFgOk, "Provider initialized; waiting for eligible real frame");
        if (desired.mode != RbaFgNvidiaProbe) providerPublished.store(provider);
        return true;
    }
    bool EnsurePool(const RbaFgCoordinatorCapture& capture)
    {
        if (pool != nullptr)
        {
            if (poolRenderWidth == capture.renderWidth && poolRenderHeight == capture.renderHeight && poolMotion == capture.motionFormat) return true;
            nativeDraining.store(true); Reason(RbaFgPending, "Render extent or motion format changed; draining input context"); return false;
        }
        RbaFgInputCreateDesc desc{}; desc.structSize = sizeof(desc); desc.abiVersion = 1;
        desc.d3d11Device = unity11->GetDevice(); desc.d3d12Queue = queue12.Get();
        desc.renderWidth = capture.renderWidth; desc.renderHeight = capture.renderHeight;
        desc.displayWidth = capture.displayWidth; desc.displayHeight = capture.displayHeight;
        desc.colorTransfer = capture.colorTransfer; desc.motionFormat = capture.motionFormat;
        auto s = PoolStatus();
        if (RbaFgInputCreate(&desc, &pool, &s) != RbaFgInputOk)
        { Reason(RbaFgDeviceError, s.error, true); return false; }
        poolRenderWidth = desc.renderWidth; poolRenderHeight = desc.renderHeight; poolMotion = desc.motionFormat;
        return true;
    }
    bool AcquireInput(Ticket& ticket, RbaFgInputStatus* status)
    {
        const auto result = RbaFgInputAcquire(pool, ticket.frame, &ticket.input, status);
        ticket.inputBusy = result == RbaFgInputBusy && status != nullptr && status->result == RbaFgInputBusy &&
            status->poisoned == 0 && status->freeSlots == 0 && status->error[0] == 0;
        if (result != RbaFgInputOk) return false;
        // Acquire polls fences before reusing a slot. It can observe retirement
        // after our last QueryLease, then expire that old token immediately. A
        // successful new token proves every older occupant of this exact pool
        // and slot has retired through both private fences. Preserve that proof
        // before the new scene copy; an arbitrary invalid token proves nothing.
        if (ticket.input.poolId == 0 || ticket.input.serial == 0) return true;
        Guard guard(lock);
        for (auto& previous : tickets)
            if (&previous != &ticket && previous.id != 0 && previous.inputAcquired &&
                previous.input.poolId == ticket.input.poolId && previous.input.slot == ticket.input.slot &&
                previous.input.serial != 0 && previous.input.serial < ticket.input.serial)
            {
                previous.sourceRetired = previous.retired = true;
                previous.result = RbaFgOk;
            }
        return true;
    }
    void Capture(Ticket& t)
    {
        ++diagnostics.captureEvents;
        uint32_t captureResult = RbaFgPending;
        bool noBorrow = true;
        bool accepted = HasProvider() && !IsProbe(providerMode) && !blocked.load() && !SoftFallback() && queue12 &&
            (IsNvidia(requested.load()) || IsAmd(requested.load())) && providerEpoch == controlEpoch.load() && t.frame > state.finalRealFrameId &&
            !nativeDraining.load() && eligible.load() != 0 && t.capture.sceneEligible != 0 &&
            t.capture.displayWidth == state.sourceWidth && t.capture.displayHeight == state.sourceHeight;
        if (accepted && IsAmd(providerMode) && (t.capture.renderWidth != amdContextRenderWidth ||
            t.capture.renderHeight != amdContextRenderHeight || t.capture.camera.reversedDepth != amdContextReversedDepth))
        { accepted = false; nativeDraining.store(true); Reason(RbaFgPending, "AMD render geometry/depth changed; reconfigure after draining"); }
        if (accepted && !EnsurePool(t.capture)) accepted = false;
        auto s = PoolStatus();
        const bool acquired = accepted && AcquireInput(t, &s);
        if (!accepted) ++diagnostics.captureUnavailable;
        else if (!acquired)
        {
            ++diagnostics.capturePoolUnavailable;
            if (t.inputBusy) captureResult = RbaFgBusy;
        }
        if (acquired)
        {
            t.inputAcquired = true;
            noBorrow = false;
            const auto result = RbaFgInputCopyScene(pool, &t.input, t.capture.hudlessColor11, t.capture.rawDepth11, t.capture.normalizedMotion11, &s);
            if (result == RbaFgInputOk) { ++diagnostics.captureAccepted; captureResult = RbaFgOk; state.lastCapturedFrameId = t.frame; }
            else { ++diagnostics.captureCopyFailures; captureResult = RbaFgDeviceError; RetireUnused(t); if (result == RbaFgInputDeviceError) Reason(RbaFgDeviceError, s.error, true); }
        }
        Guard guard(lock); t.consumed = true; t.result = captureResult;
        if (noBorrow) t.sourceRetired = t.retired = true;
    }
    void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
    {
        Ticket* t = nullptr;
        { Guard guard(lock); for (auto& candidate : tickets) if (&candidate == data && candidate.id != 0) t = &candidate; if (t == nullptr || t->consumed) return; }
        if ((t->kind == 1 && eventId != static_cast<int>(state.captureEventId)) ||
            (t->kind == 2 && eventId != static_cast<int>(state.endOfFrameEventId))) return;
        uint32_t expected = 0; renderThread.compare_exchange_strong(expected, GetCurrentThreadId());
        if (renderThread.load() != GetCurrentThreadId())
        {
            blocked.store(true); ++wrongThreadCount;
            Guard guard(lock);
            // No GPU work may be performed on this thread. The capture remains
            // owned by its own packet/pool until the real render thread drains.
            if (t->captureId != 0) { Ticket* capture = Find(t->captureId); if (capture != nullptr && capture->references != 0) --capture->references; t->captureId = 0; }
            t->consumed = t->sourceRetired = t->retired = true; t->result = RbaFgWrongThread; return;
        }
        state.renderThreadId = GetCurrentThreadId();
        if (t->kind == 1) Capture(*t);
        else
        {
            if (t->frame > state.finalRealFrameId)
            {
                ++diagnostics.realEofFrames;
                if (currentCapture != 0) DropCapture(currentCapture);
                state.finalRealFrameId = t->frame; finalFramePublished.store(t->frame);
                frameOwner.store(Owner::Unclaimed); currentCapture = t->captureId;
                RbaFgEndRender(t->frame);
            }
            else if (t->captureId != 0) DropCapture(t->captureId);
            Guard guard(lock); t->captureId = 0; t->consumed = t->sourceRetired = t->retired = true; t->result = RbaFgOk;
        }
        PollInputs(); Publish();
    }

    void UNITY_INTERFACE_API OnDeviceEvent(UnityGfxDeviceEventType event)
    {
        if (event == kUnityGfxDeviceEventInitialize || event == kUnityGfxDeviceEventAfterReset)
        {
            state.renderer = graphics != nullptr ? graphics->GetRenderer() : kUnityGfxRendererNull;
            unity11 = state.renderer == kUnityGfxRendererD3D11 && unity != nullptr ? unity->Get<IUnityGraphicsD3D11>() : nullptr;
        }
        else if (event == kUnityGfxDeviceEventBeforeReset || event == kUnityGfxDeviceEventShutdown)
        { blocked.store(true); providerPublished.store(0); nativeDraining.store(true); }
        ++state.deviceGeneration; state.nvidiaMultiplierMask = 0; completedProbes.store(0); details = {};
        state.sourceWindow = 0; state.sourceWidth = state.sourceHeight = 0;
        state.finalRealFrameId = 0; finalFramePublished.store(0); frameOwner.store(Owner::Unclaimed);
        renderThread.store(0); countValid = false; outputObserved = false; Publish();
    }
    bool ObserveSource(IDXGISwapChain* chain, DXGI_SWAP_CHAIN_DESC& desc, UINT& before)
    {
        if (FAILED(chain->GetDesc(&desc))) return false;
        state.sourceWindow = reinterpret_cast<uintptr_t>(desc.OutputWindow);
        state.sourceWidth = desc.BufferDesc.Width; state.sourceHeight = desc.BufferDesc.Height;
        state.sourceFormat = desc.BufferDesc.Format; state.sourceSwapEffect = desc.SwapEffect;
        state.sourceFlags = desc.Flags; state.sourceWindowed = desc.Windowed != FALSE;
        const uint64_t identity = reinterpret_cast<uintptr_t>(chain);
        if (identity != countChain) outputObserved = false;
        if (FAILED(chain->GetLastPresentCount(&before))) { countValid = false; return false; }
        if (countValid && identity == countChain && before >= lastCount)
        {
            const uint32_t delta = before - lastCount;
            if (previousOwned && delta != 0) { state.sourceUnexpectedCalls += delta; Reason(RbaFgProviderError, "Unexpected original Present after owned ticket", true); }
        }
        else countValid = false;
        lastCount = before; countChain = identity; countValid = true;
        return true;
    }
    bool SourceSupported(IDXGISwapChain* chain, const DXGI_SWAP_CHAIN_DESC& desc)
    {
        char reason[256]{};
        if (!desc.Windowed) { Reason(RbaFgUnsupported, "Source gate: exclusive fullscreen is unsupported"); return false; }
        if (desc.BufferDesc.Format != DXGI_FORMAT_R8G8B8A8_UNORM)
        { snprintf(reason, sizeof(reason), "Source gate: format=%u, expected RGBA8_UNORM28", desc.BufferDesc.Format); Reason(RbaFgUnsupported, reason); return false; }
        if (desc.SampleDesc.Count != 1 || desc.SampleDesc.Quality != 0)
        { snprintf(reason, sizeof(reason), "Source gate: samples=%u quality=%u, expected1/0", desc.SampleDesc.Count, desc.SampleDesc.Quality); Reason(RbaFgUnsupported, reason); return false; }
        if (desc.SwapEffect != DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL)
        { snprintf(reason, sizeof(reason), "Source gate: swapEffect=%u, expected FLIP_SEQUENTIAL3", desc.SwapEffect); Reason(RbaFgUnsupported, reason); return false; }
        const bool validTearing = state.sourcePresentFlags == DXGI_PRESENT_ALLOW_TEARING &&
            state.sourceSyncInterval == 0 && desc.Windowed && (desc.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) != 0;
        if ((desc.Flags & ~UINT(0x842)) != 0 || state.sourceSyncInterval > 1 || (state.sourcePresentFlags != 0 && !validTearing))
        { snprintf(reason, sizeof(reason), "Source gate: creationFlags=0x%X sync=%u PresentFlags=0x%X", desc.Flags, state.sourceSyncInterval, state.sourcePresentFlags); Reason(RbaFgUnsupported, reason); return false; }
        // Output6 describes the monitor signal, not this swapchain's color
        // space. An HDR desktop can compose an SDR RGBA8 Unity swapchain. Keep
        // Unity's HDR-disabled eligibility and encoded-sRGB capture contract;
        // do not substitute display capabilities for a nonexistent DXGI getter.
        ComPtr<IDXGIOutput> output; ComPtr<IDXGIOutput6> output6; DXGI_OUTPUT_DESC1 outputDesc{};
        outputObserved = false;
        HRESULT result = chain->GetContainingOutput(&output);
        if (SUCCEEDED(result)) result = output.As(&output6);
        if (SUCCEEDED(result)) result = output6->GetDesc1(&outputDesc);
        if (FAILED(result))
        { snprintf(reason, sizeof(reason), "Source gate: output6 observation failed HRESULT=0x%08X", static_cast<unsigned>(result)); Reason(RbaFgUnsupported, reason); return false; }
        outputObserved = true; outputColorSpace = outputDesc.ColorSpace; outputBitsPerColor = outputDesc.BitsPerColor;
        snprintf(reason, sizeof(reason), "Source gate passed: RGBA8 SDR contract; output ColorSpace=%u bitsPerColor=%u (monitor observation only)", outputDesc.ColorSpace, outputDesc.BitsPerColor);
        Reason(RbaFgOk, reason);
        return true;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* registry)
{
    if (registered || registry == nullptr) return;
    unity = registry; graphics = unity->Get<IUnityGraphics>();
    if (graphics == nullptr || graphics->ReserveEventIDRange == nullptr) return;
    const int first = graphics->ReserveEventIDRange(2);
    if (first < 0) return;
    state.structSize = sizeof(state); state.abiVersion = 1; state.loaded = 1;
    state.captureEventId = static_cast<uint32_t>(first); state.endOfFrameEventId = static_cast<uint32_t>(first + 1);
    graphics->RegisterDeviceEventCallback(OnDeviceEvent); registered = true;
    OnDeviceEvent(kUnityGfxDeviceEventInitialize);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    requested.store(0); eligible.store(0); providerPublished.store(0); blocked.store(true);
    // The retirement MonoBehaviour must keep Tick+EOF alive until all leases are
    // gone before unload. Do not invent a render-thread call or free a live packet.
    if (registered && graphics != nullptr) graphics->UnregisterDeviceEventCallback(OnDeviceEvent);
    registered = false; state.loaded = 0; Publish();
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityRenderingExtEvent(UnityRenderingExtEventType, void*)
{
    // This exact Unity ABI has no FrameEnd event; AfterDrawCall is not a frame
    // identifier. Retirement uses the real EOF pump and the verified Present seam.
}
extern "C" bool UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityRenderingExtQuery(UnityRenderingExtQueryType query)
{
    if (query != kUnityRenderingExtQueryOverridePresentFrame) return false;
    ++queryCount; queryThread.store(GetCurrentThreadId());
    if (renderThread.load() == 0) return false;
    if (renderThread.load() != GetCurrentThreadId())
    { ++wrongThreadCount; blocked.store(true); return YieldUnity(); }
    if (unity11 == nullptr || unity11->GetSwapChain == nullptr || unity11->GetDevice == nullptr ||
        unity11->GetSyncInterval == nullptr || unity11->GetPresentFlags == nullptr) return YieldUnity();
    ComPtr<IDXGISwapChain> chain = unity11->GetSwapChain();
    if (!chain) return YieldUnity();
    DXGI_SWAP_CHAIN_DESC desc{}; UINT before = 0;
    state.sourceSyncInterval = unity11->GetSyncInterval(); state.sourcePresentFlags = unity11->GetPresentFlags();
    const bool observed = ObserveSource(chain.Get(), desc, before);
    PollProvider();
    const Control desired = ReadControl();
    if ((HasProvider() || pool != nullptr || hostLease.leaseId != 0 || nativeDraining.load()) &&
        (desired.mode == RbaFgOff || desired.epoch != providerEpoch || blocked.load() || SoftFallback() ||
        nativeDraining.load() || (!eligible.load() && !IsProbe(desired.mode)) ||
        providerSourceChain != reinterpret_cast<uintptr_t>(chain.Get()) || providerSourceWindow != state.sourceWindow ||
        hostLease.width != state.sourceWidth || hostLease.height != state.sourceHeight || !host.Status().acceptingPresents)) Drain();
    const auto owner = frameOwner.load();
    if (owner != Owner::Unclaimed)
    {
        if (owner == Owner::Coordinator) ++state.duplicateQueries;
        previousOwned = owner == Owner::Coordinator; Publish(); return owner == Owner::Coordinator;
    }
    if (IsNvidia(desired.mode) || IsAmd(desired.mode)) ++diagnostics.activeQueries;
    bool waitTimedOut = false;
    bool candidate = observed && desired.contracts == 15 && !blocked.load() && !SoftFallback() &&
        (IsNvidia(desired.mode) || IsAmd(desired.mode) || IsProbe(desired.mode)) &&
        !nativeDraining.load() && !windowRecovery.load() && (eligible.load() || IsProbe(desired.mode)) &&
        GetModuleHandleW(L"GfxPluginReduxBetterAAPresentation.dll") == nullptr && SourceSupported(chain.Get(), desc);
    if (candidate && !(IsProbe(desired.mode) && (completedProbes.load() & ProbeBit(desired.mode))))
    {
        const bool ready = IsAmd(desired.mode) || IsAmdProbe(desired.mode) ?
            EnsureAmdProvider(unity11->GetDevice(), desired) : EnsureProvider(unity11->GetDevice(), desired);
        if (ready && IsProbe(desired.mode)) { completedProbes.fetch_or(ProbeBit(desired.mode)); Drain(); }
    }
    Ticket* capture = nullptr;
    { Guard guard(lock); capture = Find(currentCapture); }
    if (candidate && HasProvider() && (IsNvidia(desired.mode) || IsAmd(desired.mode)) && !blocked.load() && !nativeDraining.load() &&
        eligible.load() != 0 && desired.epoch == controlEpoch.load() && desired.mode == requested.load() &&
        capture != nullptr && capture->consumed && capture->inputAcquired && capture->result == RbaFgOk &&
        capture->frame == state.finalRealFrameId &&
        (state.providerPending != 0 || (amdProvider != nullptr && amdStatus.result == RbaAmdProviderBusy)))
    {
        // The next real scene is already in a separate pool slot. Wait once for
        // the previous SDK batch instead of inventing motion across a dropped
        // Unity frame. No main/window lock is held during this bounded wait.
        bool retired = false;
        const uint64_t waitStarted = GetTickCount64();
        host.HoldFrameReady(hostLease);
        if (provider != 0) retired = PollProvider(50);
        else
        {
            const ULONGLONG deadline = GetTickCount64() + 50;
            for (;;)
            {
                if (desired.epoch != controlEpoch.load() || desired.mode != requested.load() ||
                    !eligible.load() || blocked.load() || nativeDraining.load()) break;
                retired = PollProvider();
                if (retired || blocked.load() || GetTickCount64() >= deadline) break;
                // AMD Poll never enters the SDK; its worker owns those calls.
                // Require PollOk as well as pending0: the worker clears its job
                // just after publishing retirement and Submit can still be Busy.
                Sleep(1);
            }
        }
        RecordWait(waitStarted, retired);
        if (desired.epoch != controlEpoch.load() || desired.mode != requested.load() ||
            !eligible.load() || blocked.load() || nativeDraining.load())
        { candidate = false; Drain(); capture = nullptr; }
        else if (!retired)
        { candidate = false; waitTimedOut = true; ++diagnostics.waitTimeouts; Reason(RbaFgBusy, "Previous FG batch remained busy after50ms; current real frame is dropped"); }
    }
    if (!candidate || blocked.load() || nativeDraining.load() || IsProbe(desired.mode) || !HasProvider() || capture == nullptr ||
        !capture->consumed || !capture->inputAcquired || capture->result != RbaFgOk ||
        capture->frame != state.finalRealFrameId || state.providerPending != 0 || amdPresentPermit)
    {
        const auto dropReason = waitTimedOut ? RbaFgDropWaitTimeout : blocked.load() ? RbaFgDropProviderFailure :
                windowRecovery.load() ? RbaFgDropWindow : !candidate || nativeDraining.load() || !HasProvider() ? RbaFgDropGate :
                capture == nullptr ? RbaFgDropNoCapture :
                capture->consumed && capture->inputBusy && !capture->inputAcquired && capture->result == RbaFgBusy &&
                    capture->frame == state.finalRealFrameId ? RbaFgDropProviderBusy :
                !capture->consumed || !capture->inputAcquired || capture->result != RbaFgOk ||
                capture->frame != state.finalRealFrameId ? RbaFgDropCaptureRejected : RbaFgDropProviderBusy;
        if (IsNvidia(desired.mode) || IsAmd(desired.mode)) RecordDrop(dropReason);
        const bool soft = !blocked.load() && !nativeDraining.load() && !SoftFallback() &&
            eligible.load() != 0 && desired.epoch == controlEpoch.load() && desired.mode == requested.load() &&
            (dropReason == RbaFgDropNoCapture || dropReason == RbaFgDropProviderBusy || dropReason == RbaFgDropWaitTimeout);
        if (currentCapture != 0) { DropCapture(currentCapture, soft); currentCapture = 0; }
        else
        {
            DropMarker(static_cast<uint32_t>(state.finalRealFrameId));
            if (hostLease.leaseId != 0)
            { if (soft) host.HoldFrameReady(hostLease); else host.ClearFrameReady(hostLease); }
        }
        previousOwned = false; Publish(); return YieldUnity();
    }
    ComPtr<ID3D11Texture2D> finalColor;
    auto inputStatus = PoolStatus(); RbaFgInputResources resources{};
    const bool ready = SUCCEEDED(chain->GetBuffer(0, IID_PPV_ARGS(&finalColor))) &&
        RbaFgInputCopyFinal(pool, &capture->input, finalColor.Get(), &inputStatus) == RbaFgInputOk &&
        RbaFgInputQueueReady(pool, &capture->input, &resources, &inputStatus) == RbaFgInputOk;
    if (ready) capture->readyQueued = true;
    if (!ready || !host.TryBeginPresent(hostLease))
    {
        RecordDrop(!ready ? RbaFgDropTransport : RbaFgDropWindow);
        DropCapture(currentCapture); currentCapture = 0;
        if (inputStatus.poisoned) Reason(RbaFgQuarantined, inputStatus.error, true);
        previousOwned = false; Publish(); return YieldUnity();
    }
    auto expected = Owner::Unclaimed;
    if (!frameOwner.compare_exchange_strong(expected, Owner::Coordinator))
    { host.EndPresent(hostLease); DropCapture(currentCapture); currentCapture = 0; Publish(); return expected == Owner::Coordinator; }
    ++state.sourcePresentAttempts;
    const HRESULT original = chain->Present(state.sourceSyncInterval, state.sourcePresentFlags);
    UINT after = before;
    if (SUCCEEDED(chain->GetLastPresentCount(&after)) && countValid && after >= before && SUCCEEDED(original))
    {
        if (after - before == 1) ++state.sourceCountSingleSteps;
        else { ++state.sourceCountMismatches; Reason(RbaFgProviderError, "Original source Present count did not advance exactly once", true); }
        lastCount = after;
    }
    else countValid = false;
    previousOwned = true;
    if (SUCCEEDED(original)) ++state.sourcePresentSuccesses;
    if (FAILED(original)) { ++state.sourcePresentFailures; Reason(RbaFgDeviceError, "Original source Present failed", true); }
    else if (!blocked.load())
    {
        if (amdProvider != nullptr)
        {
            RbaAmdProviderFrame frame{}; frame.structSize = sizeof(frame); frame.abiVersion = 1;
            frame.realFrameId = capture->frame; frame.windowEpoch = hostLease.epoch;
            frame.finalColor = resources.finalColor; frame.hudlessColor = resources.hudlessColor;
            frame.rawDepth = resources.depth; frame.normalizedMotion = resources.motion;
            std::memcpy(&frame.camera, &capture->capture.camera, sizeof(frame.camera));
            if (amdStatus.lastAcceptedFrameId == 0 || capture->frame != amdStatus.lastAcceptedFrameId + 1) frame.camera.resetHistory = 1;
            const auto previous = amdStatus.lastAcceptedFrameId;
            auto s = AmdStatus(); const auto result = amdApi.submit(amdProvider, &frame, &s); UpdateAmd(s);
            capture->submitted = result == RbaAmdProviderOk;
            if (capture->submitted)
            {
                RecordSubmission(capture->frame, previous, frame.camera.resetHistory);
                amdPresentPermit = true; details.amdPresentPermitHeld = 1;
                state.lastSubmittedFrameId = capture->frame; state.state = s.realPresentCalls != 0 ? RbaFgActive : RbaFgReady;
                Reason(RbaFgPending, "AMD worker accepted the real frame; presentation and retirement are pending");
            }
            else if (result != RbaAmdProviderBusy) AmdFailure(result, s);
        }
        else
        {
        RbaSlFgFrame frame{}; frame.structSize = sizeof(frame); frame.abiVersion = 1;
        frame.realFrameId = capture->frame; frame.uiStrategy = RbaSlFgHudlessAndFinal; frame.windowEpoch = hostLease.epoch;
        frame.finalColor = resources.finalColor; frame.hudlessColor = resources.hudlessColor;
        frame.rawDepth = resources.depth; frame.normalizedMotion = resources.motion;
        std::memcpy(&frame.camera, &capture->capture.camera, sizeof(frame.camera));
        if (providerStatus.lastSubmittedFrameId == 0 ||
            capture->frame != providerStatus.lastSubmittedFrameId + 1) frame.camera.resetHistory = 1;
        const auto previous = providerStatus.lastSubmittedFrameId;
        auto s = ProviderStatus(); const auto result = api.submit(provider, &frame, &s); UpdateProvider(s);
        capture->submitted = SubmissionOwnsInput(result, s.inputBatchPending);
        if (result == RbaSlFgOk) { RecordSubmission(capture->frame, previous, frame.camera.resetHistory); state.lastSubmittedFrameId = capture->frame; state.state = RbaFgActive; Reason(RbaFgOk, "NVIDIA child presentation submitted"); }
        else if (result != RbaSlFgBusy) NvidiaFailure(result, s);
        }
    }
    if (!capture->submitted) RecordDrop(windowRecovery.load() ? RbaFgDropWindow : RbaFgDropProviderFailure);
    if (!amdPresentPermit) host.EndPresent(hostLease);
    if (!capture->submitted || blocked.load()) host.ClearFrameReady(hostLease);
    if (!capture->submitted) RetireUnused(*capture);
    { Guard guard(lock); if (capture->references != 0) --capture->references; }
    currentCapture = 0; PollInputs(); Publish();
    return true; // A failed owned attempt is never retried by Unity on this ticket.
}

RBA_FG_COORD_API uint32_t RbaFgConfigure(const RbaFgCoordinatorConfig* config)
{
    if (config == nullptr || config->structSize != sizeof(*config) || config->abiVersion != 1 ||
        config->mode > RbaFgNvidiaProbe || (config->mode != RbaFgOff && config->verifiedContracts != 15)) return RbaFgInvalidArgument;
    if (config->mode == RbaFgAmd2x) return RbaFgUnsupported;
    uint32_t expected = 0; mainThread.compare_exchange_strong(expected, GetCurrentThreadId());
    if (mainThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
    try
    {
        const std::wstring providerPath = config->providerDllPath != nullptr ? config->providerDllPath : L"";
        const std::wstring runtimePath = config->runtimeDirectory != nullptr ? config->runtimeDirectory : L"";
        if (config->mode != RbaFgOff && (!std::filesystem::path(providerPath).is_absolute() || !std::filesystem::path(runtimePath).is_absolute()))
            return RbaFgInvalidArgument;
        Guard guard(lock);
        if (providerModulePublished.load() != nullptr && !providerPath.empty() && _wcsicmp(providerPath.c_str(), control.providerPath.c_str()) != 0)
            return RbaFgUnsupported;
        const bool changed = control.mode != config->mode ||
            (!providerPath.empty() && providerPath != control.providerPath) || (!runtimePath.empty() && runtimePath != control.runtimePath);
        if (!providerPath.empty()) control.providerPath = providerPath;
        if (!runtimePath.empty()) control.runtimePath = runtimePath;
        control.mode = config->mode; control.contracts = config->verifiedContracts;
        if (changed) { ++control.epoch; controlEpoch.store(control.epoch); blocked.store(false); }
        requested.store(control.mode);
        if (config->mode == RbaFgOff) providerPublished.store(0);
        return snapshot.loaded ? RbaFgOk : RbaFgNotLoaded;
    }
    catch (...) { return RbaFgInvalidArgument; }
}

RBA_FG_COORD_API uint32_t RbaFgConfigureV2(const RbaFgCoordinatorConfigV2* config)
{
    if (config == nullptr || config->structSize != sizeof(*config) || config->abiVersion != 2 ||
        config->mode > RbaFgAmdProbeCompatibility || (config->mode != RbaFgOff && config->verifiedContracts != 15) ||
        config->amdReversedDepth > 1 || config->amdPreferCompatibility > 1 ||
        config->amdRenderWidth > 16384 || config->amdRenderHeight > 16384 ||
        ((config->amdRenderWidth == 0) != (config->amdRenderHeight == 0))) return RbaFgInvalidArgument;
    const bool amd = IsAmd(config->mode) || IsAmdProbe(config->mode);
    const bool compatibility = config->mode == RbaFgAmdCompatibility2x || config->mode == RbaFgAmdProbeCompatibility;
    if (amd && config->amdPreferCompatibility != static_cast<uint32_t>(compatibility)) return RbaFgInvalidArgument;
    uint32_t expected = 0; mainThread.compare_exchange_strong(expected, GetCurrentThreadId());
    if (mainThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
    try
    {
        Control desired = ReadControl();
        const auto copy = [](const wchar_t* value, std::wstring& destination) { if (value != nullptr && *value != 0) destination = value; };
        copy(config->nvidiaProviderDllPath, desired.providerPath); copy(config->nvidiaRuntimeDirectory, desired.runtimePath);
        copy(config->amdProviderDllPath, desired.amdProviderPath); copy(config->amdRuntimeDirectory, desired.amdRuntimePath);
        if (config->mode != RbaFgOff &&
            (!std::filesystem::path(amd ? desired.amdProviderPath : desired.providerPath).is_absolute() ||
             !std::filesystem::path(amd ? desired.amdRuntimePath : desired.runtimePath).is_absolute())) return RbaFgInvalidArgument;
        Guard guard(lock);
        if ((providerModulePublished.load() != nullptr && _wcsicmp(desired.providerPath.c_str(), control.providerPath.c_str()) != 0) ||
            (amdModulePublished.load() != nullptr && _wcsicmp(desired.amdProviderPath.c_str(), control.amdProviderPath.c_str()) != 0)) return RbaFgUnsupported;
        const bool changed = control.mode != config->mode || desired.providerPath != control.providerPath ||
            desired.runtimePath != control.runtimePath || desired.amdProviderPath != control.amdProviderPath ||
            desired.amdRuntimePath != control.amdRuntimePath || control.amdRenderWidth != config->amdRenderWidth ||
            control.amdRenderHeight != config->amdRenderHeight || control.amdReversedDepth != config->amdReversedDepth ||
            control.amdCompatibility != config->amdPreferCompatibility;
        desired.mode = config->mode; desired.contracts = config->verifiedContracts;
        desired.amdRenderWidth = config->amdRenderWidth; desired.amdRenderHeight = config->amdRenderHeight;
        desired.amdReversedDepth = config->amdReversedDepth; desired.amdCompatibility = config->amdPreferCompatibility;
        if (changed) { ++desired.epoch; blocked.store(false); }
        control = std::move(desired); controlEpoch.store(control.epoch); requested.store(control.mode);
        if (!IsNvidia(control.mode)) providerPublished.store(0);
        return snapshot.loaded ? RbaFgOk : RbaFgNotLoaded;
    }
    catch (...) { return RbaFgInvalidArgument; }
}

RBA_FG_COORD_API uint32_t RbaFgTick(uint32_t realFrameId, uint32_t sceneEligible)
{
    if (sceneEligible > 1 || realFrameId == 0) return RbaFgInvalidArgument;
    uint32_t expected = 0; mainThread.compare_exchange_strong(expected, GetCurrentThreadId());
    if (mainThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
    lastMainFrame.store(realFrameId);
    eligible.store(sceneEligible);
    RbaFgCoordinatorStatus s{};
    { Guard guard(lock); s = snapshot; }
    const auto mode = requested.load();
    const bool wants = IsNvidia(mode) || IsAmd(mode) || (IsProbe(mode) && !(completedProbes.load() & ProbeBit(mode)));
    ChildWindowConfig config{};
    config.parent = reinterpret_cast<HWND>(static_cast<uintptr_t>(s.sourceWindow));
    config.physicalWidth = s.sourceWidth; config.physicalHeight = s.sourceHeight;
    config.realFrameId = realFrameId; config.windowed = s.sourceWindowed != 0;
    config.renderFrameId = finalFramePublished.load();
    config.enabled = wants && !blocked.load() && !nativeDraining.load() && !windowRecovery.load() && !SoftFallback() && s.loaded != 0 &&
        s.sourceWindow != 0 && s.sourceFormat == DXGI_FORMAT_R8G8B8A8_UNORM &&
        (sceneEligible != 0 || IsProbe(mode));
    const bool alreadyExpired = host.Status().backpressureExpired;
    const auto tickEpoch = controlEpoch.load();
    const bool ticked = host.Tick(config);
    if (!alreadyExpired && host.Status().backpressureExpired)
    {
        softFallbackEpoch.store(tickEpoch);
        providerPublished.store(0);
    }
    if (windowRecovery.load() && !nativeDraining.load())
    {
        const auto child = host.Status();
        // A complete disabled owner Tick acknowledges old HWND cleanup/rearm.
        // New creation happens on a later Tick after normal geometry/DPI gates.
        if (!child.leaseRetained && child.window == nullptr) windowRecovery.store(false);
    }
    return ticked ? RbaFgOk : RbaFgPending;
}

RBA_FG_COORD_API uint32_t RbaFgGetStatus(RbaFgCoordinatorStatus* output)
{
    if (output == nullptr || output->structSize != sizeof(*output) || output->abiVersion != 1) return RbaFgInvalidArgument;
    {
        Guard guard(lock); *output = snapshot; output->pendingTickets = 0;
        for (const auto& t : tickets) if (t.id != 0) ++output->pendingTickets;
    }
    const auto child = host.Status();
    output->structSize = sizeof(*output); output->abiVersion = 1;
    output->requestedMode = requested.load(); output->sceneEligible = eligible.load();
    output->presentQueries = queryCount.load(); output->lastQueryThreadId = queryThread.load(); output->wrongThreadQueries = wrongThreadCount.load();
    output->renderThreadId = renderThread.load(); output->childWindow = reinterpret_cast<uintptr_t>(child.window);
    output->windowEpoch = child.epoch; output->childVisible = child.visible;
    output->childLeaseRetained = child.leaseRetained; output->childAcceptingPresents = child.acceptingPresents;
    output->childReason = static_cast<uint32_t>(child.reason);
    if (nativeDraining.load() || windowRecovery.load()) output->state = RbaFgDraining;
    else if (requested.load() == RbaFgOff && !child.leaseRetained && child.window == nullptr)
    { output->state = RbaFgDisabled; output->actualMultiplier = 0; }
    else if (blocked.load()) output->state = RbaFgBlocked;
    else if (SoftFallback())
    {
        // The managed capture gate accepts Ready/Active. Disabled prevents new
        // NVIDIA and AMD copies while preserving the user's requested mode.
        output->state = RbaFgDisabled; output->actualMultiplier = 0; output->result = RbaFgBusy;
        output->childReason = static_cast<uint32_t>(ChildWindowReason::BackpressureExpired);
        snprintf(output->reason, sizeof(output->reason), "%s", SoftFallbackReason);
    }
    else if (IsProbe(requested.load()) && (completedProbes.load() & ProbeBit(requested.load())) &&
        !child.leaseRetained && child.window == nullptr)
    { output->state = RbaFgReady; output->actualMultiplier = 0; }
    return RbaFgOk;
}

RBA_FG_COORD_API uint32_t RbaFgGetStatusV2(RbaFgCoordinatorStatusV2* output)
{
    if (output == nullptr || output->structSize != sizeof(*output) || output->abiVersion != 2) return RbaFgInvalidArgument;
    { Guard guard(lock); *output = detailsSnapshot; }
    output->structSize = sizeof(*output); output->abiVersion = 2;
    output->base.structSize = sizeof(output->base); output->base.abiVersion = 1;
    output->completedDiscoveryMask = completedProbes.load();
    return RbaFgGetStatus(&output->base);
}

RBA_FG_COORD_API uint32_t RbaFgGetDiagnostics(RbaFgCoordinatorDiagnostics* output)
{
    if (output == nullptr || output->structSize != sizeof(*output) || output->abiVersion != 1) return RbaFgInvalidArgument;
    {
        Guard guard(lock); *output = diagnosticsSnapshot;
        output->finalRealFrame = snapshot.finalRealFrameId; output->lastCapturedFrame = snapshot.lastCapturedFrameId;
        output->lastSubmittedFrame = snapshot.lastSubmittedFrameId; output->lastRetiredFrame = snapshot.lastRetiredFrameId;
    }
    output->structSize = sizeof(*output); output->abiVersion = 1; output->result = RbaFgOk;
    output->lastMainFrame = lastMainFrame.load(); output->windowRecovering = windowRecovery.load();
    output->beginSimulationFailures = markerFailures[0].load(); output->endSimulationFailures = markerFailures[1].load();
    output->beginRenderFailures = markerFailures[2].load(); output->endRenderFailures = markerFailures[3].load();
    const auto child = host.Status(); const auto now = GetTickCount64();
    output->windowEpoch = child.epoch; output->readyRealFrame = child.readyRealFrameId;
    output->readyAgeMilliseconds = child.readyRealFrameId != 0 && now >= child.readyObservedMilliseconds ? now - child.readyObservedMilliseconds : UINT64_MAX;
    output->childVisible = child.visible; output->childReason = static_cast<uint32_t>(child.reason);
    if (SoftFallback()) output->childReason = static_cast<uint32_t>(ChildWindowReason::BackpressureExpired);
    return RbaFgOk;
}

namespace
{
    using Marker = uint32_t (*)(uint64_t, uint32_t);
    uint32_t MainMarker(uint32_t frame, uint32_t kind)
    {
        if (frame == 0) return RbaFgInvalidArgument;
        if (mainThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
        const uint64_t handle = providerPublished.load();
        if (handle == 0 || !IsNvidia(requested.load()) || blocked.load() || SoftFallback()) return RbaFgPending;
        // The acquire of providerPublished precedes reading this immutable table.
        Marker marker = kind == 0 ? api.beginSimulation : kind == 1 ? api.endSimulation : api.beginRender;
        if (marker == nullptr) return RbaFgPending;
        const auto result = marker(handle, frame);
        if (result != RbaSlFgOk) ++markerFailures[kind];
        return result == RbaSlFgOk ? RbaFgOk : result == RbaSlFgBusy ? RbaFgBusy : RbaFgPending;
    }
}
RBA_FG_COORD_API uint32_t RbaFgBeginSimulation(uint32_t frame) { return MainMarker(frame, 0); }
RBA_FG_COORD_API uint32_t RbaFgEndSimulation(uint32_t frame) { return MainMarker(frame, 1); }
RBA_FG_COORD_API uint32_t RbaFgBeginRender(uint32_t frame)
{
    const auto result = MainMarker(frame, 2);
    if (result == RbaFgOk)
    {
        Guard guard(lock);
        for (auto& id : renderingFrames) if (id == 0 || id == frame) { id = frame; return RbaFgOk; }
        return RbaFgBusy;
    }
    return result;
}
RBA_FG_COORD_API uint32_t RbaFgEndRender(uint32_t frame)
{
    if (frame == 0) return RbaFgInvalidArgument;
    if (renderThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
    bool started = false;
    { Guard guard(lock); for (auto& id : renderingFrames) if (id == frame) { id = 0; started = true; break; } }
    if (!started || provider == 0 || api.endRender == nullptr || providerMode == RbaFgNvidiaProbe)
    { if (!started && provider != 0 && IsNvidia(providerMode)) ++markerFailures[3]; return RbaFgPending; }
    const auto result = api.endRender(provider, frame);
    if (result != RbaSlFgOk) ++markerFailures[3];
    return result == RbaSlFgOk ? RbaFgOk : RbaFgPending;
}

RBA_FG_COORD_API uint32_t RbaFgPrepareCapture(const RbaFgCoordinatorCapture* capture, uint64_t* ticket, void** eventData)
{
    if (ticket != nullptr) *ticket = 0; if (eventData != nullptr) *eventData = nullptr;
    if (capture == nullptr || ticket == nullptr || eventData == nullptr || capture->structSize != sizeof(*capture) ||
        capture->abiVersion != 1 || capture->realFrameId == 0 || capture->sceneEligible > 1 ||
        capture->realFrameId <= finalFramePublished.load()) return RbaFgInvalidArgument;
    if (mainThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
    if (capture->sceneEligible != 0 && (capture->renderWidth == 0 || capture->renderHeight == 0 ||
        capture->displayWidth == 0 || capture->displayHeight == 0 || capture->colorTransfer != RbaFgInputSrgbEncoded ||
        (capture->motionFormat != RbaFgInputMotionRg16Float && capture->motionFormat != RbaFgInputMotionRg32Float) ||
        capture->hudlessColor11 == nullptr || capture->rawDepth11 == nullptr || capture->normalizedMotion11 == nullptr)) return RbaFgInvalidArgument;
    Guard guard(lock);
    if (!snapshot.loaded) return RbaFgNotLoaded;
    for (const auto& t : tickets) if (t.id != 0 && t.kind == 1 && t.frame == capture->realFrameId) return RbaFgInvalidArgument;
    for (auto& t : tickets) if (t.id == 0)
    {
        t = {}; t.id = ++nextTicket; t.kind = 1; t.frame = capture->realFrameId; t.capture = *capture;
        *ticket = t.id; *eventData = &t; return RbaFgOk;
    }
    return RbaFgBusy;
}
RBA_FG_COORD_API uint32_t RbaFgPrepareEndOfFrame(uint32_t frame, uint64_t captureId, uint64_t* ticket, void** eventData)
{
    if (ticket != nullptr) *ticket = 0; if (eventData != nullptr) *eventData = nullptr;
    if (ticket == nullptr || eventData == nullptr || frame == 0 || frame <= finalFramePublished.load()) return RbaFgInvalidArgument;
    if (mainThread.load() != GetCurrentThreadId()) return RbaFgWrongThread;
    Guard guard(lock);
    if (!snapshot.loaded) return RbaFgNotLoaded;
    Ticket* capture = Find(captureId);
    if (captureId != 0 && (capture == nullptr || capture->kind != 1 || capture->frame != frame)) return RbaFgInvalidTicket;
    for (const auto& t : tickets) if (t.id != 0 && t.kind == 2 && t.frame == frame) return RbaFgInvalidArgument;
    for (auto& t : tickets) if (t.id == 0)
    {
        t = {}; t.id = ++nextTicket; t.kind = 2; t.frame = frame; t.captureId = captureId;
        if (capture != nullptr) ++capture->references;
        *ticket = t.id; *eventData = &t; return RbaFgOk;
    }
    return RbaFgBusy;
}
RBA_FG_COORD_API void* RbaFgGetRenderEventFunc() { return reinterpret_cast<void*>(&OnRenderEvent); }
RBA_FG_COORD_API uint32_t RbaFgQueryTicket(uint64_t id, RbaFgCoordinatorTicketStatus* output)
{
    if (output == nullptr || output->structSize != sizeof(*output) || output->abiVersion != 1) return RbaFgInvalidArgument;
    Guard guard(lock); const Ticket* t = Find(id);
    if (t == nullptr) return RbaFgInvalidTicket;
    *output = {}; output->structSize = sizeof(*output); output->abiVersion = 1; output->ticket = id;
    output->result = t->result; output->kind = t->kind; output->realFrameId = t->frame;
    output->eventConsumed = t->consumed; output->sourcePixelsRetired = t->consumed && t->sourceRetired;
    output->fullyRetired = t->consumed && t->retired && t->references == 0;
    return RbaFgOk;
}
RBA_FG_COORD_API uint32_t RbaFgReleaseTicket(uint64_t id)
{
    Guard guard(lock); Ticket* t = Find(id);
    if (t == nullptr) return RbaFgInvalidTicket;
    if (!t->consumed || !t->retired || t->references != 0) return RbaFgBusy;
    *t = {}; return RbaFgOk;
}
