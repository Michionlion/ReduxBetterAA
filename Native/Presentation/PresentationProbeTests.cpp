#include "PresentationProbe.h"

#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <IUnityGraphics.h>
#include <IUnityGraphicsD3D11.h>
#include <IUnityRenderingExtensions.h>
#include <cstdio>
#include <cstdlib>
#include <thread>

namespace
{
    int checks = 0;
    void Check(bool condition, const char* description)
    {
        ++checks;
        if (!condition)
        {
            std::fprintf(stderr, "FAILED: %s\n", description);
            std::exit(1);
        }
    }

    class SwapChain final : public IDXGISwapChain
    {
    public:
        ULONG refs = 1;
        UINT presents = 0;
        UINT interval = 0;
        UINT flags = 0;
        HRESULT result = S_OK;
        UINT totalPresentCalls = 0;
        UINT countReads = 0;
        UINT extraCallsInsidePresent = 0;
        UINT width = 640;
        HRESULT countResult = S_OK;
        bool queryOnOtherThreadDuringPresent = false;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
        ULONG STDMETHODCALLTYPE Release() override { return --refs; }
        HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID, UINT, const void*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID, const IUnknown*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID, UINT*, void*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetParent(REFIID, void**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDevice(REFIID, void**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE Present(UINT syncInterval, UINT presentFlags) override
        {
            ++presents;
            totalPresentCalls += 1 + extraCallsInsidePresent;
            interval = syncInterval;
            flags = presentFlags;
            // Status/control must be safe during Present (no lock held while
            // DXGI may wait for the main/window thread).
            RbaPresentStatus status{};
            status.structSize = sizeof(status);
            status.abiVersion = 2;
            Check(RbaPresent_GetStatus(&status) == 1, "status reentrancy during Present");
            if (queryOnOtherThreadDuringPresent)
            {
                std::thread otherThread([] {
                    Check(UnityRenderingExtQuery(kUnityRenderingExtQueryOverridePresentFrame),
                        "unexpected thread preserves ownership during in-flight Present");
                });
                otherThread.join();
            }
            return result;
        }
        HRESULT STDMETHODCALLTYPE GetBuffer(UINT, REFIID, void**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL, IDXGIOutput*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL*, IDXGIOutput**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC* desc) override
        {
            *desc = {};
            desc->BufferDesc.Width = width;
            desc->BufferDesc.Height = 360;
            desc->BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            desc->SampleDesc.Count = 1;
            desc->BufferCount = 2;
            desc->Windowed = TRUE;
            desc->SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT, UINT, UINT, DXGI_FORMAT, UINT) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT* count) override
        {
            ++countReads;
            *count = totalPresentCalls;
            return countResult;
        }
    } swapChain;
    SwapChain* selectedSwapChain = &swapChain;

    IUnityInterfaces registry{};
    IUnityGraphics graphics{};
    IUnityGraphicsD3D11 d3d{};
    UnityGfxRenderer renderer = kUnityGfxRendererD3D11;
    IUnityGraphicsDeviceEventCallback deviceCallback = nullptr;
    UINT syncInterval = 2;
    UINT presentFlags = DXGI_PRESENT_TEST | DXGI_PRESENT_DO_NOT_WAIT;
    uint32_t registrations = 0;
    uint32_t unregistrations = 0;
    uint32_t swapChainReads = 0;
    bool missingSwapChain = false;
    bool missingD3D = false;
    bool queryOnOtherThreadBeforePresent = false;

