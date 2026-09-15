// Private fault injection exercises the actual implementation without a vendor
// runtime, adapter, or invented production timing. Real GPU validation is separate.
#define RBA_SL_PROVIDER_EXPORTS
#include "StreamlineProvider.cpp"
#include <thread>
#include <vector>

namespace {
unsigned Checks = 0;
void Expect(bool value, const char* message) { ++Checks; if (!value) throw std::runtime_error(message); }
RbaSlFgStatus Status() { RbaSlFgStatus status{}; InitStatus(status); return status; }
struct TestToken : sl::FrameToken { uint32_t id = 0; operator uint32_t() const override { return id; } };
std::array<TestToken, 3> TestTokens;
std::mutex TestMutex;
std::vector<std::pair<uint32_t, sl::PCLMarker>> TestMarkers;
HANDLE SleepEntered = nullptr, SleepRelease = nullptr;
sl::Result NewToken(sl::FrameToken*& token, const uint32_t* id) { auto& value = TestTokens[*id % 3]; value.id = *id; token = &value; return sl::Result::eOk; }
sl::Result SleepToken(const sl::FrameToken&) {
    if (SleepEntered) { SetEvent(SleepEntered); WaitForSingleObject(SleepRelease, 5000); }
    return sl::Result::eOk;
}
sl::Result Mark(sl::PCLMarker marker, const sl::FrameToken& token) {
    std::lock_guard<std::mutex> lock(TestMutex); TestMarkers.emplace_back(static_cast<uint32_t>(token), marker); return sl::Result::eOk;
}
std::pair<uint64_t, std::shared_ptr<Provider>> TestProvider() {
    auto provider = std::make_shared<Provider>(); provider->initialized = true; provider->newFrame = NewToken; provider->sleep = SleepToken; provider->marker = Mark;
    std::lock_guard<std::mutex> lock(RegistryMutex); const auto handle = NextHandle++; Registry.emplace(handle, provider); return {handle, provider};
}
void MarkerOrderingAndBackpressure() {
    auto [handle, p] = TestProvider();
    Expect(RbaSlFgEndSimulation(handle, 1) == RbaSlFgInvalidOrder, "Missing simulation start accepted");
    Expect(RbaSlFgBeginSimulation(handle, 1) == RbaSlFgOk && RbaSlFgEndSimulation(handle, 1) == RbaSlFgOk, "Frame1 simulation failed");
    Expect(RbaSlFgBeginSimulation(handle, 2) == RbaSlFgOk && RbaSlFgEndSimulation(handle, 2) == RbaSlFgOk, "Frame2 main loop cannot overlap frame1 rendering");
    Expect(RbaSlFgBeginRender(handle, 1) == RbaSlFgOk && RbaSlFgEndRender(handle, 1) == RbaSlFgOk, "Frame1 render markers failed");
    Expect(RbaSlFgEndRender(handle, 1) == RbaSlFgInvalidOrder, "Duplicate render end accepted");
    Expect(RbaSlFgBeginSimulation(handle, 3) == RbaSlFgOk, "Third buffered marker frame rejected");
    Expect(RbaSlFgBeginSimulation(handle, 4) == RbaSlFgBusy, "Fourth frame overwrote an SDK token still in flight");
    Expect(RbaSlFgDiscardFrame(handle, 2) == RbaSlFgOk && RbaSlFgBeginSimulation(handle, 4) == RbaSlFgOk, "Busy advanced newest ID or Discard did not free slot");
    Expect(RbaSlFgBeginRender(handle, 3) == RbaSlFgInvalidOrder, "Invented missing simulation end");
    p->inputFrame = 1; p->status.inputBatchPending = 1;
    const auto previousLedger = p->ledger.frames;
    auto status = Status(); RbaSlFgFrame invalid{};
    Expect(RbaSlFgSubmit(handle, &invalid, &status) == RbaSlFgBusy, "Busy must reject before inspecting replacement input");
    Expect(status.poisoned == 0 && p->inputFrame == 1 && p->status.inputBatchPending == 1, "Busy consumed or poisoned previous batch");
    for (size_t i = 0; i < previousLedger.size(); ++i)
        Expect(p->ledger.frames[i].phase == previousLedger[i].phase && p->ledger.frames[i].id == previousLedger[i].id, "Busy mutated marker ownership");
    p->inputFrame = 0; p->status.inputBatchPending = 0;
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgOk, "Marker-only test provider cleanup failed");
    Expect(RbaSlFgBeginSimulation(handle, 5) == RbaSlFgInvalidHandle, "Destroyed handle was reused");
}
void ConcurrentSleepAndShutdown() {
    auto [handle, p] = TestProvider();
    SleepEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr); SleepRelease = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Expect(SleepEntered && SleepRelease, "Create sleep test events failed");
    std::atomic<uint32_t> result{999};
    std::thread mainLoop([&] { result = RbaSlFgBeginSimulation(handle, 1); });
    Expect(WaitForSingleObject(SleepEntered, 5000) == WAIT_OBJECT_0, "Did not reach blocking Reflex sleep stub");
    auto status = Status();
    Expect(RbaSlFgGetStatus(handle, &status) == RbaSlFgOk, "Status held behind Reflex sleep");
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgBusy, "Shutdown freed a session while sleep callback was active");
    Expect(p->runtime.value != nullptr && !p->destroyed, "Busy shutdown released session");
    SetEvent(SleepRelease); mainLoop.join();
    Expect(result == RbaSlFgOk, "Already entered marker could not leave safely during shutdown");
    Expect(RbaSlFgBeginSimulation(handle, 2) == RbaSlFgInvalidHandle, "Stopping session accepted new marker work");
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgOk && p->runtime.value == nullptr, "Drained shutdown did not release session exactly once");
    CloseHandle(SleepEntered); CloseHandle(SleepRelease); SleepEntered = SleepRelease = nullptr;
}
unsigned Stops = 0;
unsigned Frees = 0;
sl::Result StopOk() { ++Stops; return sl::Result::eOk; }
sl::Result FreeOk(sl::Feature feature, const sl::ViewportHandle&) { ++Frees; return feature == sl::kFeatureDLSS_G ? sl::Result::eOk : sl::Result::eErrorInvalidParameter; }
sl::Result FreeFail(sl::Feature, const sl::ViewportHandle&) { ++Frees; return sl::Result::eErrorInvalidParameter; }
sl::Result StopFail() { ++Stops; return sl::Result::eErrorNotInitialized; }
void OffCachesOnlyTheInitializedSession() {
    auto [handle, p] = TestProvider(); p->sessionReady = p->runtimeLoaded = true;
    p->runtime.value->initAttempted = true; p->runtime.value->shutdown = StopOk; p->freeResources = FreeOk;
    const auto stops = Stops, frees = Frees; auto status = Status();
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgOk, "Healthy viewport could not enter Off");
    Expect(Frees == frees + 1 && Stops == stops, "Off did not free viewport or incorrectly shut down the global SDK");
    Expect(IdleRuntime && IdleRuntime->reusable && IdleRuntime->runtime.value->initAttempted && !p->runtime.value,
        "Off did not transfer exclusive ownership of its initialized SDK session");
    IdleRuntime.reset();
    Expect(Stops == stops + 1, "Final session cleanup must shut down once before releasing its engine objects");
}
void FailedViewportFreeRetainsOwnership() {
    auto [handle, p] = TestProvider(); p->sessionReady = p->runtimeLoaded = true; p->freeResources = FreeFail;
    const auto frees = Frees; auto status = Status();
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgSdkError && p->runtime.value && !IdleRuntime && !p->destroyed,
        "Failed viewport destruction released or cached a live provider");
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgPoisoned && Frees == frees + 1,
        "Failed viewport destruction was retried without proof of SDK cleanup");
    { std::lock_guard<std::mutex> lock(RegistryMutex); Registry.erase(handle); }
}
void FailedInitializationCannotBecomeReusable() {
    auto [handle, p] = TestProvider(); p->sessionReady = p->runtimeLoaded = true;
    p->runtime.value->initAttempted = true; p->runtime.value->shutdown = StopOk; p->freeResources = FreeOk;
    const auto stops = Stops, frees = Frees;
    p->InitializationFailure(RbaSlFgSdkError, "Injected SDK exception after device-session initialization");
    Expect(p->Cleanup(0), "Terminal initialized session could not complete known shutdown");
    Expect(Stops == stops + 1 && Frees == frees && IdleRuntime && !IdleRuntime->reusable && !IdleRuntime->runtime.value->initAttempted,
        "A failed initialized SDK session became reusable after cleanup");
    { std::lock_guard<std::mutex> lock(RegistryMutex); Registry.erase(handle); }
    IdleRuntime.reset();
    Expect(Stops == stops + 1, "Stopped terminal session shutdown was repeated");
}
void FailedCleanupRetainsEverything() {
    auto [handle, p] = TestProvider(); p->runtime.value->initAttempted = true; p->runtime.value->shutdown = StopFail;
    HANDLE sentinel = CreateEventW(nullptr, FALSE, FALSE, nullptr); p->runtime.value->files[0].handle = sentinel;
    auto status = Status();
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgSdkError, "Uncertain shutdown reported success");
    DWORD flags = 0;
    Expect(GetHandleInformation(sentinel, &flags) && Lookup(handle) && status.poisoned, "Failed shutdown lost runtime/window lease owner");
    const auto stopsAfterFailure = Stops;
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgPoisoned && Stops == stopsAfterFailure, "Quarantined SDK teardown was repeated");
    // The injected callback owns no GPU work: explicitly remove this test-only session.
    p->runtime.value->initAttempted = false;
    { std::lock_guard<std::mutex> lock(RegistryMutex); Registry.erase(handle); }
}
void AsyncFailureCannotBecomeReady() {
    auto [handle, p] = TestProvider(); auto status = Status();
    AsyncErrors.store(1);
    Expect(RbaSlFgPoll(handle, 0, &status) == RbaSlFgSdkError && status.poisoned && status.asyncApiErrors == 1,
        "Asynchronous SDK error disappeared behind successful queue retirement");
    Expect(RbaSlFgDestroy(handle, 0, &status) == RbaSlFgOk, "A drained async error prevented safe shutdown");
    AsyncErrors.store(0);
}
void ProcessOwnershipLease() {
    auto first = std::make_unique<ProcessSessionLease>(); first->Acquire(); bool rejected = false;
    try { ProcessSessionLease second; second.Acquire(); } catch (const Failure& failure) { rejected = failure.code == RbaSlAlreadyOwned; }
    Expect(rejected, "Concurrent probe/provider session lease was not exclusive");
    std::thread release([lease = std::move(first)] {}); release.join();
    ProcessSessionLease third; third.Acquire(); Expect(third.acquired, "Session lease could not be released from another thread");
}
void IdleOwnerRejectsNewFeatureModules(const wchar_t* fixturePath) {
    const auto path = std::filesystem::canonical(fixturePath);
    HMODULE fixture = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    Expect(fixture != nullptr, "Could not load harmless foreign-owner fixture");
    bool rejected = false;
    try { ValidateDeviceSessionModules(path.parent_path(), {}); }
    catch (const Failure& f) { rejected = f.code == RbaSlFgAlreadyOwned; }
    FreeLibrary(fixture);
    Expect(rejected, "An idle loader accepted a newly loaded feature plugin as its own stopped residue");
}
RbaSlFgCamera Camera() {
    RbaSlFgCamera camera{}; camera.nearPlane = .1f; camera.farPlane = 100.f; camera.verticalFovRadians = 1.0471975512f;
    camera.aspectRatio = 16.f / 9; camera.viewSpaceToMeters = 10; camera.frameTimeMilliseconds = 0; camera.resetHistory = 1;
    camera.motionScaleX = 1; camera.motionScaleY = -1; camera.jitterRenderPixelsX = .25f; camera.jitterRenderPixelsY = -.125f;
    camera.projection[0] = 1.f / (std::tan(camera.verticalFovRadians / 2) * camera.aspectRatio);
    camera.projection[5] = -1.f / std::tan(camera.verticalFovRadians / 2); // actual GPU Y flip
    camera.projection[2] = .1f; camera.projection[6] = -.05f; // off-center lens
    camera.projection[10] = camera.farPlane / (camera.nearPlane - camera.farPlane);
    camera.projection[11] = camera.nearPlane * camera.farPlane / (camera.nearPlane - camera.farPlane); camera.projection[14] = -1;
    for (size_t i = 0; i < 4; ++i) camera.worldToView[i * 4 + i] = 1;
    camera.worldToView[3] = -10; camera.worldToView[7] = -20; camera.worldToView[11] = -30;
    rba_sl::Matrix p{}, v{}; rba_sl::MatrixRead(camera.projection, p); rba_sl::MatrixRead(camera.worldToView, v);
    const auto current = rba_sl::Multiply(p, v); v[3] += 2; const auto previous = rba_sl::Multiply(p, v);
    for (size_t i = 0; i < 16; ++i) { camera.viewProjection[i] = static_cast<float>(current[i]); camera.previousViewProjection[i] = static_cast<float>(previous[i]); }
    return camera;
}
std::array<double, 4> Transform(const sl::float4x4& matrix, const std::array<double, 4>& point) {
    std::array<double, 4> out{}; for (uint32_t r = 0; r < 4; ++r) for (uint32_t c = 0; c < 4; ++c) out[c] += point[r] * (&matrix[r].x)[c]; return out;
}
void CameraConventions() {
    auto camera = Camera(); Expect(rba_sl::CameraValid(camera), "Valid translated, Y-flipped, off-center Unity camera rejected");
    const auto sl = rba_sl::MakeConstants(camera, false);
    Expect(sl.cameraPos.x == 100 && sl.cameraPos.y == 200 && sl.cameraPos.z == 300 && sl.cameraFwd.z == -1, "Unity camera position/negative-Z/unit conversion is wrong");
    Expect(sl.cameraNear == 1 && sl.cameraFar == 1000 && sl.jitterOffset.x == .25f && sl.mvecScale.y == -1, "Explicit scalar/sign metadata was changed");
    const std::array<double, 4> viewMeters{3, 4, -20, 1};
    auto clip = Transform(sl.cameraViewToClip, viewMeters); const auto recovered = Transform(sl.clipToCameraView, clip);
    for (size_t i = 0; i < 4; ++i) Expect(rba_sl::Near(recovered[i], viewMeters[i], .0001), "Projection/inverse lost view-space meter scale");
    const auto previous = Transform(sl.clipToPrevClip, clip);
    Expect(rba_sl::Near(previous[0] / previous[3] - clip[0] / clip[3], camera.projection[0], .0001), "Current-to-previous clip transposition or direction is wrong");
    const auto roundTrip = Transform(sl.prevClipToClip, previous);
    for (size_t i = 0; i < 4; ++i) Expect(rba_sl::Near(roundTrip[i], clip[i], .0001), "Previous/current clip transforms are not inverses");
    camera.resetHistory = 0; Expect(!rba_sl::CameraValid(camera), "Zero elapsed accepted without reset"); camera.frameTimeMilliseconds = 16;
    Expect(rba_sl::CameraValid(camera), "Valid non-reset elapsed rejected"); camera.viewProjection[3] += 1;
    Expect(!rba_sl::CameraValid(camera), "Camera matrices from mismatched frames accepted"); camera = Camera(); camera.reversedDepth = 1;
    Expect(!rba_sl::CameraValid(camera), "Depth convention mismatch accepted"); camera.projection[10] = camera.nearPlane / (camera.farPlane - camera.nearPlane);
    camera.projection[11] = camera.nearPlane * camera.farPlane / (camera.farPlane - camera.nearPlane);
    rba_sl::Matrix p{}, v{}; rba_sl::MatrixRead(camera.projection, p); rba_sl::MatrixRead(camera.worldToView, v); const auto vp = rba_sl::Multiply(p, v);
    for (size_t i = 0; i < 16; ++i) camera.viewProjection[i] = static_cast<float>(vp[i]);
    Expect(rba_sl::CameraValid(camera), "Valid reversed-device-depth projection rejected");
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        auto status = Status(); uint64_t handle = 99;
        Expect(argc == 2, "Pass harmless module-audit fixture path");
        Expect(RbaSlFgCreate(nullptr, &handle, &status) == RbaSlFgAbiMismatch && handle == 0, "Bad create ABI must clear handle without starting SDK");
        Expect(RbaSlFgGetStatus(999999, &status) == RbaSlFgInvalidHandle, "Foreign handle accepted");
        CameraConventions(); ProcessOwnershipLease(); MarkerOrderingAndBackpressure(); ConcurrentSleepAndShutdown(); FailedCleanupRetainsEverything(); AsyncFailureCannotBecomeReady();
        OffCachesOnlyTheInitializedSession(); FailedViewportFreeRetainsOwnership(); FailedInitializationCannotBecomeReusable();
        IdleOwnerRejectsNewFeatureModules(argv[1]);
        std::printf("PASS: %u provider ABI, camera, marker ordering, backpressure, lifecycle and process-ownership checks\n", Checks); return 0;
    } catch (const std::exception& error) { std::fprintf(stderr, "FAIL: %s\n", error.what()); return 1; }
}
