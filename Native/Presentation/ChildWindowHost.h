#pragma once
#include <Windows.h>
#include <cstdint>

enum class ChildWindowReason : uint32_t
{
    Disabled, WaitingForFreshImage, Ready, Draining, WrongThread,
    InvalidParent, ParentClaimChanged, UnsupportedDpi, UnsupportedExclusive,
    InvalidExtent, GeometryChanged, ParentHidden, WindowFailure,
    WindowDestroyed, RearmRequired, InvalidFrameSequence, BackpressureExpired
};

struct ChildWindowConfig
{
    HWND parent = nullptr;
    uint32_t physicalWidth = 0, physicalHeight = 0;
    uint64_t realFrameId = 0;
    bool enabled = false;
    bool windowed = true; // The original DXGI description, not a window-style guess.
    // Actual render EOF frontier. Zero uses realFrameId for synchronous hosts.
    // Main may be ahead by at most the coordinator's eight queued EOF packets.
    uint64_t renderFrameId = 0;
};

struct ChildWindowTarget
{
    HWND window = nullptr;
    uint64_t epoch = 0, leaseId = 0;
    uint32_t width = 0, height = 0;
};

struct ChildWindowStatus
{
    HWND parent = nullptr, window = nullptr;
    uint64_t epoch = 0, readyRealFrameId = 0, readyObservedMilliseconds = 0;
    uint64_t softHoldStartedFrame = 0;
    uint64_t creations = 0, destructions = 0, shows = 0, hides = 0;
    uint32_t ownerThreadId = 0, width = 0, height = 0, dpi = 0;
    ChildWindowReason reason = ChildWindowReason::Disabled;
    DWORD lastError = ERROR_SUCCESS;
    bool acceptingPresents = false, visible = false;
    bool leaseRetained = false, presenting = false, rearmRequired = false;
    bool softBackpressure = false, backpressureExpired = false;
};

enum class ChildImageDisposition { Unavailable, Fresh, SoftHold, Expired };
// Pure policy shared by the actual owner tick and CPU lifecycle fixtures.
ChildImageDisposition EvaluateChildImage(const ChildWindowConfig& config,
    const ChildWindowStatus& state, uint64_t now);

// A Win32 host only: no provider, DXGI chain, GPU wait, focus change or input
// forwarding. Keep this object alive through ReleaseRenderLease and a final
// disabled Tick. Its creating/window-owner thread must continue pumping messages.
class ChildWindowHost
{
public:
    ChildWindowHost();
    ~ChildWindowHost();
    ChildWindowHost(const ChildWindowHost&) = delete;
    ChildWindowHost& operator=(const ChildWindowHost&) = delete;

    // Owner thread only. No waits for the renderer. Geometry/disable invalidates
    // new presents and hides immediately; destruction waits for lease release.
    bool Tick(const ChildWindowConfig& config);
    ChildWindowStatus Status() const;

    // One provider-context lease at a time. The HWND cannot be destroyed by this
    // host until release. Parent/OS destruction can still invalidate the target.
    bool AcquireRenderLease(ChildWindowTarget& target);
    bool GetStableTarget(const ChildWindowTarget& lease, ChildWindowTarget& target) const;
    bool TryBeginPresent(const ChildWindowTarget& lease);
    bool EndPresent(const ChildWindowTarget& lease);
    // Call after completing the fresh image, outside a Present call. Tick shows
    // only a completed stamp no more than two render frames and 250ms old, with
    // bounded main/render queue lag. Repeated observation cannot refresh time or
    // restore a cleared stamp. This is not an acknowledgment of GPU retirement.
    bool MarkFrameReady(const ChildWindowTarget& lease, uint64_t realFrameId);
    // A dropped/invalid real frame removes the image's eligibility for visibility
    // at the next owner Tick, without claiming GPU retirement or closing the lease.
    bool ClearFrameReady(const ChildWindowTarget& lease);
    // Preserve only an already visible completed image during eligible soft
    // backpressure. Never refresh its completion time or revive a hidden image.
    bool HoldFrameReady(const ChildWindowTarget& lease);
    // Caller attests all provider and queue users of the HWND have retired.
    // EndPresent alone is insufficient. Rejected while a Present is in flight.
    bool ReleaseRenderLease(const ChildWindowTarget& lease);

    static constexpr const wchar_t* ParentClaimPropertyName()
    { return L"ReduxBetterAA.ChildWindowHost.6DD4AA41-5F1A-4CF6-8EE6-193ED2F1C6A3"; }
private:
    struct Impl;
    Impl* impl_ = nullptr;
};