    UnityGfxRenderer UNITY_INTERFACE_API GetRenderer() { return renderer; }
    void UNITY_INTERFACE_API Register(IUnityGraphicsDeviceEventCallback callback) { deviceCallback = callback; ++registrations; }
    void UNITY_INTERFACE_API Unregister(IUnityGraphicsDeviceEventCallback callback)
    {
        Check(callback == deviceCallback, "unregisters exact callback");
        deviceCallback = nullptr;
        ++unregistrations;
    }
    int UNITY_INTERFACE_API Reserve(int count) { Check(count == 1, "reserves one unique event"); return 700; }
    ID3D11Device* UNITY_INTERFACE_API GetDevice() { return reinterpret_cast<ID3D11Device*>(static_cast<uintptr_t>(1)); }
    IDXGISwapChain* UNITY_INTERFACE_API GetSwapChain() { ++swapChainReads; return missingSwapChain ? nullptr : selectedSwapChain; }
    UINT32 UNITY_INTERFACE_API GetSyncInterval() { return syncInterval; }
    UINT UNITY_INTERFACE_API GetPresentFlags()
    {
        if (queryOnOtherThreadBeforePresent)
        {
            std::thread otherThread([] {
                Check(!UnityRenderingExtQuery(kUnityRenderingExtQueryOverridePresentFrame),
                    "unexpected thread claims unowned ticket for Unity during acquisition");
            });
            otherThread.join();
        }
        return presentFlags;
    }
    IUnityInterface* UNITY_INTERFACE_API GetInterface(UnityInterfaceGUID guid)
    {
        if (guid == GetUnityInterfaceGUID<IUnityGraphics>()) return &graphics;
        if (guid == GetUnityInterfaceGUID<IUnityGraphicsD3D11>()) return missingD3D ? nullptr : &d3d;
        return nullptr;
    }

    RbaPresentStatus Status()
    {
        RbaPresentStatus status{};
        status.structSize = sizeof(status);
        status.abiVersion = 2;
        Check(RbaPresent_GetStatus(&status) == 1, "status ABI roundtrip");
        return status;
    }

    void Frame(uint64_t id)
    {
        RbaPresentFrame frame{sizeof(RbaPresentFrame), 1, id};
        const auto callback = reinterpret_cast<UnityRenderingEventAndData>(RbaPresent_GetRenderEventFunc());
        callback(700, &frame);
    }

