#include "FrameGenerationCoordinator.h"
#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <IUnityGraphics.h>
#include <IUnityGraphicsD3D11.h>
#include <IUnityRenderingExtensions.h>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>

namespace
{
    int checks = 0;
    void Check(bool condition, const char* message)
    { ++checks; if (!condition) { std::fprintf(stderr, "FAILED: %s\n", message); std::exit(1); } }
    class Output final : public IDXGIOutput6
    {
    public:
        ULONG references = 1;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** output) override
        { if (id != __uuidof(IDXGIOutput6) && id != __uuidof(IUnknown)) return E_NOINTERFACE; *output = this; AddRef(); return S_OK; }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
        ULONG STDMETHODCALLTYPE Release() override { return --references; }
        HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID, UINT, const void*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID, const IUnknown*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID, UINT*, void*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetParent(REFIID, void**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDesc(DXGI_OUTPUT_DESC*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDisplayModeList(DXGI_FORMAT, UINT, UINT*, DXGI_MODE_DESC*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE FindClosestMatchingMode(const DXGI_MODE_DESC*, DXGI_MODE_DESC*, IUnknown*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE WaitForVBlank() override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE TakeOwnership(IUnknown*, BOOL) override { return E_NOTIMPL; }
        void STDMETHODCALLTYPE ReleaseOwnership() override {}
        HRESULT STDMETHODCALLTYPE GetGammaControlCapabilities(DXGI_GAMMA_CONTROL_CAPABILITIES*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetGammaControl(const DXGI_GAMMA_CONTROL*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetGammaControl(DXGI_GAMMA_CONTROL*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetDisplaySurface(IDXGISurface*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDisplaySurfaceData(IDXGISurface*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDisplayModeList1(DXGI_FORMAT, UINT, UINT*, DXGI_MODE_DESC1*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE FindClosestMatchingMode1(const DXGI_MODE_DESC1*, DXGI_MODE_DESC1*, IUnknown*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDisplaySurfaceData1(IDXGIResource*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE DuplicateOutput(IUnknown*, IDXGIOutputDuplication**) override { return E_NOTIMPL; }
        BOOL STDMETHODCALLTYPE SupportsOverlays() override { return FALSE; }
        HRESULT STDMETHODCALLTYPE CheckOverlaySupport(DXGI_FORMAT, IUnknown*, UINT*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE CheckOverlayColorSpaceSupport(DXGI_FORMAT, DXGI_COLOR_SPACE_TYPE, IUnknown*, UINT*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE DuplicateOutput1(IUnknown*, UINT, UINT, const DXGI_FORMAT*, IDXGIOutputDuplication**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDesc1(DXGI_OUTPUT_DESC1* desc) override
        { *desc = {}; desc->ColorSpace = DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020; desc->BitsPerColor = 10; return S_OK; }
        HRESULT STDMETHODCALLTYPE CheckHardwareCompositionSupport(UINT*) override { return E_NOTIMPL; }
    } monitor;
    class SwapChain final : public IDXGISwapChain
    {
    public:
        ULONG references = 1;
        UINT presents = 0, countReads = 0, descReads = 0, outputReads = 0;
        UINT width = 2560, height = 1440;
        UINT creationFlags = 0x842;
        BOOL windowed = TRUE;
        HRESULT countResult = S_OK;
        bool exposeHdrMonitor = false;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
        ULONG STDMETHODCALLTYPE Release() override { return --references; }
        HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID, UINT, const void*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID, const IUnknown*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID, UINT*, void*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetParent(REFIID, void**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDevice(REFIID, void**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE Present(UINT interval, UINT flags) override
        { Check(interval == 1 && flags == 0, "mock Unity preserves source Present arguments"); ++presents; return S_OK; }
        HRESULT STDMETHODCALLTYPE GetBuffer(UINT, REFIID, void**) override { Check(false, "Off must not borrow final texture"); return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL, IDXGIOutput*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL*, IDXGIOutput**) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC* desc) override
        {
            ++descReads; *desc = {}; desc->BufferDesc.Width = width; desc->BufferDesc.Height = height;
            desc->BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc->SampleDesc.Count = 1;
            desc->BufferCount = 2; desc->Windowed = windowed; desc->SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            desc->Flags = creationFlags; desc->OutputWindow = reinterpret_cast<HWND>(static_cast<uintptr_t>(1)); return S_OK;
        }
        HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT, UINT, UINT, DXGI_FORMAT, UINT) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput** output) override
        { ++outputReads; if (!exposeHdrMonitor) return E_NOTIMPL; *output = &monitor; monitor.AddRef(); return S_OK; }
        HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS*) override { return E_NOTIMPL; }
        HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT* count) override { ++countReads; *count = presents; return countResult; }
    } chain;
    IUnityInterfaces registry{};
    IUnityGraphics graphics{};
    IUnityGraphicsD3D11 d3d{};
    IUnityGraphicsDeviceEventCallback deviceCallback = nullptr;
    UINT registrations = 0, unregistrations = 0, swapReads = 0, deviceReads = 0;
    UINT sourceInterval = 1, sourcePresentFlags = 0;
    UnityGfxRenderer UNITY_INTERFACE_API GetRenderer() { return kUnityGfxRendererD3D11; }
    void UNITY_INTERFACE_API Register(IUnityGraphicsDeviceEventCallback callback) { deviceCallback = callback; ++registrations; }
    void UNITY_INTERFACE_API Unregister(IUnityGraphicsDeviceEventCallback callback)
    { Check(callback == deviceCallback, "unregister exact callback"); deviceCallback = nullptr; ++unregistrations; }
    int UNITY_INTERFACE_API Reserve(int count) { Check(count == 2, "reserve separate capture and EOF event IDs"); return 830; }
    ID3D11Device* UNITY_INTERFACE_API GetDevice() { ++deviceReads; return reinterpret_cast<ID3D11Device*>(static_cast<uintptr_t>(1)); }
    IDXGISwapChain* UNITY_INTERFACE_API GetSwapChain() { ++swapReads; return &chain; }
    UINT32 UNITY_INTERFACE_API GetSyncInterval() { return sourceInterval; }
    UINT UNITY_INTERFACE_API GetPresentFlags() { return sourcePresentFlags; }
    IUnityInterface* UNITY_INTERFACE_API GetInterface(UnityInterfaceGUID guid)
    {
        if (guid == GetUnityInterfaceGUID<IUnityGraphics>()) return &graphics;
        if (guid == GetUnityInterfaceGUID<IUnityGraphicsD3D11>()) return &d3d;
        return nullptr;
    }
    RbaFgCoordinatorStatus Status()
    {
        RbaFgCoordinatorStatus s{}; s.structSize = sizeof(s); s.abiVersion = 1;
        Check(RbaFgGetStatus(&s) == RbaFgOk, "status ABI roundtrip"); return s;
    }
    RbaFgCoordinatorTicketStatus Ticket(uint64_t id)
    {
        RbaFgCoordinatorTicketStatus s{}; s.structSize = sizeof(s); s.abiVersion = 1;
        Check(RbaFgQueryTicket(id, &s) == RbaFgOk, "ticket ABI roundtrip"); return s;
    }
    struct Packet { uint64_t id = 0; void* data = nullptr; };
    Packet Eof(uint32_t frame, uint64_t capture = 0)
    { Packet p; Check(RbaFgPrepareEndOfFrame(frame, capture, &p.id, &p.data) == RbaFgOk, "prepare immutable EOF"); return p; }
    Packet Capture(uint32_t frame)
    {
        RbaFgCoordinatorCapture c{}; c.structSize = sizeof(c); c.abiVersion = 1; c.realFrameId = frame;
        Packet p; Check(RbaFgPrepareCapture(&c, &p.id, &p.data) == RbaFgOk, "prepare no-scene capture"); return p;
    }
    void Event(const Packet& packet, int eventId)
    { reinterpret_cast<UnityRenderingEventAndData>(RbaFgGetRenderEventFunc())(eventId, packet.data); }
    bool Query() { return UnityRenderingExtQuery(kUnityRenderingExtQueryOverridePresentFrame); }
    void Release(const Packet& packet) { Check(RbaFgReleaseTicket(packet.id) == RbaFgOk, "release acknowledged retired packet"); }
}

int main()
{
    static_assert(sizeof(RbaFgCoordinatorConfig) == 32 && sizeof(RbaFgCoordinatorCamera) == 304 &&
        sizeof(RbaFgCoordinatorCapture) == 368 && sizeof(RbaFgCoordinatorTicketStatus) == 40 && sizeof(RbaFgCoordinatorStatus) == 792, "ABI1");
    static_assert(sizeof(RbaFgCoordinatorConfigV2) == 64 && sizeof(RbaFgCoordinatorStatusV2) == 904, "Additive ABI2");
    registry.GetInterface = GetInterface; graphics.GetRenderer = GetRenderer;
    graphics.RegisterDeviceEventCallback = Register; graphics.UnregisterDeviceEventCallback = Unregister;
    graphics.ReserveEventIDRange = Reserve; d3d.GetDevice = GetDevice; d3d.GetSwapChain = GetSwapChain;
    d3d.GetSyncInterval = GetSyncInterval; d3d.GetPresentFlags = GetPresentFlags;
    RbaFgCoordinatorConfig off{}; off.structSize = sizeof(off); off.abiVersion = 1;
    Check(RbaFgConfigure(&off) == RbaFgNotLoaded && !Query(), "unloaded coordinator does not own presentation");
    UnityPluginLoad(&registry); UnityPluginLoad(&registry);
    Check(registrations == 1 && Status().loaded == 1, "idempotent exact Unity registration");
    Check(Status().captureEventId == 830 && Status().endOfFrameEventId == 831, "stable distinct render event IDs");
    Check(Status().requestedMode == 0 && Status().state == RbaFgDisabled && Status().nvidiaMultiplierMask == 0,
        "passive startup does not advertise fabricated capabilities");
    Check(!Query() && swapReads == 0, "no swapchain access before a genuine EOF establishes render thread");
    Check(!UnityRenderingExtQuery(kUnityRenderingExtQueryOverrideViewport), "no unrelated renderer override");
    Check(RbaFgConfigure(&off) == RbaFgOk && RbaFgTick(1, 0) == RbaFgOk, "main-thread Off controls");
    Check(RbaFgBeginSimulation(1) == RbaFgPending && RbaFgEndSimulation(1) == RbaFgPending &&
        RbaFgBeginRender(1) == RbaFgPending, "no provider means no fabricated markers");
    Check(RbaFgEndRender(1) == RbaFgWrongThread, "EndRender requires actual established render thread");

    auto c = Capture(1); auto e = Eof(1, c.id);
    Check(!Ticket(c.id).eventConsumed && !Ticket(c.id).sourcePixelsRetired && !Ticket(c.id).fullyRetired,
        "preparation cannot retire queued event or source pixels");
    Check(RbaFgReleaseTicket(c.id) == RbaFgBusy && RbaFgReleaseTicket(e.id) == RbaFgBusy, "unconsumed packets retained");
    Event(c, 831); Check(!Ticket(c.id).eventConsumed, "wrong event ID cannot acknowledge capture");
    Event(c, 830);
    Check(Ticket(c.id).eventConsumed && Ticket(c.id).sourcePixelsRetired && !Ticket(c.id).fullyRetired,
        "no-borrow capture pixels retire but EOF retains metadata");
    Event(e, 831); Event(e, 831);
    Check(Ticket(e.id).fullyRetired && Status().finalRealFrameId == 1, "EOF consumed exactly once");
    Release(e);
    Check(RbaFgReleaseTicket(c.id) == RbaFgBusy, "joined final-frame metadata retains capture until query");
    Check(!Query(), "Off yields original source Present");
    Check(Ticket(c.id).fullyRetired, "Off query drops capture pin safely"); Release(c);
    Check(RbaFgReleaseTicket(c.id) == RbaFgInvalidTicket, "retired ticket cannot release a future slot occupant");
    auto s = Status();
    Check(s.sourceWidth == 2560 && s.sourceHeight == 1440 && s.sourceFormat == 28 && s.sourceSwapEffect == 3 &&
        s.sourceFlags == 0x842 && s.sourceWindowed == 1 && s.sourceSyncInterval == 1 && s.sourcePresentFlags == 0,
        "native source telemetry preserves physical extent and actual flags");
    Check(chain.presents == 0 && chain.outputReads == 0 && deviceReads == 0 && chain.references == 1,
        "passive observation neither presents nor loads graphics/provider context");
    chain.Present(1, 0); Check(!Query() && chain.presents == 1 && Status().sourceUnexpectedCalls == 0,
        "duplicate passive query does not mistake Unity's allowed call for generated output");
    Check(RbaFgEndRender(1) == RbaFgPending, "EOF does not fabricate missing BeginRender");

    RbaFgCoordinatorCapture immutable{}; immutable.structSize = sizeof(immutable); immutable.abiVersion = 1; immutable.realFrameId = 2;
    Packet copied; Check(RbaFgPrepareCapture(&immutable, &copied.id, &copied.data) == RbaFgOk, "capture descriptor copied before caller reuse");
    immutable.realFrameId = 999; immutable.sceneEligible = 1; immutable.hudlessColor11 = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
    Check(Ticket(copied.id).realFrameId == 2, "event frame cannot change with caller descriptor");
    Packet invalid; Check(RbaFgPrepareEndOfFrame(3, copied.id, &invalid.id, &invalid.data) == RbaFgInvalidTicket,
        "cross-frame join rejected without borrowing capture");
    e = Eof(2, copied.id);
    Check(RbaFgConfigure(&off) == RbaFgOk && RbaFgReleaseTicket(copied.id) == RbaFgBusy,
        "disable preserves outstanding managed source/event lifetime");
    Event(copied, 830); Event(e, 831); Check(!Query(), "immutable no-scene descriptor remains passive");
    Release(e); Release(copied);
    Check(RbaFgPrepareEndOfFrame(2, 0, &invalid.id, &invalid.data) == RbaFgInvalidArgument, "past EOF cannot reset ownership");
    chain.countResult = DXGI_ERROR_DEVICE_REMOVED; e = Eof(3); Event(e, 831);
    Check(!Query() && Status().sourcePresentAttempts == 0, "invalid count observation remains passive"); Release(e);
    chain.countResult = S_OK; chain.width = 1920; chain.height = 1080;
    e = Eof(4); Event(e, 831); Check(!Query() && Status().sourceWidth == 1920, "resize updates native snapshot"); Release(e);

    auto nv = off; nv.mode = RbaFgNvidia2x; nv.verifiedContracts = 15;
    Check(RbaFgConfigure(&nv) == RbaFgInvalidArgument, "activation rejects missing absolute audited runtime paths");
    nv.providerDllPath = L"relative.dll"; nv.runtimeDirectory = L"relative";
    Check(RbaFgConfigure(&nv) == RbaFgInvalidArgument, "activation rejects relative paths");
    nv.mode = RbaFgAmd2x; Check(RbaFgConfigure(&nv) == RbaFgUnsupported, "unintegrated AMD route is explicit unsupported");
    Check(Status().requestedMode == 0 && Status().providerRealPresents == 0 && Status().generatedPresentationObserved == 0,
        "failed configuration does not publish readiness or change requested mode");

    std::array<Packet, 24> capacity{};
    for (uint32_t i = 0; i < capacity.size(); ++i) capacity[i] = Eof(100 + i);
    Check(RbaFgPrepareEndOfFrame(200, 0, &invalid.id, &invalid.data) == RbaFgBusy && invalid.id == 0 && invalid.data == nullptr,
        "full packet queue applies backpressure without taking ownership");
    Check(Status().pendingTickets == 24 && RbaFgReleaseTicket(capacity.front().id) == RbaFgBusy,
        "capacity pressure cannot discard oldest unconsumed event");
    for (const auto& p : capacity) { Event(p, 831); Check(!Query(), "capacity EOF stays passive"); Release(p); }
    Check(Status().pendingTickets == 0, "all acknowledged capacity records release");

    RbaFgCoordinatorStatusV2 v2{}; v2.structSize = sizeof(v2); v2.abiVersion = 2;
    Check(RbaFgGetStatusV2(&v2) == RbaFgOk && v2.base.structSize == 792 && v2.base.abiVersion == 1 &&
        v2.base.sourceWidth == 1920 && v2.amdBestMultiplierMask == 0 && v2.amdCompatibilityMultiplierMask == 0 &&
        v2.completedDiscoveryMask == 0, "additive V2 preserves ABI1 snapshot and never invents AMD support");
    auto wrongV2 = v2; wrongV2.abiVersion = 1;
    Check(RbaFgGetStatusV2(&wrongV2) == RbaFgInvalidArgument, "V2 status rejects ABI1 layout claim");
    RbaFgCoordinatorConfigV2 amd{}; amd.structSize = sizeof(amd); amd.abiVersion = 2;
    amd.mode = RbaFgAmdProbeBest; amd.verifiedContracts = 15;
    Check(RbaFgConfigureV2(&amd) == RbaFgInvalidArgument, "AMD discovery requires its own absolute provider/runtime paths");
    amd.amdProviderDllPath = L"G:\\missing-reviewed-test-runtime\\ReduxBetterAA.AmdFrameGeneration.dll";
    amd.amdRuntimeDirectory = L"G:\\missing-reviewed-test-runtime\\amd";
    Check(RbaFgConfigureV2(&amd) == RbaFgOk && Status().requestedMode == RbaFgAmdProbeBest,
        "hidden best discovery accepts explicit paths without loading an SDK on main");
    e = Eof(250); Event(e, 831); Check(!Query(), "unsupported source output blocks AMD provider initialization"); Release(e);
    Check(RbaFgGetStatusV2(&v2) == RbaFgOk && !v2.amdBestMultiplierMask && !v2.completedDiscoveryMask &&
        !v2.amdPresentPermitHeld && !v2.amdWorkerPending && deviceReads == 0,
        "configuration and unsupported source are not capability, provider or async ownership evidence");
    amd.mode = RbaFgAmdCompatibility2x;
    Check(RbaFgConfigureV2(&amd) == RbaFgInvalidArgument, "compatibility mode requires matching explicit preference");
    amd.amdPreferCompatibility = 1; amd.amdRenderWidth = 960;
    Check(RbaFgConfigureV2(&amd) == RbaFgInvalidArgument, "partial AMD render geometry cannot configure context");
    amd.amdRenderHeight = 540; amd.amdReversedDepth = 2;
    Check(RbaFgConfigureV2(&amd) == RbaFgInvalidArgument, "invalid depth convention rejected before worker creation");
    amd.amdReversedDepth = 1;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk && Status().requestedMode == RbaFgAmdCompatibility2x,
        "explicit compatibility mode retains real geometry for later provider initialization");
    Check(RbaFgConfigure(&off) == RbaFgOk && Status().requestedMode == RbaFgOff && Status().state == RbaFgDisabled,
        "legacy ABI1 Off can safely disable additive AMD selection");
    amd.mode = RbaFgAmdProbeBest; amd.amdPreferCompatibility = 0; chain.exposeHdrMonitor = true;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk, "configure hidden discovery with actual HDR monitor observation");
    e = Eof(251); Event(e, 831); Check(!Query(), "missing SDK still yields Unity on HDR desktop"); Release(e);
    s = Status();
    Check(s.result == RbaFgProviderError && std::strstr(s.reason, "Pinned AMD provider DLL") != nullptr &&
        std::strstr(s.reason, "monitor ColorSpace=12 bits=10") != nullptr && monitor.references == 1,
        "HDR display does not reject SDR swapchain contract and stays a distinct recorded observation");
    Check(RbaFgConfigure(&off) == RbaFgOk, "Off clears failed optional provider discovery");
    sourceInterval = 0; sourcePresentFlags = DXGI_PRESENT_ALLOW_TEARING;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk, "configure exact valid source tearing contract");
    e = Eof(252); Event(e, 831); Check(!Query(), "missing provider still yields source on valid tearing path"); Release(e);
    s = Status();
    Check(s.result == RbaFgProviderError && s.sourceSyncInterval == 0 && s.sourcePresentFlags == DXGI_PRESENT_ALLOW_TEARING,
        "windowed sync0 plus creation ALLOW_TEARING accepts exact Present0x200 without stripping it");
    Check(RbaFgConfigure(&off) == RbaFgOk, "restore after optional provider attempt");
    chain.creationFlags = 0x42;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk, "configure missing creation tearing flag negative case");
    e = Eof(253); Event(e, 831); Check(!Query(), "invalid tearing creation contract stays with Unity"); Release(e);
    Check(Status().result == RbaFgUnsupported, "Present tearing requires original chain ALLOW_TEARING");
    Check(RbaFgConfigure(&off) == RbaFgOk, "restore after missing creation flag");
    chain.creationFlags = 0x842; sourceInterval = 1;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk, "configure tearing sync mismatch negative case");
    e = Eof(254); Event(e, 831); Check(!Query(), "tearing with sync1 stays with Unity"); Release(e);
    Check(Status().result == RbaFgUnsupported, "Present tearing requires sync interval0");
    Check(RbaFgConfigure(&off) == RbaFgOk, "restore after sync mismatch");
    sourceInterval = 0; sourcePresentFlags |= DXGI_PRESENT_TEST;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk, "configure extra Present flag negative case");
    e = Eof(255); Event(e, 831); Check(!Query(), "extra flags do not enter source takeover"); Release(e);
    Check(Status().result == RbaFgUnsupported, "valid tearing flag cannot mask unvalidated extra Present flags");
    Check(RbaFgConfigure(&off) == RbaFgOk, "restore after extra flag case");
    sourcePresentFlags = DXGI_PRESENT_ALLOW_TEARING; chain.windowed = FALSE;
    Check(RbaFgConfigureV2(&amd) == RbaFgOk, "configure exclusive tearing negative case");
    e = Eof(256); Event(e, 831); Check(!Query(), "exclusive source stays with Unity"); Release(e);
    Check(Status().result == RbaFgUnsupported, "tearing does not relax the windowed source requirement");
    Check(RbaFgConfigure(&off) == RbaFgOk, "restore complete source gate test state");
    sourceInterval = 1; sourcePresentFlags = 0; chain.windowed = TRUE;

    c = Capture(300); e = Eof(300, c.id); Event(c, 830);
    UINT readsBefore = swapReads;
    std::thread foreign([&] {
        Check(RbaFgTick(300, 0) == RbaFgWrongThread && RbaFgConfigure(&off) == RbaFgWrongThread,
            "control cannot move window ownership to worker");
        Check(RbaFgBeginSimulation(300) == RbaFgWrongThread, "simulation marker cannot move to worker");
        Check(RbaFgConfigureV2(&amd) == RbaFgWrongThread, "V2 configuration preserves original owner thread");
        Check(!Query(), "unexpected query thread preserves Unity ownership");
        Event(e, 831);
    }); foreign.join();
    Check(swapReads == readsBefore && Status().wrongThreadQueries == 2, "wrong thread performs no source COM calls");
    Check(Ticket(e.id).result == RbaFgWrongThread && Ticket(e.id).fullyRetired && Ticket(c.id).fullyRetired,
        "rejected wrong-thread EOF drops its CPU capture reference without leaking source lease");
    Release(e); Release(c);
    Check(Status().state == RbaFgDisabled && Status().actualMultiplier == 0,
        "fully drained Off can finish retirement despite historical wrong-thread counter");
    deviceCallback(kUnityGfxDeviceEventBeforeReset); deviceCallback(kUnityGfxDeviceEventAfterReset);
    e = Eof(400); Event(e, 831); Check(!Query(), "reset with no provider drains without taking ownership"); Release(e);
    Check(Status().state == RbaFgDisabled && Status().nvidiaMultiplierMask == 0 && Status().sourcePresentAttempts == 0,
        "device generation clears capabilities and completes empty drain");
    Check(Status().pendingTickets == 0 && RbaFgTick(401, 0) == RbaFgOk, "final main tick has no orphan window or packet");
    Check(RbaFgGetDiagnostics(nullptr) == RbaFgInvalidArgument, "diagnostics reject a null output without native work");
    RbaFgCoordinatorDiagnostics diagnostics{}; diagnostics.structSize = sizeof(diagnostics); diagnostics.abiVersion = 2;
    Check(RbaFgGetDiagnostics(&diagnostics) == RbaFgInvalidArgument, "diagnostics preserve their separate ABI version");
    diagnostics.abiVersion = 1;
    Check(RbaFgGetDiagnostics(&diagnostics) == RbaFgOk && diagnostics.structSize == 336 && diagnostics.lastMainFrame == 401 &&
        diagnostics.finalRealFrame == 400 && diagnostics.readyRealFrame == 0 && diagnostics.readyAgeMilliseconds == UINT64_MAX,
        "actual DLL exposes independent main/render frontiers and invalid-ready sentinel");
    Check(diagnostics.realEofFrames > 1 && diagnostics.captureEvents > 0 && diagnostics.captureUnavailable > 0 &&
        diagnostics.successfulSubmissions == 0 && diagnostics.waitCalls == 0,
        "real mocked events accumulate passive/gated diagnostics across device reset without inventing submissions");
    const auto eofCount = diagnostics.realEofFrames;
    const auto readsBeforeDiagnostics = swapReads;
    Check(RbaFgGetDiagnostics(&diagnostics) == RbaFgOk && diagnostics.realEofFrames == eofCount && swapReads == readsBeforeDiagnostics,
        "repeated diagnostic reads do not consume counters or issue scene work");
    UnityPluginUnload(); Check(unregistrations == 1 && Status().loaded == 0, "explicit unload unregisters once");
    std::printf("Passed %d actual-DLL coordinator checks; mocked Unity/DXGI, no GPU, SDK, child window or FG claims.\n", checks);
    return 0;
}
