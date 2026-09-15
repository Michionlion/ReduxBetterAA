#include "CompositionPresenter.h"
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace
{
    int checks = 0;
    void Check(bool value, const char* name)
    {
        ++checks;
        if (!value) { std::fprintf(stderr, "FAIL: %s\n", name); std::exit(1); }
    }
    void Hr(HRESULT value, const char* name)
    {
        if (FAILED(value)) std::fprintf(stderr, "%s HRESULT=0x%08lx\n", name, static_cast<unsigned long>(value));
        Check(SUCCEEDED(value), name);
    }
    void ResizeWindow(HWND window, UINT width, UINT height)
    {
        RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
        Check(AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE) != FALSE, "window size adjustment");
        Check(SetWindowPos(window, nullptr, 0, 0, rect.right - rect.left, rect.bottom - rect.top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE) != FALSE, "resize test client");
    }
    void Pattern(CompositionPresenter& presenter, ID3D11DeviceContext* immediate, ID3D11Device* device,
        IDXGISwapChain* source, UINT width, UINT height, uint8_t phase)
    {
        std::vector<uint8_t> expected(static_cast<size_t>(width) * height * 4);
        for (UINT y = 0; y < height; ++y) for (UINT x = 0; x < width; ++x)
        {
            const auto index = (static_cast<size_t>(y) * width + x) * 4;
            expected[index] = static_cast<uint8_t>(x + phase);
            expected[index + 1] = static_cast<uint8_t>(y + phase * 3);
            expected[index + 2] = static_cast<uint8_t>((x ^ y) + phase * 7);
            expected[index + 3] = 255;
        }
        ComPtr<ID3D11Texture2D> buffer; Hr(source->GetBuffer(0, IID_PPV_ARGS(&buffer)), "get original buffer");
        immediate->UpdateSubresource(buffer.Get(), 0, nullptr, expected.data(), width * 4, 0); buffer.Reset();
        const auto prepare = presenter.Prepare(device, source, 0, 0);
        if (FAILED(prepare)) std::fprintf(stderr, "composition reason=%u operation=%u\n", presenter.Status().reason, presenter.Status().operation);
        Hr(prepare, "prepare shared composition frame");
        std::vector<uint8_t> actual; Hr(presenter.ReadPrepared(actual), "read composition destination");
        Check(expected == actual, "exact final-buffer bytes survive 11-to-12 transport including every pixel");
        Hr(presenter.Present(0), "present one composition frame");
        Check(presenter.Status().lastOwnedDelta == 1 && presenter.Status().countValid == 1, "exactly one composition Present call");
    }
}
int main()
{
    WNDCLASSW wc{}; wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"RbaCompositionGpuTest";
    Check(RegisterClassW(&wc) != 0, "register bounded diagnostic window");
    HWND window = CreateWindowExW(0, wc.lpszClassName, L"ReduxBetterAA native composition test", WS_OVERLAPPEDWINDOW,
        0, 0, 100, 100, nullptr, nullptr, wc.hInstance, nullptr);
    Check(window != nullptr, "create one hidden test HWND"); ResizeWindow(window, 640, 360);
    for (const auto format : {DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM})
    {
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> immediate;
        Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &immediate), "create original D3D11 device");
        ComPtr<IDXGIFactory2> factory; Hr(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "create test factory");
        DXGI_SWAP_CHAIN_DESC1 d{}; d.Width = 640; d.Height = 360; d.Format = format; d.SampleDesc.Count = 1;
        d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d.BufferCount = 2; d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        d.Scaling = DXGI_SCALING_STRETCH; d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> source; Hr(factory->CreateSwapChainForHwnd(device.Get(), window, &d, nullptr, nullptr, &source), "create original HWND chain");
        CompositionPresenter presenter;
        presenter.Observe(source.Get(), 0, 0);
        Check(presenter.Status().format == static_cast<UINT>(format) && presenter.Status().sourceValid == 1 && !presenter.Exists(), "passive details without presenter creation");
        Check(FAILED(presenter.Prepare(device.Get(), source.Get(), 0, DXGI_PRESENT_TEST)) && !presenter.Exists(), "special flags rejected before creating composition chain");
        UINT before = 0; Hr(source->GetLastPresentCount(&before), "original initial count");
        for (uint8_t phase = 1; phase <= 3; ++phase) Pattern(presenter, immediate.Get(), device.Get(), source.Get(), 640, 360, phase);
        UINT after = 0; Hr(source->GetLastPresentCount(&after), "original suppressed count"); Check(after == before, "original chain receives zero calls during composition");
        Check(presenter.Status().singleCallSamples == 3 && presenter.Status().countMismatches == 0, "three composition single-call samples");
        presenter.FailNextDetach();
        Check(!presenter.Stop() && presenter.Exists() && presenter.Status().attached == 1 &&
            presenter.Status().reason == RbaCompositionReason_DetachFailed, "failed detach preserves attached state and resources for retry");
        Check(presenter.Stop() && !presenter.Exists() && presenter.Status().attached == 0 && presenter.Status().draining == 0, "detach and retire composition resources");
        Hr(source->Present(0, 0), "restore original Present");
        Hr(source->GetLastPresentCount(&after), "original restored count"); Check(after == before + 1, "one original call after detach");
        Pattern(presenter, immediate.Get(), device.Get(), source.Get(), 640, 360, 7);
        ResizeWindow(window, 672, 384);
        Hr(source->ResizeBuffers(2, 672, 384, format, 0), "Unity-style resize succeeds while no original buffers are retained");
        Check(FAILED(presenter.Prepare(device.Get(), source.Get(), 0, 0)), "changed source stops active composition");
        Check(presenter.Stop() && presenter.Status().attached == 0, "changed source detaches before restoration");
        Pattern(presenter, immediate.Get(), device.Get(), source.Get(), 672, 384, 9);
        Check(presenter.Stop() && presenter.Stop(), "recreated composition can stop idempotently");
        source.Reset(); immediate->ClearState(); immediate->Flush(); immediate.Reset(); device.Reset(); factory.Reset();
        ResizeWindow(window, 640, 360);
    }
    // The exact player's source chain has a latency waitable object. Determine
    // whether a normal per-frame wait consumes readiness while original
    // Presents are suppressed. The production gate rejects this flag until a
    // supported way to preserve Unity's pacing contract is established.
    {
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> immediate;
        Hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &immediate), "create waitable-source device");
        ComPtr<IDXGIFactory2> factory; Hr(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "create waitable-source factory");
        DXGI_SWAP_CHAIN_DESC1 d{}; d.Width = 640; d.Height = 360; d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        d.SampleDesc.Count = 1; d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d.BufferCount = 2;
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; d.Scaling = DXGI_SCALING_STRETCH;
        d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        d.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT | DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
        ComPtr<IDXGISwapChain1> source; Hr(factory->CreateSwapChainForHwnd(device.Get(), window, &d, nullptr, nullptr, &source), "create exact-player-flag source chain");
        ComPtr<IDXGISwapChain2> waitable; Hr(source.As(&waitable), "query latency source");
        Hr(waitable->SetMaximumFrameLatency(1), "set bounded source latency");
        const HANDLE ready = waitable->GetFrameLatencyWaitableObject(); Check(ready != nullptr, "obtain source latency handle");
        const DWORD first = WaitForSingleObject(ready, 500);
        const DWORD withoutPresent = WaitForSingleObject(ready, 100);
        Hr(source->Present(0, 0), "one ordinary source Present after suppression wait");
        const DWORD restored = WaitForSingleObject(ready, 500);
        std::printf("Waitable-source flags=0x842: firstWait=%lu, nextWaitWithoutPresent=%lu, afterSourcePresent=%lu (0=ready,258=timeout).\n",
            first, withoutPresent, restored);
        Check(first == WAIT_OBJECT_0 && restored == WAIT_OBJECT_0, "normal source Present restores latency readiness");
        Check(withoutPresent == WAIT_TIMEOUT || withoutPresent == WAIT_OBJECT_0, "suppression wait completes within diagnostic bound");
        CompositionPresenter presenter;
        Check(FAILED(presenter.Prepare(device.Get(), source.Get(), 1, 0)) && !presenter.Exists(), "unproven original waitable pacing remains gated");
        CloseHandle(ready); waitable.Reset(); source.Reset(); immediate->ClearState(); immediate->Flush();
    }
    DestroyWindow(window); UnregisterClassW(wc.lpszClassName, wc.hInstance);
    std::printf("Passed %d composition GPU checks (hidden standalone HWND, RGBA8/BGRA8, no Unity or FG claim).\n", checks);
    return 0;
}