    bool Query() { return UnityRenderingExtQuery(kUnityRenderingExtQueryOverridePresentFrame); }
    void Enable() { Check(RbaPresent_Request(1, RbaPresentContract_All) == 1, "explicit harness contract accepts enable"); }
}

int main()
{
    static_assert(sizeof(RbaPresentFrame) == 16, "C frame ABI");
    static_assert(sizeof(RbaPresentStatus) == 312, "C status ABI");
    registry.GetInterface = GetInterface;
    graphics.GetRenderer = GetRenderer;
    graphics.RegisterDeviceEventCallback = Register;
    graphics.UnregisterDeviceEventCallback = Unregister;
    graphics.ReserveEventIDRange = Reserve;
    d3d.GetDevice = GetDevice;
    d3d.GetSwapChain = GetSwapChain;
    d3d.GetSyncInterval = GetSyncInterval;
    d3d.GetPresentFlags = GetPresentFlags;

    Check(!Query(), "before loading, Unity owns Present");
    Check(RbaPresent_Request(1, RbaPresentContract_All) == 0, "cannot request takeover before loading");
    UnityPluginLoad(&registry);
    UnityPluginLoad(&registry);
    Check(registrations == 1, "repeated load registers only once");
    Check(Status().loaded == 1 && Status().requested == 0, "load starts passive");
    Check(!Query(), "startup query has no side effects");
    Check(Status().queriesWithoutFinalFrame == 1, "startup cadence is observable");
    Check(RbaPresent_Request(1, 0) == 0, "rejects missing exact-player contract");
    Frame(9);
    Check(!Query(), "final-frame ticket does not arm presentation");
    Check(Status().lastQueryFrameId == 9, "passive query reports preceding ticket");
    Check(swapChainReads == 0 && swapChain.presents == 0, "passive path never touches the swapchain");
    Check(!UnityRenderingExtQuery(kUnityRenderingExtQueryOverrideViewport), "never takes unrelated rendering ownership");

    Enable();
    Check(!Query() && swapChain.presents == 0, "enabling cannot steal a ticket already owned by Unity");
    Frame(10);
    swapChain.queryOnOtherThreadDuringPresent = true;
    Check(Query(), "armed query takes ownership of this Present");
    swapChain.queryOnOtherThreadDuringPresent = false;
    Check(swapChain.presents == 1, "one Present for one ticket");
    Check(swapChain.interval == syncInterval && swapChain.flags == presentFlags, "exact interval and flags forwarded");
    Check(swapChain.refs == 1, "swapchain reference released after Present");
    Check(Query() && swapChain.presents == 1, "repeated query cannot double Present");
    Check(Status().duplicateQueries == 1, "duplicate query telemetry");
    std::thread wrongThreadAfterPresent([] { Check(Query(), "wrong-thread query cannot return an already-presented ticket to Unity"); });
    wrongThreadAfterPresent.join();
    Check(swapChain.presents == 1 && Status().requested == 0, "wrong thread after Present disables future takeover without double present");
    Check(RbaPresent_Request(0, 0) == 1, "disable accepted");
    Check(Query() && swapChain.presents == 1, "disable cannot return already-presented frame to Unity");
    Frame(11);
    Check(!Query(), "next frame returns ownership after disable");

    Enable();
    syncInterval = 0;
    presentFlags = DXGI_PRESENT_ALLOW_TEARING;
    Frame(12);
    Check(Query(), "new ticket presents");
    Check(swapChain.interval == 0 && swapChain.flags == DXGI_PRESENT_ALLOW_TEARING, "interval/flags are queried anew each frame");
    Frame(12);
    Check(Status().rejectedFrames == 1 && Status().requested == 0, "duplicate ticket disables future takeover");
    Check(Query() && swapChain.presents == 2, "duplicate ticket cannot double Present");
    Frame(11);
    Check(Status().rejectedFrames == 2, "stale ticket rejected");
    Frame(13);
    Check(!Query(), "bad ticket recovers to normal Unity ownership");

    Enable();
    swapChain.result = DXGI_ERROR_DEVICE_REMOVED;
    Frame(14);
    Check(Query(), "failed Present is still owned, no Unity retry");
    Check(Query() && swapChain.presents == 3, "failed Present is never duplicated");
    std::thread wrongThreadAfterFailure([] { Check(Query(), "wrong-thread query preserves failed Present ownership too"); });
    wrongThreadAfterFailure.join();
    Check(Status().presentFailures == 1 && Status().requested == 0, "device loss disables takeover");
    Frame(15);
    Check(!Query(), "device-loss fallback on next frame");
    Check(Status().lastPresentResult == DXGI_ERROR_DEVICE_REMOVED, "failure HRESULT retained");
    swapChain.result = S_OK;

    Enable();
    missingSwapChain = true;
    Frame(16);
    Check(!Query() && swapChain.presents == 3, "missing swapchain leaves Unity ownership");
    Check(Status().reason == RbaPresentReason_MissingSwapChain, "missing swapchain diagnosed");
    missingSwapChain = false;
    Enable();
    missingD3D = true;
    Frame(17);
    Check(!Query() && Status().reason == RbaPresentReason_MissingInterface, "missing interface fails passively");
    missingD3D = false;

    Enable();
    Frame(18);
    const uint64_t wrongQueriesBefore = Status().wrongThreadQueries;
    std::thread wrongThread([] { Check(!Query(), "off-render-thread query never calls D3D"); });
    wrongThread.join();
    Check(Status().wrongThreadQueries == wrongQueriesBefore + 1 && Status().requested == 0, "wrong thread stops takeover");
    Check(!Query(), "wrong-thread fault remains disabled on correct thread");
    Enable();
    Check(!Query(), "new request cannot steal ticket already yielded by wrong-thread query");

    Enable();
    Frame(19);
    queryOnOtherThreadBeforePresent = true;
    Check(!Query(), "wrong-thread query during acquisition prevents later probe Present");
    queryOnOtherThreadBeforePresent = false;
    Check(swapChain.presents == 3 && swapChain.refs == 1, "racing normal owner prevents duplicate Present and releases COM reference");
    Check(Status().requested == 0, "acquisition race disables future takeover");

    Enable();
    const auto generation = Status().deviceGeneration;
    deviceCallback(kUnityGfxDeviceEventBeforeReset);
    Check(!Query() && Status().requested == 0, "reset removes ownership");
    Check(Status().deviceGeneration > generation, "device generation changes on reset");
    deviceCallback(kUnityGfxDeviceEventAfterReset);
    Frame(1);
    Check(!Query(), "reset requires a new explicit request");
    Enable();
    Check(!Query(), "request after passive query waits for a new ticket");
    Frame(2);
    Check(Query(), "new device sequence may be explicitly armed");
    Check(swapChain.refs == 1, "no retained COM refs across lifecycle");
    deviceCallback(kUnityGfxDeviceEventShutdown);
    Check(!Query() && Status().requested == 0, "shutdown restores passive ownership");

    UnityPluginUnload();
    UnityPluginUnload();
    Check(unregistrations == 1 && deviceCallback == nullptr, "unload unregisters exactly once");
    Check(!Query() && Status().loaded == 0, "unload cannot Present");
    renderer = kUnityGfxRendererD3D12;
    UnityPluginLoad(&registry);
    Check(RbaPresent_Request(1, RbaPresentContract_All) == 0, "unsupported renderer cannot arm");
    Frame(1);
    Check(!Query(), "D3D12 stays passive");
    UnityPluginUnload();
    Check(swapChain.countReads == 0, "count observation is opt-in even during takeover");

    renderer = kUnityGfxRendererD3D11;
    UnityPluginLoad(&registry);
    Check(RbaPresent_Observe(1, 0) == 0, "count observation requires exact Unity ABI confirmation");
    Check(RbaPresent_RequestComposition(1, RbaPresentContract_All) == 0, "composition requires passive observation first");
    Check(RbaPresent_Observe(1, RbaPresentContract_Unity6000_5_8f1) == 1, "read-only observation can be enabled independently");
    swapChain.totalPresentCalls = 100;
    Frame(100);
    Check(!Query(), "observing counts never requests takeover");
    RbaCompositionStatus compositionStatus{};
    static_assert(sizeof(RbaCompositionStatus) == 200, "composition ABI size");
    compositionStatus.structSize = sizeof(compositionStatus); compositionStatus.abiVersion = 1;
    Check(RbaPresent_GetCompositionStatus(&compositionStatus) == 1 && compositionStatus.sourceValid == 1 &&
        compositionStatus.width == swapChain.width && compositionStatus.height == 360 &&
        compositionStatus.format == DXGI_FORMAT_R8G8B8A8_UNORM && compositionStatus.swapEffect == DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL &&
        compositionStatus.active == 0, "composition descriptor is cached passively without creating a device");
    Check(Status().countValid == 1 && Status().lastBeforeCount == 100, "initial count establishes a baseline");
    ++swapChain.totalPresentCalls; // Model Unity's own Present after our false query.
    Frame(101);
    Check(!Query(), "passive observation continues");
    Check(Status().lastBeforeDelta == 1 && Status().passiveCallDeltas == 1 &&
        Status().lastDeltaFrameSpan == 1, "passive baseline records one API call across one ticket");
    ++swapChain.totalPresentCalls;
    Enable();
    Frame(102);
    Check(Query(), "observed takeover performs Present");
    Check(Status().lastBeforeCount == 102 && Status().lastAfterCount == 103 && Status().lastOwnedDelta == 1,
        "before/after raw counters prove exactly one owned API call");
    Check(Status().ownedSingleCallSamples == 1 && Status().unexplainedCallDeltas == 0,
        "first owned counter sample is exact");
    ++swapChain.totalPresentCalls; // Simulate an erroneous extra Unity Present.
    Frame(103);
    Check(Query(), "next observed owned frame");
    Check(Status().unexplainedCallDeltas == 1, "extra call after owned Present is detected at next query");
    swapChain.extraCallsInsidePresent = 1;
    Frame(104);
    Check(Query(), "instrumented duplicate during owned call");
    Check(Status().lastOwnedDelta == 2 && Status().ownedCallMismatches == 1,
        "more than one call inside owned interval is not certified");
    swapChain.extraCallsInsidePresent = 0;
    const uint32_t continuousEpoch = Status().countEpoch;
    swapChain.countResult = E_NOTIMPL;
    Frame(105);
    Check(Query(), "unsupported observational query does not change Present arguments or ownership");
    Check(Status().countValid == 0 && Status().countFailures == 2 && Status().countEpoch > continuousEpoch &&
        Status().countResetReason == RbaPresentCountReason_QueryFailed, "failed counter reads invalidate continuity");
    swapChain.countResult = S_OK;
    Frame(106);
    Check(Query() && Status().countValid == 1, "counter recovery begins a new evidence epoch");
    const uint32_t recoveredEpoch = Status().countEpoch;
    swapChain.totalPresentCalls = 2;
    Frame(107);
    Check(Query(), "counter rollback keeps presentation alive");
    Check(Status().countEpoch > recoveredEpoch && Status().countResetReason == RbaPresentCountReason_Disjoint,
        "counter rollback is explicit disjoint evidence");
    const uint32_t rollbackEpoch = Status().countEpoch;
    ++swapChain.width;
    Frame(108);
    Check(Query() && Status().countEpoch > rollbackEpoch && Status().countResetReason == RbaPresentCountReason_ChainChanged,
        "resize descriptor change starts a new count epoch");
    SwapChain replacement;
    replacement.totalPresentCalls = 50;
    selectedSwapChain = &replacement;
    const uint32_t resizeEpoch = Status().countEpoch;
    Frame(109);
    Check(Query() && Status().countEpoch > resizeEpoch && Status().lastOwnedDelta == 1,
        "new swapchain identity resets baseline before proving its own Present");
    const uint64_t certifiedBeforeFailure = Status().ownedDeltaSamples;
    replacement.result = DXGI_ERROR_DEVICE_RESET;
    Frame(110);
    Check(Query() && Status().countValid == 0 && Status().countResetReason == RbaPresentCountReason_PresentFailed,
        "failed Present invalidates count evidence despite successful raw count reads");
    Check(Status().ownedDeltaSamples == certifiedBeforeFailure, "failed Present is never certified as a successful owned interval");
    replacement.result = S_OK;
    Check(RbaPresent_Request(0, 0) == 1, "stop observed takeover");
    Frame(111);
    Check(!Query(), "observed disable returns normal ownership");
    std::thread pendingInvalidation([] { Check(!Query(), "wrong-thread passive query requests evidence invalidation"); });
    pendingInvalidation.join();
    Check(Status().countValid == 0 && Status().countReason == RbaPresentCountReason_WrongThread,
        "pending invalidation is visible before render thread processes it");
    Frame(111);
    Check(Status().countValid == 0 && Status().countResetReason == RbaPresentCountReason_InvalidFrame,
        "invalid frame ticket invalidates count evidence");
    deviceCallback(kUnityGfxDeviceEventBeforeReset);
    Check(Status().countValid == 0 && Status().countResetReason == RbaPresentCountReason_DeviceReset,
        "device reset invalidates count evidence");
    deviceCallback(kUnityGfxDeviceEventAfterReset);
    Frame(1);
    Check(!Query() && Status().countValid == 1, "observation recovers passively on the new device sequence");
    const UINT readsBeforeDisable = replacement.countReads;
    Check(RbaPresent_Observe(0, 0) == 1, "disable read-only observation");
    Frame(2);
    Check(!Query() && replacement.countReads == readsBeforeDisable && Status().countValid == 0,
        "disabled observation stops COM reads and clears evidence");
    Check(replacement.refs == 1 && swapChain.refs == 1, "observation retains no swapchain references");
    Check(RbaPresent_Observe(1, RbaPresentContract_Unity6000_5_8f1) == 1, "observation for composition rejection test");
    Check(RbaPresent_RequestComposition(1, RbaPresentContract_All) == 0, "composition requires current valid count baseline");
    Frame(3);
    Check(!Query(), "fresh passive baseline before composition request");
    Check(RbaPresent_RequestComposition(1, RbaPresentContract_All) == 1, "explicit composition request accepted");
    Frame(4);
    Check(!Query() && Status().requested == 0, "unsupported composition source yields Unity without Present");
    Check(RbaPresent_GetCompositionStatus(&compositionStatus) == 1 && compositionStatus.active == 0 &&
        compositionStatus.presentAttempts == 0 && FAILED(compositionStatus.lastResult), "composition preparation failure is observable and passive");
    Check(RbaPresent_RequestComposition(0, 0) == 1, "composition disable always accepted");
    Check(RbaPresent_GetCompositionStatus(nullptr) == 0, "null composition status rejected");
    UnityPluginUnload();
    selectedSwapChain = &swapChain;
    Check(RbaPresent_GetStatus(nullptr) == 0, "null status pointer rejected");
    RbaPresentStatus invalid{};
    Check(RbaPresent_GetStatus(&invalid) == 0, "invalid status ABI rejected");
    std::printf("Passed %d native presentation checks (mock Unity/DXGI; no player validation).\n", checks);
    return 0;
}
