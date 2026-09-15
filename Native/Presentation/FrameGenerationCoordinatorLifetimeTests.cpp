// Test-only executable compiles the owned implementation against fake pool and
// provider boundaries. No fixture symbols are linked into the runtime DLL.
#include "FrameGenerationCoordinator.cpp"
#include <cstdlib>

namespace
{
    unsigned checks = 0, poolDestroys = 0, sdkDestroys = 0, lastPollBudget = 0;
    bool poolCanDestroy = true, expectPoolGoneAtShutdown = true;
    bool recycleOnAcquire = false, oldLeaseExpired = false;
    unsigned expiredLeaseQueries = 0;
    RbaFgInputLease recycledLease{};
    uint32_t fakePending = 0, fakePoisoned = 0, fakeDestroyResult = RbaSlFgOk, fakeAmdPollResult = RbaAmdProviderBusy;
    uint32_t fakeAcquireResult = RbaFgInputBusy, fakeAcquirePoisoned = 0, fakeAcquireFreeSlots = 0;
    void Check(bool condition, const char* description)
    { ++checks; if (!condition) { std::fprintf(stderr, "FAILED: %s\n", description); std::exit(1); } }
    uint32_t PoolReport(RbaFgInputStatus* output, uint32_t result)
    { if (output) { *output = {}; output->structSize = sizeof(*output); output->abiVersion = 1; output->result = result; } return result; }
    uint32_t FakePoll(uint64_t, uint32_t budget, RbaSlFgStatus* output)
    {
        lastPollBudget = budget; *output = ProviderStatus(); output->inputBatchPending = fakePending; output->poisoned = fakePoisoned;
        output->result = fakePoisoned ? RbaSlFgPoisoned : fakePending ? RbaSlFgBusy : RbaSlFgOk;
        RbaFgCoordinatorStatus readable{}; readable.structSize = sizeof(readable); readable.abiVersion = 1;
        Check(RbaFgGetStatus(&readable) == RbaFgOk, "status/control lock is not held across provider retirement");
        return output->result;
    }
    uint32_t FakeDestroy(uint64_t, uint32_t budget, RbaSlFgStatus* output)
    {
        ++sdkDestroys; Check(budget == 0, "SDK drain never waits on main/control disposal");
        Check((pool == nullptr) == expectPoolGoneAtShutdown, "SDK shutdown observes required pool-retirement order");
        *output = ProviderStatus(); output->result = fakeDestroyResult;
        output->inputBatchPending = fakePending; output->poisoned = fakePoisoned; return output->result;
    }
    uint32_t FakeAmdPoll(void*, RbaAmdProviderStatus* output)
    {
        *output = AmdStatus(); output->result = fakeAmdPollResult; output->initialized = 1;
        output->lastAcceptedFrameId = output->lastRetiredFrameId = 12;
        return output->result;
    }
    void ResetFixture()
    {
        Check(!queue12 && !device12 && !completionFence && hostLease.leaseId == 0, "fixture has no GPU or window resources");
        tickets = {}; state = {}; snapshot = {}; details = {}; detailsSnapshot = {}; diagnostics = {}; diagnosticsSnapshot = {};
        pool = reinterpret_cast<void*>(static_cast<uintptr_t>(1)); provider = 42; amdProvider = nullptr;
        providerMode = RbaFgNvidia2x; providerRetiredFrame = 0; currentCapture = 0;
        requested.store(RbaFgOff); blocked.store(false); nativeDraining.store(false); windowRecovery.store(false);
        softFallbackEpoch.store(0);
        fakePending = fakePoisoned = 0; fakeDestroyResult = RbaSlFgOk;
        poolCanDestroy = expectPoolGoneAtShutdown = true; poolDestroys = sdkDestroys = lastPollBudget = 0;
        recycleOnAcquire = oldLeaseExpired = false; expiredLeaseQueries = 0; recycledLease = {};
        fakeAcquireResult = RbaFgInputBusy; fakeAcquirePoisoned = fakeAcquireFreeSlots = 0;
        api = {}; api.poll = FakePoll; api.destroy = FakeDestroy;
        tickets[0].id = 8; tickets[0].frame = 12; tickets[0].kind = 1;
        tickets[0].consumed = true; tickets[0].inputAcquired = true;
    }
}

