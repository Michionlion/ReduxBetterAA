#pragma once
#include "PresentationProbe.h"
#include <d3d11.h>
#include <dxgi.h>
#ifdef RBA_COMPOSITION_TESTING
#include <vector>
#endif

// Render-thread-only implementation; exported status is a separately locked copy.
class CompositionPresenter
{
public:
    CompositionPresenter();
    ~CompositionPresenter();
    CompositionPresenter(const CompositionPresenter&) = delete;
    CompositionPresenter& operator=(const CompositionPresenter&) = delete;
    void Observe(IDXGISwapChain* source, UINT interval, UINT flags);
    HRESULT Prepare(ID3D11Device* device, IDXGISwapChain* source, UINT interval, UINT flags, bool sourceMaintenance = false);
    HRESULT Present(UINT interval);
    // Detaches first, then proves GPU retirement. A pending context is retained
    // and retried; its resources are never freed just because a wait timed out.
    bool Stop();
    bool Exists() const;
    const RbaCompositionStatus& Status() const { return status_; }
#ifdef RBA_COMPOSITION_TESTING
    HRESULT ReadPrepared(std::vector<uint8_t>& bytes);
    void FailNextDetach();
#endif
private:
    struct Impl;
    Impl* impl_ = nullptr;
    RbaCompositionStatus status_{};
};
