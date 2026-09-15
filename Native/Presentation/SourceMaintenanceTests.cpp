#include "PresentationProbe.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <IUnityGraphics.h>
#include <IUnityGraphicsD3D11.h>
#include <IUnityRenderingExtensions.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <thread>

extern "C" __declspec(dllimport) void __cdecl RbaPresent_TestFailNextDetach();
using Microsoft::WRL::ComPtr;
namespace {
int checks = 0;
void Check(bool value, const char* text) { ++checks; if (!value) { std::fprintf(stderr,"FAIL: %s\n",text); std::exit(1); } }
void Hr(HRESULT result, const char* text) { if(FAILED(result)) std::fprintf(stderr,"HRESULT=0x%08lx\n",static_cast<unsigned long>(result)); Check(SUCCEEDED(result),text); }
void Pump() { MSG msg{}; while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); } }
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> immediate;
class ForwardChain final : public IDXGISwapChain {
public:
    ComPtr<IDXGISwapChain1> real;
    std::atomic<ULONG> refs{1};
    uint64_t attempts=0;
    bool failNext=false;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if(iid==__uuidof(IUnknown)||iid==__uuidof(IDXGISwapChain)||iid==__uuidof(IDXGIDeviceSubObject)||iid==__uuidof(IDXGIObject)) { *out=this; AddRef(); return S_OK; }
        return real->QueryInterface(iid,out);
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID id,UINT size,const void* data) override { return real->SetPrivateData(id,size,data); }
    HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID id,const IUnknown* data) override { return real->SetPrivateDataInterface(id,data); }
    HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID id,UINT* size,void* data) override { return real->GetPrivateData(id,size,data); }
    HRESULT STDMETHODCALLTYPE GetParent(REFIID iid,void** out) override { return real->GetParent(iid,out); }
    HRESULT STDMETHODCALLTYPE GetDevice(REFIID iid,void** out) override { return real->GetDevice(iid,out); }
    HRESULT STDMETHODCALLTYPE Present(UINT interval,UINT flags) override {
        ++attempts; Check(interval==1 && flags==0,"original arguments preserved");
        if(failNext) { failNext=false; return DXGI_ERROR_DEVICE_RESET; }
        return real->Present(interval,flags);
    }
    HRESULT STDMETHODCALLTYPE GetBuffer(UINT i,REFIID iid,void** out) override { return real->GetBuffer(i,iid,out); }
    HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL full,IDXGIOutput* output) override { return real->SetFullscreenState(full,output); }
    HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL* full,IDXGIOutput** output) override { return real->GetFullscreenState(full,output); }
    HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC* desc) override { return real->GetDesc(desc); }
    HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT count,UINT w,UINT h,DXGI_FORMAT f,UINT flags) override { return real->ResizeBuffers(count,w,h,f,flags); }
    HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC* desc) override { return real->ResizeTarget(desc); }
    HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput** output) override { return real->GetContainingOutput(output); }
    HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS* stats) override { return real->GetFrameStatistics(stats); }
    HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT* count) override { return real->GetLastPresentCount(count); }
} chain;
IUnityInterfaces registry{};
IUnityGraphics graphics{};
IUnityGraphicsD3D11 unity11{};
IUnityGraphicsDeviceEventCallback callback=nullptr;
UINT disableWhileAcquiring=0;
UnityGfxRenderer UNITY_INTERFACE_API Renderer() { return kUnityGfxRendererD3D11; }
void UNITY_INTERFACE_API Register(IUnityGraphicsDeviceEventCallback value) { callback=value; }
void UNITY_INTERFACE_API Unregister(IUnityGraphicsDeviceEventCallback value) { Check(value==callback,"exact device callback removed"); callback=nullptr; }
int UNITY_INTERFACE_API Reserve(int count) { Check(count==1,"one final-frame event"); return 700; }
ID3D11Device* UNITY_INTERFACE_API Device() { return device.Get(); }
IDXGISwapChain* UNITY_INTERFACE_API Chain() { return &chain; }
UINT32 UNITY_INTERFACE_API Interval() { return 1; }
UINT UNITY_INTERFACE_API Flags() { if(disableWhileAcquiring!=0 && --disableWhileAcquiring==0) { Check(RbaPresent_RequestCompositionWithSourceMaintenance(0,15)==1,"concurrent disable accepted"); } return 0; }
IUnityInterface* UNITY_INTERFACE_API Interface(UnityInterfaceGUID guid) {
    if(guid==GetUnityInterfaceGUID<IUnityGraphics>()) return &graphics;
    if(guid==GetUnityInterfaceGUID<IUnityGraphicsD3D11>()) return &unity11;
    return nullptr;
}
RbaPresentStatus Status() { RbaPresentStatus s{}; s.structSize=sizeof(s); s.abiVersion=2; Check(RbaPresent_GetStatus(&s)==1,"original status ABI"); return s; }
RbaCompositionStatus Overlay() { RbaCompositionStatus s{}; s.structSize=sizeof(s); s.abiVersion=1; Check(RbaPresent_GetCompositionStatus(&s)==1,"overlay status ABI"); return s; }
uint64_t frameId=0;
void Frame() { RbaPresentFrame f{sizeof(f),1,++frameId}; reinterpret_cast<UnityRenderingEventAndData>(RbaPresent_GetRenderEventFunc())(700,&f); }
bool Query() { return UnityRenderingExtQuery(kUnityRenderingExtQueryOverridePresentFrame); }
void Fill() { ComPtr<ID3D11Texture2D> buffer; Hr(chain.GetBuffer(0,IID_PPV_ARGS(&buffer)),"logical source buffer"); ComPtr<ID3D11RenderTargetView> rtv; Hr(device->CreateRenderTargetView(buffer.Get(),nullptr,&rtv),"source view"); const float color[4]={0.08f,0.3f,0.85f,0.f}; immediate->ClearRenderTargetView(rtv.Get(),color); }
void Wait(HANDLE ready) { Pump(); Check(WaitForSingleObject(ready,2000)==WAIT_OBJECT_0,"original latency handle continues"); }
void Normal(bool wait,HANDLE ready) { if(wait) Wait(ready); Frame(); Check(!Query(),"normal Unity ticket"); Hr(chain.Present(1,0),"Unity original call"); }
void Arm() { Check(RbaPresent_RequestCompositionWithSourceMaintenance(1,15)==1,"explicit maintained-source arm"); Check(Status().requested==3,"distinct external requested mode3"); }
void Owned(HANDLE ready) { Wait(ready); Fill(); Frame(); Check(Query(),"maintained composition owns ticket"); }
}
int main() {
    static_assert(sizeof(RbaPresentStatus)==312 && sizeof(RbaCompositionStatus)==200,"existing ABI sizes");
    WNDCLASSW wc{}; wc.lpfnWndProc=DefWindowProcW; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"RbaMaintainedSourceActualDllTest";
    Check(RegisterClassW(&wc)!=0,"register bounded test HWND"); RECT rect{0,0,640,360}; Check(AdjustWindowRect(&rect,WS_OVERLAPPEDWINDOW,FALSE)!=FALSE,"test physical client extent");
    HWND window=CreateWindowExW(WS_EX_NOACTIVATE,wc.lpszClassName,L"ReduxBetterAA maintained-source diagnostic",WS_OVERLAPPEDWINDOW,20,20,rect.right-rect.left,rect.bottom-rect.top,nullptr,nullptr,wc.hInstance,nullptr);
    Check(window!=nullptr,"single test window"); Check(SetWindowPos(window,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW)!=FALSE,"show own window"); Pump();
    Hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&immediate),"original D3D11 device");
    ComPtr<IDXGIFactory2> factory; Hr(CreateDXGIFactory2(0,IID_PPV_ARGS(&factory)),"DXGI factory");
    DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width=640; desc.Height=360; desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count=1; desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount=2; desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; desc.Scaling=DXGI_SCALING_STRETCH; desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE; desc.Flags=0x842;
    Hr(factory->CreateSwapChainForHwnd(device.Get(),window,&desc,nullptr,nullptr,&chain.real),"actual source flags0x842");
    ComPtr<IDXGISwapChain2> latency; Hr(chain.real.As(&latency),"source waitable interface"); Hr(latency->SetMaximumFrameLatency(2),"source latency budget2"); HANDLE ready=latency->GetFrameLatencyWaitableObject(); Check(ready!=nullptr,"source waitable handle");
    registry.GetInterface=Interface; graphics.GetRenderer=Renderer; graphics.RegisterDeviceEventCallback=Register; graphics.UnregisterDeviceEventCallback=Unregister; graphics.ReserveEventIDRange=Reserve;
    unity11.GetDevice=Device; unity11.GetSwapChain=Chain; unity11.GetSyncInterval=Interval; unity11.GetPresentFlags=Flags;
    Check(RbaPresent_RequestCompositionWithSourceMaintenance(1,15)==0,"cannot arm before native load"); UnityPluginLoad(&registry);
    Check(RbaPresent_Observe(1,1)==1,"enable passive observation"); Normal(true,ready);
    Check(Overlay().sourceFlags==0x842 && Overlay().width==640,"passive exact source descriptor");
    Check(RbaPresent_RequestCompositionWithSourceMaintenance(1,7)==0,"incomplete contract rejected"); Arm();
    const auto initial=Status(); const auto firstOverlay=Overlay(); const auto calls=chain.attempts;
    for(UINT i=0;i<120;++i) { Owned(ready); const auto before=chain.attempts; Check(Query(),"duplicate remains owned"); Check(chain.attempts==before,"duplicate never adds original call"); Check(Overlay().attached==1,"overlay remains attached"); }
    auto s=Status(); auto c=Overlay();
    Check(chain.attempts-calls==120 && s.presentAttempts-initial.presentAttempts==120,"exactly120 source attempts");
    Check(s.presentSuccesses-initial.presentSuccesses==120 && s.ownedSingleCallSamples-initial.ownedSingleCallSamples==120,"exactly120 original count deltas");
    Check(c.presentAttempts-firstOverlay.presentAttempts==120 && c.presentSuccesses-firstOverlay.presentSuccesses==120 && c.singleCallSamples-firstOverlay.singleCallSamples==120,"exactly120 independent overlay deltas");
    Check(s.presentFailures==0 && c.presentFailures==0 && c.countMismatches==0,"no failures or overlay count mismatch");
    std::printf("LIVE_DLL interval realTickets=120 originalCalls=120 originalSingleCallSamples=120 overlayCalls=120 overlaySingleCallSamples=120 sourceFlags=0x842 sync=1 flags=0\n");
    // A control update during acquisition cannot turn mode3 into strict suppression.
    disableWhileAcquiring=2; Owned(ready); Check(Status().requested==0,"in-flight disable remains disabled for future tickets"); Check(Overlay().attached==1,"current coherent mode finished");
    RbaPresent_TestFailNextDetach(); const auto beforeDrain=chain.attempts; const auto overlayBeforeDrain=Overlay().presentAttempts;
    Owned(ready); Check(chain.attempts==beforeDrain+1,"failed detach still maintains one original call"); Check(Overlay().attached==1 && Overlay().presentAttempts==overlayBeforeDrain,"failed detach retains old visual without another overlay call");
    Normal(true,ready); Check(Overlay().attached==0 && Overlay().draining==0,"detach retry restores ordinary chain");
    Arm(); Owned(ready);
    RbaPresent_TestFailNextDetach(); Check(RbaPresent_RequestComposition(1,15)==1,"request strict mode change");
    const auto beforeSwitch=chain.attempts; const auto overlayBeforeSwitch=Overlay().presentAttempts; Owned(ready);
    Check(chain.attempts==beforeSwitch+1 && Overlay().presentAttempts==overlayBeforeSwitch && Overlay().attached==1,"mode change preserves attached maintenance identity after failed detach");
    Normal(true,ready); Check(Overlay().attached==0,"strict mode rejects exact waitable source after detach");
    Arm(); Owned(ready);
    bool otherResult=false; std::thread ownedOther([&]{otherResult=Query();}); ownedOther.join(); Check(otherResult,"wrong-thread already owned ticket suppresses duplicate");
    Normal(true,ready); Normal(true,ready); Arm(); Owned(ready);
    Wait(ready); Fill(); Frame(); std::thread unownedOther([&]{otherResult=Query();}); unownedOther.join(); Check(!otherResult,"wrong-thread unclaimed maintained source yields original to Unity");
    const auto beforeWrong=chain.attempts; Check(!Query(),"render-thread cannot steal wrong-thread Unity ticket"); Check(chain.attempts==beforeWrong,"wrong-thread handoff has no probe original call"); Hr(chain.Present(1,0),"Unity maintains after wrong-thread invalidation");
    Normal(true,ready); Arm(); Owned(ready);
    const auto beforeFailure=Status(); const auto overlayBeforeFailure=Overlay().presentAttempts; chain.failNext=true; Owned(ready);
    Check(Status().presentFailures==beforeFailure.presentFailures+1 && Status().requested==0,"failed original disables future takeover");
    Check(Overlay().presentAttempts==overlayBeforeFailure,"failed original prevents overlay call"); const auto failedAttempts=chain.attempts; Check(Query() && chain.attempts==failedAttempts,"failed original never retried in duplicate query");
    Normal(false,ready); Check(Overlay().attached==0 && Overlay().draining==0,"failed original retires overlay before restoration"); Normal(true,ready);
    Check(RbaPresent_RequestCompositionWithSourceMaintenance(0,15)==1,"final disable"); UnityPluginUnload(); Check(chain.refs==1,"all original COM borrows retired");
    CloseHandle(ready); latency.Reset(); chain.real.Reset(); immediate->ClearState(); immediate->Flush(); immediate.Reset(); device.Reset(); factory.Reset(); DestroyWindow(window); UnregisterClassW(wc.lpszClassName,wc.hInstance);
    std::printf("Passed %d actual-DLL maintained-source checks; no Unity launch, generated frames or displayed FPS claim.\n",checks); return 0;
}