uint32_t RbaFgInputCreate(const RbaFgInputCreateDesc*, void**, RbaFgInputStatus* s) { return PoolReport(s, RbaFgInputInvalidArgument); }
uint32_t RbaFgInputAcquire(void*, uint64_t frame, RbaFgInputLease* output, RbaFgInputStatus* s)
{
    if (!recycleOnAcquire)
    {
        const auto result = PoolReport(s, fakeAcquireResult);
        if (s != nullptr) { s->poisoned = fakeAcquirePoisoned; s->freeSlots = fakeAcquireFreeSlots; }
        return result;
    }
    // The private fences complete inside Acquire, after the caller's last poll.
    // The real pool immediately expires the earlier same-slot serial on reuse.
    oldLeaseExpired = true; *output = recycledLease; output->realFrameId = frame;
    return PoolReport(s, RbaFgInputOk);
}
uint32_t RbaFgInputCopyScene(void*, const RbaFgInputLease*, void*, void*, void*, RbaFgInputStatus* s) { return PoolReport(s, RbaFgInputInvalidArgument); }
uint32_t RbaFgInputCopyFinal(void*, const RbaFgInputLease*, void*, RbaFgInputStatus* s) { return PoolReport(s, RbaFgInputInvalidArgument); }
uint32_t RbaFgInputQueueReady(void*, const RbaFgInputLease*, RbaFgInputResources*, RbaFgInputStatus* s) { return PoolReport(s, RbaFgInputInvalidArgument); }
uint32_t RbaFgInputRetire(void*, const RbaFgInputLease*, void*, uint64_t, RbaFgInputStatus* s) { return PoolReport(s, RbaFgInputInvalidArgument); }
uint32_t RbaFgInputCancel(void*, const RbaFgInputLease*, RbaFgInputStatus* s) { return PoolReport(s, RbaFgInputOk); }
uint32_t RbaFgInputQueryLease(void*, const RbaFgInputLease* lease, RbaFgInputStatus* s)
{
    if (oldLeaseExpired && lease->poolId == recycledLease.poolId && lease->slot == recycledLease.slot &&
        lease->serial < recycledLease.serial)
    { ++expiredLeaseQueries; return PoolReport(s, RbaFgInputInvalidArgument); }
    return PoolReport(s, RbaFgInputBusy);
} // The last ordinary snapshot is deliberately stale.
uint32_t RbaFgInputDestroy(void*, RbaFgInputStatus* s)
{ ++poolDestroys; return PoolReport(s, poolCanDestroy ? RbaFgInputOk : RbaFgInputBusy); }

int main()
{
    ResetFixture();
    Check(Drain(), "late pool fence completion can finish healthy drain");
    Check(poolDestroys == 1 && sdkDestroys == 1 && !pool && !provider, "pool destruction precedes exactly one SDK shutdown");
    RbaFgCoordinatorTicketStatus t{}; t.structSize = sizeof(t); t.abiVersion = 1;
    Check(RbaFgQueryTicket(8, &t) == RbaFgOk && t.eventConsumed && t.sourcePixelsRetired && t.fullyRetired,
        "DestroyOk acknowledges a ticket even when preceding QueryLease was pending");
    Check(RbaFgReleaseTicket(8) == RbaFgOk, "managed packet cannot strand after the pool is destroyed");

    ResetFixture(); poolCanDestroy = false;
    Check(!Drain() && pool != nullptr && provider == 42 && sdkDestroys == 0 && nativeDraining.load(),
        "pending pool copies prevent SDK shutdown without a blocking wait");
    Check(RbaFgReleaseTicket(8) == RbaFgBusy, "pending source copies preserve managed ticket");
    poolCanDestroy = true;
    Check(Drain() && sdkDestroys == 1 && RbaFgReleaseTicket(8) == RbaFgOk, "retry acknowledges late copy completion then shuts SDK down");

    ResetFixture(); fakePending = 1; tickets[0].submitted = tickets[0].readyQueued = true;
    Check(!Drain() && poolDestroys == 0 && sdkDestroys == 0 && provider == 42,
        "healthy submitted input must retire through Poll before pool or SDK teardown");
    Check(RbaFgReleaseTicket(8) == RbaFgBusy, "healthy pending SDK readers preserve caller lease");

    ResetFixture(); fakePending = fakePoisoned = 1; fakeDestroyResult = RbaSlFgPoisoned;
    expectPoolGoneAtShutdown = false; tickets[0].submitted = tickets[0].readyQueued = true;
    Check(!Drain() && sdkDestroys == 1 && poolDestroys == 0 && provider == 42 && pool != nullptr,
        "poisoned input can request cleanup but failed cleanup never frees its pool");
    Check(RbaFgReleaseTicket(8) == RbaFgBusy && nativeDraining.load(), "uncertain SDK cleanup retains all retirement obligations");

    ResetFixture(); pool = nullptr; tickets = {};
    Check(PollProvider(50) && lastPollBudget == 50, "bounded NVIDIA retirement forwards the exact50ms budget");
    provider = 0; amdProvider = reinterpret_cast<void*>(static_cast<uintptr_t>(2));
    amdApi.poll = FakeAmdPoll; providerMode = RbaFgAmd2x;
    Check(!PollProvider() && amdStatus.inputBatchPending == 0 && amdStatus.lastRetiredFrameId == 12,
        "AMD pending0 does not pretend the worker has finished its final job-clear step");
    fakeAmdPollResult = RbaAmdProviderOk;
    Check(PollProvider(), "AMD PollOk permits the next batch after actual retirement and job clear");
    amdProvider = nullptr; provider = 0; pool = nullptr; tickets = {};

    provider = 42; outputObserved = true; outputColorSpace = 12; outputBitsPerColor = 10;
    providerStatus = ProviderStatus(); providerStatus.sdkRuntimeStatus = 16; providerStatus.lastSdkPresented = 1;
    providerStatus.nativePresentCountDelta = 1234; providerStatus.nativePresentCountValid = 1;
    providerStatus.lastPresentHresult = 0x887a0005; providerStatus.asyncApiErrors = 2;
    Reason(RbaFgOk, "NVIDIA child completed"); Publish();
    Check(std::strstr(snapshot.reason, "NV status=16 SDKlast=1 native=1234 valid=1 hr=0x887A0005 async=2") != nullptr,
        "active provider diagnostics preserve raw SDK runtime and native Present counters");
    Check(std::strstr(snapshot.reason, "monitor ColorSpace=12 bits=10") != nullptr &&
        std::strstr(snapshot.reason, "NVIDIA child completed") != nullptr, "numeric suffix preserves monitor observation and original reason");
    std::memset(state.reason, 'E', sizeof(state.reason) - 1); state.reason[sizeof(state.reason) - 1] = 0;
    const char firstError[] = "First SDK error: callback failure ";
    std::memcpy(state.reason, firstError, sizeof(firstError) - 1); Publish();
    Check(std::strstr(snapshot.reason, firstError) != nullptr && std::strstr(snapshot.reason, "NV status=") == nullptr,
        "long first SDK failure is never replaced by optional numeric telemetry");
    provider = 0; Reason(RbaFgOk, "Off"); Publish();
    Check(std::strstr(snapshot.reason, "NV status=") == nullptr, "Off diagnostics cannot display an earlier provider numeric state");

    ResetFixture(); requested.store(RbaFgNvidia2x); providerPublished.store(provider); frameOwner.store(Owner::Coordinator);
    auto changed = ProviderStatus(); changed.result = RbaSlFgWindowChanged;
    std::snprintf(changed.reason, sizeof(changed.reason), "Child extent changed before input tagging");
    NvidiaFailure(RbaSlFgWindowChanged, changed);
    Check(windowRecovery.load() && nativeDraining.load() && !blocked.load() && providerPublished.load() == 0 && state.state == RbaFgDraining,
        "unborrowed healthy window drift suspends markers and requests recoverable drain");
    Check(YieldUnity(), "recoverable window drift never lets Unity repeat an already owned original Present");
    poolCanDestroy = false;
    Check(!Drain() && windowRecovery.load() && provider == 42 && sdkDestroys == 0,
        "window recovery preserves pending pool leases before SDK retirement");
    Check(RbaFgTick(12, 1) == RbaFgOk && windowRecovery.load(), "owner Tick cannot acknowledge recovery before retirement completes");
    poolCanDestroy = true;
    Check(Drain() && !nativeDraining.load() && windowRecovery.load() && !provider && !pool,
        "successful render drain still requires a complete disabled owner Tick");
    Check(RbaFgTick(13, 1) == RbaFgOk && !windowRecovery.load() && !blocked.load(),
        "owner cleanup acknowledgment permits a later stable-window rearm");

    ResetFixture(); changed.poisoned = 1; NvidiaFailure(RbaSlFgWindowChanged, changed);
    Check(blocked.load() && !windowRecovery.load(), "window code never clears a poisoned NVIDIA session");
    ResetFixture(); changed.poisoned = 0; changed.inputBatchPending = 1; NvidiaFailure(RbaSlFgWindowChanged, changed);
    Check(blocked.load() && !windowRecovery.load(), "uncertain pending SDK input does not enter automatic window recovery");
    ResetFixture(); changed.inputBatchPending = 0; changed.asyncApiErrors = 1; NvidiaFailure(RbaSlFgWindowChanged, changed);
    Check(blocked.load() && !windowRecovery.load(), "asynchronous API failure remains terminal despite window result");
    ResetFixture(); changed.asyncApiErrors = 0; changed.sdkRuntimeStatus = 8; NvidiaFailure(RbaSlFgWindowChanged, changed);
    Check(blocked.load() && !windowRecovery.load(), "SDK runtime error remains terminal despite window result");
    ResetFixture(); changed.sdkRuntimeStatus = 0; NvidiaFailure(RbaSlFgDeviceError, changed);
    Check(blocked.load() && !windowRecovery.load(), "device removal cannot be reclassified as recoverable window drift");
    ResetFixture(); blocked.store(true); NvidiaFailure(RbaSlFgWindowChanged, changed);
    Check(blocked.load() && !windowRecovery.load(), "prior terminal coordinator failure is never cleared by window recovery");
    ResetFixture(); auto amdChanged = AmdStatus(); amdChanged.result = RbaAmdProviderWindowChanged;
    AmdFailure(RbaAmdProviderWindowChanged, amdChanged);
    Check(windowRecovery.load() && !blocked.load(), "AMD unborrowed unpoisoned window rejection uses the same owner retirement handshake");
    ResetFixture(); amdChanged.poisoned = 1; AmdFailure(RbaAmdProviderWindowChanged, amdChanged);
    Check(blocked.load() && !windowRecovery.load(), "AMD poisoned worker state remains terminal");
    provider = 0; amdProvider = nullptr; pool = nullptr; tickets = {};
    diagnostics = {}; state.finalRealFrameId = 900;
    RecordDrop(RbaFgDropNoCapture); RecordDrop(RbaFgDropCaptureRejected); RecordDrop(RbaFgDropWaitTimeout);
    RecordWait(GetTickCount64() - 7, true); RecordSubmission(900, 0, 1); RecordSubmission(901, 900, 0); RecordSubmission(903, 901, 1);
    Publish(); RbaFgCoordinatorDiagnostics observed{}; observed.structSize = sizeof(observed); observed.abiVersion = 1;
    Check(RbaFgGetDiagnostics(&observed) == RbaFgOk && observed.dropNoCapture == 1 && observed.dropCaptureRejected == 1 &&
        observed.dropWaitTimeout == 1 && observed.lastDropReason == RbaFgDropWaitTimeout && observed.lastDropFrame == 900,
        "diagnostics keep no capture, rejected capture and retirement timeout separate");
    Check(observed.waitCalls == 1 && observed.waitCompleted == 1 && observed.waitTotalMilliseconds >= 7 &&
        observed.waitLongestMilliseconds == observed.waitTotalMilliseconds && observed.lastWaitBudgetMilliseconds == 50,
        "wait diagnostics report observed duration separately from configured retirement budget");
    Check(observed.successfulSubmissions == 3 && observed.resetSubmissions == 2 && observed.gapSubmissions == 1,
        "submission diagnostics distinguish priming, consecutive input and reset after missing real frames");

    ResetFixture(); provider = 0;
    tickets[0].input = {77, 10, 12, 1, 0}; tickets[0].references = 1;
    PollInputs();
    Check(!tickets[0].sourceRetired && !tickets[0].retired,
        "last pre-acquisition snapshot can still have both source and full retirement pending");
    tickets[1].id = 9; tickets[1].frame = 13; tickets[1].kind = 1;
    recycledLease = {77, 11, 13, 1, 0}; recycleOnAcquire = true;
    auto inputStatus = PoolStatus();
    Check(AcquireInput(tickets[1], &inputStatus) && oldLeaseExpired,
        "actual acquisition boundary observes late private-fence completion and replaces the old serial");
    Check(tickets[0].sourceRetired && tickets[0].retired && !tickets[1].sourceRetired && !tickets[1].retired,
        "new same-slot serial preserves only the prior occupant's complete retirement proof");
    PollInputs();
    Check(expiredLeaseQueries == 0, "reused cancelled input no longer queries an expired token forever");
    t = {}; t.structSize = sizeof(t); t.abiVersion = 1;
    Check(RbaFgQueryTicket(8, &t) == RbaFgOk && t.sourcePixelsRetired && !t.fullyRetired &&
        RbaFgReleaseTicket(8) == RbaFgBusy, "late GPU proof does not discard a retained EOF packet reference");
    --tickets[0].references;
    Check(RbaFgQueryTicket(8, &t) == RbaFgOk && t.fullyRetired && RbaFgReleaseTicket(8) == RbaFgOk,
        "old managed surface and ticket become reusable when the separate EOF reference retires");

    ResetFixture(); provider = 0;
    tickets[0].input = {77, 10, 12, 1, 0}; tickets[0].sourceRetired = true;
    recycledLease = {77, 11, 13, 1, 0}; oldLeaseExpired = true;
    PollInputs();
    Check(expiredLeaseQueries == 1 && !tickets[0].retired && tickets[0].sourceRetired,
        "arbitrary invalid-token response never invents full retirement or erases earlier source proof");
    tickets[1].id = 9; tickets[1].frame = 13;
    Check(!AcquireInput(tickets[1], &inputStatus) && !tickets[0].retired,
        "failed acquisition cannot acknowledge an earlier occupant");
    recycleOnAcquire = true; recycledLease = {78, 11, 13, 1, 0};
    Check(AcquireInput(tickets[1], &inputStatus) && !tickets[0].retired,
        "another pool's identical slot number cannot acknowledge the old input");
    recycledLease = {77, 11, 13, 2, 0};
    Check(AcquireInput(tickets[1], &inputStatus) && !tickets[0].retired,
        "another slot in the same pool cannot acknowledge the old input");
    recycledLease = {77, 10, 13, 1, 0};
    Check(AcquireInput(tickets[1], &inputStatus) && !tickets[0].retired,
        "equal serial is not proof of slot replacement");
    recycledLease = {77, 9, 13, 1, 0};
    Check(AcquireInput(tickets[1], &inputStatus) && !tickets[0].retired,
        "older serial is not proof of slot replacement");
    tickets = {}; provider = 0; pool = nullptr;
    ChildWindowConfig imageConfig{}; imageConfig.enabled = true;
    imageConfig.realFrameId = imageConfig.renderFrameId = 100;
    ChildWindowStatus image{}; image.acceptingPresents = image.leaseRetained = image.visible = true;
    image.readyRealFrameId = 100; image.readyObservedMilliseconds = 1000;
    Check(EvaluateChildImage(imageConfig, image, 1001) == ChildImageDisposition::Fresh,
        "a completed current image is normally eligible");
    image.softBackpressure = true;
    imageConfig.realFrameId = imageConfig.renderFrameId = 101;
    Check(EvaluateChildImage(imageConfig, image, 1050) == ChildImageDisposition::Fresh,
        "one ordinary busy frame keeps the completed child visible");
    imageConfig.realFrameId = imageConfig.renderFrameId = 102;
    Check(EvaluateChildImage(imageConfig, image, 1100) == ChildImageDisposition::Fresh,
        "two ordinary busy frames keep the same completed child visible");
    imageConfig.realFrameId = imageConfig.renderFrameId = 110;
    Check(EvaluateChildImage(imageConfig, image, 1250) == ChildImageDisposition::SoftHold,
        "eligible soft backpressure preserves the already visible image only through its completion deadline");
    image.readyRealFrameId = 102; image.readyObservedMilliseconds = 1100;
    Check(EvaluateChildImage(imageConfig, image, 1251) == ChildImageDisposition::SoftHold,
        "real completed progress that remains behind the frontier keeps the hold and advances the deadline");
    image.readyRealFrameId = 100; image.readyObservedMilliseconds = 1000;
    Check(EvaluateChildImage(imageConfig, image, 1251) == ChildImageDisposition::Expired &&
        EvaluateChildImage(imageConfig, image, 1500) == ChildImageDisposition::Expired && image.readyObservedMilliseconds == 1000,
        "repeated observations never extend the 250ms completion deadline");
    imageConfig.enabled = false;
    Check(EvaluateChildImage(imageConfig, image, 1100) == ChildImageDisposition::Unavailable,
        "Off or hard scene eligibility disables soft hold immediately");
    imageConfig.enabled = true; image.acceptingPresents = false;
    Check(EvaluateChildImage(imageConfig, image, 1100) == ChildImageDisposition::Unavailable,
        "window or device invalidation cannot hold an old image");
    image.acceptingPresents = true; image.visible = false;
    Check(EvaluateChildImage(imageConfig, image, 1100) == ChildImageDisposition::Unavailable,
        "soft hold cannot show an already hidden older image");
    image.visible = true; image.backpressureExpired = true;
    image.readyRealFrameId = 110; image.readyObservedMilliseconds = 1100;
    Check(EvaluateChildImage(imageConfig, image, 1101) == ChildImageDisposition::Unavailable,
        "late completion cannot revive a host after its fallback deadline exposed Unity");
    image.backpressureExpired = false; image.softBackpressure = false;
    Check(EvaluateChildImage(imageConfig, image, 1101) == ChildImageDisposition::Fresh,
        "a newly leased epoch still requires an actually completed fresh image");

    ResetFixture(); provider = 0;
    auto& busyTicket = tickets[1]; busyTicket.id = 99; busyTicket.frame = 20; busyTicket.capture.camera.resetHistory = 1;
    auto busyStatus = PoolStatus();
    Check(!AcquireInput(busyTicket, &busyStatus) && busyTicket.inputBusy && !busyTicket.inputAcquired &&
        busyTicket.capture.camera.resetHistory == 1,
        "actual healthy full-pool Busy records soft pressure without changing SDK gap-reset semantics");
    fakeAcquirePoisoned = 1;
    Check(!AcquireInput(busyTicket, &busyStatus) && !busyTicket.inputBusy,
        "poisoned pool Busy never authorizes a soft presentation hold");
    fakeAcquirePoisoned = 0; fakeAcquireFreeSlots = 1;
    Check(!AcquireInput(busyTicket, &busyStatus) && !busyTicket.inputBusy,
        "Busy with available slots is not certified capacity backpressure");
    fakeAcquireFreeSlots = 0; fakeAcquireResult = RbaFgInputInvalidArgument;
    Check(!AcquireInput(busyTicket, &busyStatus) && !busyTicket.inputBusy,
        "invalid input acquisition remains a hard capture rejection");

    ResetFixture(); requested.store(RbaFgNvidia2x); controlEpoch.store(42); softFallbackEpoch.store(42);
    fakePending = 1; poolCanDestroy = false;
    Check(!Drain() && SoftFallback() && provider != 0 && pool != nullptr && !tickets[0].retired,
        "soft fallback retains real pending SDK and pool obligations while ordinary Unity continues");
    fakePending = 0; poolCanDestroy = true;
    Check(Drain() && SoftFallback() && provider == 0 && pool == nullptr,
        "completed cleanup does not automatically rearm a soft-fallback epoch");
    Publish(); RbaFgCoordinatorStatus paused{}; paused.structSize = sizeof(paused); paused.abiVersion = 1;
    Check(RbaFgGetStatus(&paused) == RbaFgOk && paused.state == RbaFgDisabled && paused.actualMultiplier == 0 &&
        paused.childVisible == 0 && paused.childReason == static_cast<uint32_t>(ChildWindowReason::BackpressureExpired) &&
        std::strstr(paused.reason, "Off") != nullptr && std::strstr(paused.reason, "retry") != nullptr,
        "latched fallback is inactive and reports an actionable retry reason");
    Check(paused.state < RbaFgReady && paused.requestedMode == RbaFgNvidia2x,
        "paused status stops the existing managed capture gate without overwriting the user's requested mode");
    control = {}; control.mode = RbaFgNvidia2x; control.epoch = 42;
    control.providerPath = L"G:\\fixture\\provider.dll"; control.runtimePath = L"G:\\fixture\\runtime";
    mainThread.store(GetCurrentThreadId());
    RbaFgCoordinatorConfig retry{}; retry.structSize = sizeof(retry); retry.abiVersion = 1;
    retry.mode = RbaFgNvidia2x; retry.verifiedContracts = 15;
    retry.providerDllPath = control.providerPath.c_str(); retry.runtimeDirectory = control.runtimePath.c_str();
    const auto unchanged = RbaFgConfigure(&retry);
    Check((unchanged == RbaFgOk || unchanged == RbaFgNotLoaded) && SoftFallback(),
        "repeating the same mode does not revive an expired epoch");
    retry.mode = RbaFgOff; retry.providerDllPath = nullptr; retry.runtimeDirectory = nullptr;
    const auto disabled = RbaFgConfigure(&retry);
    Check((disabled == RbaFgOk || disabled == RbaFgNotLoaded) && !SoftFallback(),
        "explicit Off starts a distinct control epoch");
    retry.mode = RbaFgNvidia2x;
    retry.providerDllPath = L"G:\\fixture\\provider.dll"; retry.runtimeDirectory = L"G:\\fixture\\runtime";
    const auto reenabled = RbaFgConfigure(&retry);
    Check((reenabled == RbaFgOk || reenabled == RbaFgNotLoaded) && !SoftFallback(),
        "explicit mode re-enable is permitted after old provider work retired");
    tickets = {}; provider = 0; pool = nullptr; requested.store(RbaFgOff); softFallbackEpoch.store(0);
    std::printf("Passed %u coordinator lifecycle fixture checks; no GPU, SDK, child window or runtime test exports.\n", checks);
    return 0;
}
