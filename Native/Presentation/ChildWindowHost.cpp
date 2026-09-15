#include "ChildWindowHost.h"
#include <atomic>
#include <cwchar>
#include <new>

namespace
{
    struct Guard
    {
        SRWLOCK& lock;
        explicit Guard(SRWLOCK& value) : lock(value) { AcquireSRWLockExclusive(&lock); }
        ~Guard() { ReleaseSRWLockExclusive(&lock); }
    };
    std::atomic<uint64_t> nextClass{0};
}

struct ChildWindowHost::Impl
{
    mutable SRWLOCK lock = SRWLOCK_INIT;
    ChildWindowStatus state{};
    ChildWindowTarget lease{};
    uint64_t leaseSequence = 0, lastTickFrame = 0, lastCompletedFrame = 0;
    DWORD presentThread = 0;
    HINSTANCE module = nullptr;
    wchar_t className[96]{};
    bool classRegistered = false, claimedParent = false, destroying = false;

    bool Matches(const ChildWindowTarget& value) const
    {
        return state.leaseRetained && value.leaseId == lease.leaseId &&
            value.epoch == lease.epoch && value.window == lease.window &&
            value.width == lease.width && value.height == lease.height;
    }
    bool Current(const ChildWindowTarget& value) const
    { return Matches(value) && state.acceptingPresents && value.epoch == state.epoch && value.window == state.window; }
    bool Fresh(const ChildWindowConfig& config, uint64_t now) const
    {
        const auto decision = EvaluateChildImage(config, state, now);
        return decision == ChildImageDisposition::Fresh || decision == ChildImageDisposition::SoftHold;
    }
    void Invalidate(ChildWindowReason reason, DWORD error = ERROR_SUCCESS)
    {
        // Caller holds the state lock. Only the first invalidation changes epoch.
        if (state.acceptingPresents) ++state.epoch;
        state.acceptingPresents = false;
        state.readyRealFrameId = 0;
        state.readyObservedMilliseconds = 0;
        state.softBackpressure = false;
        state.softHoldStartedFrame = 0;
        state.reason = reason;
        state.lastError = error;
    }
    static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto* self = reinterpret_cast<Impl*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            self = static_cast<Impl*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            Guard guard(self->lock);
            self->state.window = window;
        }
        if (message == WM_NCDESTROY && self != nullptr)
        {
            {
                Guard guard(self->lock);
                if (self->state.window == window)
                {
                    if (!self->destroying)
                    {
                        self->Invalidate(ChildWindowReason::WindowDestroyed);
                        self->state.rearmRequired = true;
                    }
                    self->state.window = nullptr;
                    self->state.visible = false;
                    ++self->state.destructions;
                    // A lost HWND does not acknowledge provider/GPU retirement.
                }
            }
            if (GetWindowLongPtrW(window, GWLP_USERDATA) == reinterpret_cast<LONG_PTR>(self))
                SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        }
        // Disabled WS_CHILD input is routed to the parent by Windows. These
        // defensive replies never synthesize or forward an input message.
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint{};
            BeginPaint(window, &paint);
            EndPaint(window, &paint);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }
    bool Hide()
    {
        HWND window = nullptr;
        { Guard guard(lock); window = state.window; }
        if (window == nullptr) return true;
        // No state lock across Win32 APIs that may dispatch WindowProc.
        if (!SetWindowPos(window, nullptr, 0, 0, 0, 0,
            SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW))
        {
            const DWORD error = GetLastError();
            Guard guard(lock); state.reason = ChildWindowReason::WindowFailure; state.lastError = error;
            return false;
        }
        Guard guard(lock);
        if (state.visible) ++state.hides;
        state.visible = false;
        return true;
    }
    bool Cleanup()
    {
        HWND window = nullptr, parent = nullptr;
        {
            Guard guard(lock);
            if (state.leaseRetained || state.presenting)
            { state.reason = ChildWindowReason::Draining; return false; }
            window = state.window; parent = state.parent; destroying = true;
        }
        const BOOL destroyed = window == nullptr || DestroyWindow(window);
        const DWORD error = destroyed ? ERROR_SUCCESS : GetLastError();
        {
            Guard guard(lock); destroying = false;
            if (!destroyed) { state.reason = ChildWindowReason::WindowFailure; state.lastError = error; return false; }
        }
        // A foreign claim or a reused HWND must never be modified by cleanup.
        if (claimedParent && GetPropW(parent, ChildWindowHost::ParentClaimPropertyName()) == this)
            RemovePropW(parent, ChildWindowHost::ParentClaimPropertyName());
        claimedParent = false;
        if (classRegistered && !UnregisterClassW(className, module))
        {
            const DWORD classError = GetLastError();
            Guard guard(lock); state.reason = ChildWindowReason::WindowFailure; state.lastError = classError;
            return false;
        }
        classRegistered = false;
        Guard guard(lock); state.parent = nullptr; state.width = state.height = state.dpi = 0;
        return true;
    }
    bool Create(HWND parent, uint32_t width, uint32_t height, uint32_t dpi)
    {
        if (GetPropW(parent, ChildWindowHost::ParentClaimPropertyName()) != nullptr)
        {
            Guard guard(lock); state.reason = ChildWindowReason::ParentClaimChanged; return false;
        }
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&WindowProc), &module)) return false;
        const auto id = ++nextClass;
        swprintf_s(className, L"ReduxBetterAAChild_%p_%llu", static_cast<void*>(module), id);
        WNDCLASSW wc{}; wc.lpfnWndProc = WindowProc; wc.hInstance = module; wc.lpszClassName = className;
        if (!RegisterClassW(&wc)) return false;
        classRegistered = true;
        if (!SetPropW(parent, ChildWindowHost::ParentClaimPropertyName(), this)) return false;
        claimedParent = true;
        {
            Guard guard(lock); state.parent = parent; state.width = width; state.height = height; state.dpi = dpi;
        }
        HWND window = CreateWindowExW(WS_EX_NOPARENTNOTIFY, className, L"", WS_CHILD | WS_DISABLED,
            0, 0, static_cast<int>(width), static_cast<int>(height), parent, nullptr, module, this);
        if (window == nullptr) return false;
        Guard guard(lock);
        ++state.epoch; ++state.creations;
        state.window = window; state.acceptingPresents = true; state.visible = false;
        state.readyRealFrameId = 0; state.reason = ChildWindowReason::WaitingForFreshImage;
        state.lastError = ERROR_SUCCESS;
        return true;
    }
};

ChildWindowHost::ChildWindowHost() : impl_(new Impl) {}
ChildWindowHost::~ChildWindowHost()
{
    if (impl_ == nullptr) return;
    const auto status = Status();
    if (status.ownerThreadId == 0 || status.ownerThreadId == GetCurrentThreadId())
        Tick({});
    const auto after = Status();
    if (!after.leaseRetained && !after.presenting && after.window == nullptr && !impl_->classRegistered && !impl_->claimedParent)
        delete impl_;
    // A violated shutdown contract quarantines the small context. Never free a
    // live WindowProc context or destroy a provider's HWND from a wrong thread.
}

bool ChildWindowHost::Tick(const ChildWindowConfig& config)
{
    auto& self = *impl_;
    const DWORD thread = GetCurrentThreadId();
    {
        Guard guard(self.lock);
        if (self.state.ownerThreadId != 0 && self.state.ownerThreadId != thread)
        { self.Invalidate(ChildWindowReason::WrongThread); return false; }
    }
    auto close = [&](ChildWindowReason reason, bool rearm = false)
    {
        { Guard guard(self.lock); self.Invalidate(reason); self.state.rearmRequired |= rearm; }
        const bool hidden = self.Hide();
        const bool cleaned = self.Cleanup();
        return hidden && cleaned;
    };
    if (!config.enabled)
    {
        const bool complete = close(ChildWindowReason::Disabled);
        if (complete) { Guard guard(self.lock); self.state.rearmRequired = false; self.lastTickFrame = 0; }
        return true;
    }
    if (Status().rearmRequired) { close(ChildWindowReason::RearmRequired, true); return false; }
    DWORD process = 0;
    const DWORD parentThread = GetWindowThreadProcessId(config.parent, &process);
    if (config.parent == nullptr || !IsWindow(config.parent) || process != GetCurrentProcessId())
    { close(ChildWindowReason::InvalidParent, true); return false; }
    if (parentThread != thread) { close(ChildWindowReason::WrongThread); return false; }
    { Guard guard(self.lock); self.state.ownerThreadId = thread; }
    if (!config.windowed) { close(ChildWindowReason::UnsupportedExclusive); return false; }
    if (config.realFrameId == 0 || config.realFrameId < self.lastTickFrame)
    { close(ChildWindowReason::InvalidFrameSequence, true); return false; }
    self.lastTickFrame = config.realFrameId;
    if (config.physicalWidth == 0 || config.physicalHeight == 0 || config.physicalWidth > 16384 || config.physicalHeight > 16384)
    { close(ChildWindowReason::InvalidExtent); return false; }
    if (!IsWindowVisible(config.parent) || IsIconic(config.parent))
    { close(ChildWindowReason::ParentHidden); return false; }
    const auto parentDpi = GetWindowDpiAwarenessContext(config.parent);
    const auto threadDpi = GetThreadDpiAwarenessContext();
    const UINT dpi = GetDpiForWindow(config.parent);
    if (dpi == 0 || !AreDpiAwarenessContextsEqual(parentDpi, threadDpi) ||
        GetAwarenessFromDpiAwarenessContext(parentDpi) == DPI_AWARENESS_UNAWARE)
    { close(ChildWindowReason::UnsupportedDpi); return false; }
    RECT client{};
    if (!GetClientRect(config.parent, &client) || client.left != 0 || client.top != 0 ||
        client.right != static_cast<LONG>(config.physicalWidth) || client.bottom != static_cast<LONG>(config.physicalHeight))
    { close(ChildWindowReason::InvalidExtent); return false; }
    auto status = Status();
    if (self.claimedParent && GetPropW(status.parent, ParentClaimPropertyName()) != &self)
    { close(ChildWindowReason::ParentClaimChanged, true); return false; }
    if (status.window != nullptr && (status.parent != config.parent || status.width != config.physicalWidth ||
        status.height != config.physicalHeight || status.dpi != dpi))
    {
        if (!close(ChildWindowReason::GeometryChanged)) return true;
        status = Status();
    }
    if (!status.acceptingPresents && (status.window != nullptr || status.leaseRetained || self.classRegistered || self.claimedParent))
    {
        if (!close(ChildWindowReason::Draining)) return true;
        status = Status();
    }
    if (status.window == nullptr)
    {
        if (GetPropW(config.parent, ParentClaimPropertyName()) != nullptr)
        { close(ChildWindowReason::ParentClaimChanged, true); return false; }
        if (!self.Create(config.parent, config.physicalWidth, config.physicalHeight, dpi))
        {
            const DWORD error = GetLastError(); close(ChildWindowReason::WindowFailure);
            Guard guard(self.lock); self.state.lastError = error; return false;
        }
    }
    status = Status();
    bool fresh = false;
    {
        Guard guard(self.lock);
        const auto decision = EvaluateChildImage(config, self.state, GetTickCount64());
        if (decision == ChildImageDisposition::Expired)
        {
            self.Invalidate(ChildWindowReason::BackpressureExpired);
            self.state.backpressureExpired = true;
        }
        else if (decision == ChildImageDisposition::Fresh && self.state.softBackpressure &&
            self.state.readyRealFrameId > self.state.softHoldStartedFrame)
        {
            // An owner tick can precede the next query's renewed hold. Only
            // genuinely newer completed output catching up ends this hold.
            self.state.softBackpressure = false; self.state.softHoldStartedFrame = 0;
        }
        fresh = decision == ChildImageDisposition::Fresh || decision == ChildImageDisposition::SoftHold;
    }
    if (!fresh)
    {
        if (!self.Hide()) return false;
        Guard guard(self.lock);
        if (self.state.acceptingPresents) self.state.reason = ChildWindowReason::WaitingForFreshImage;
        return true;
    }
    if (!status.visible)
    {
        if (!SetWindowPos(status.window, HWND_TOP, 0, 0, 0, 0,
            SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW))
        { const DWORD error = GetLastError(); close(ChildWindowReason::WindowFailure); Guard guard(self.lock); self.state.lastError = error; return false; }
        bool stillCurrent = false;
        {
            Guard guard(self.lock);
            stillCurrent = self.Fresh(config, GetTickCount64()) &&
                self.state.window == status.window && self.state.epoch == status.epoch &&
                self.state.readyRealFrameId >= status.readyRealFrameId;
            // SetWindowPos may dispatch messages and cannot run under our lock.
            // If retirement raced it, immediately hide again on this owner thread.
            self.state.visible = true; ++self.state.shows;
        }
        if (!stillCurrent) { self.Hide(); return true; }
    }
    bool current = false;
    {
        Guard guard(self.lock);
        current = self.Fresh(config, GetTickCount64()) &&
            self.state.window == status.window && self.state.epoch == status.epoch;
        if (current) self.state.reason = ChildWindowReason::Ready;
    }
    if (!current) self.Hide();
    return true;
}

ChildWindowStatus ChildWindowHost::Status() const
{ Guard guard(impl_->lock); return impl_->state; }
bool ChildWindowHost::AcquireRenderLease(ChildWindowTarget& target)
{
    auto& self = *impl_; Guard guard(self.lock); target = {};
    if (!self.state.acceptingPresents || self.state.window == nullptr || self.state.leaseRetained) return false;
    self.lease = {self.state.window, self.state.epoch, ++self.leaseSequence, self.state.width, self.state.height};
    self.state.leaseRetained = true; self.state.readyRealFrameId = 0;
    self.state.readyObservedMilliseconds = 0; self.state.softBackpressure = false; self.state.softHoldStartedFrame = 0;
    self.state.backpressureExpired = false;
    self.lastCompletedFrame = 0; target = self.lease; return true;
}
bool ChildWindowHost::GetStableTarget(const ChildWindowTarget& lease, ChildWindowTarget& target) const
{ Guard guard(impl_->lock); target = {}; if (!impl_->Current(lease)) return false; target = impl_->lease; return true; }
bool ChildWindowHost::TryBeginPresent(const ChildWindowTarget& lease)
{
    auto& self = *impl_; Guard guard(self.lock);
    if (!self.Current(lease) || self.state.presenting) return false;
    self.state.presenting = true; self.presentThread = GetCurrentThreadId(); return true;
}
bool ChildWindowHost::EndPresent(const ChildWindowTarget& lease)
{
    auto& self = *impl_; Guard guard(self.lock);
    if (!self.Matches(lease) || !self.state.presenting || self.presentThread != GetCurrentThreadId()) return false;
    self.state.presenting = false; self.presentThread = 0; return true;
}
bool ChildWindowHost::MarkFrameReady(const ChildWindowTarget& lease, uint64_t realFrameId)
{
    auto& self = *impl_; Guard guard(self.lock);
    if (!self.Current(lease) || self.state.presenting || realFrameId == 0 || realFrameId < self.lastCompletedFrame) return false;
    if (realFrameId == self.lastCompletedFrame) return self.state.readyRealFrameId == realFrameId;
    self.lastCompletedFrame = self.state.readyRealFrameId = realFrameId;
    self.state.readyObservedMilliseconds = GetTickCount64(); return true;
}
bool ChildWindowHost::ReleaseRenderLease(const ChildWindowTarget& lease)
{
    auto& self = *impl_; Guard guard(self.lock);
    if (!self.Matches(lease) || self.state.presenting) return false;
    self.Invalidate(ChildWindowReason::Draining);
    self.state.leaseRetained = false; self.lease = {}; return true;
}

bool ChildWindowHost::ClearFrameReady(const ChildWindowTarget& lease)
{
    auto& self = *impl_; Guard guard(self.lock);
    if (!self.Matches(lease)) return false;
    self.state.readyRealFrameId = 0; self.state.readyObservedMilliseconds = 0;
    self.state.softBackpressure = false; self.state.softHoldStartedFrame = 0; return true;
}

bool ChildWindowHost::HoldFrameReady(const ChildWindowTarget& lease)
{
    auto& self = *impl_; Guard guard(self.lock);
    if (!self.Current(lease) || !self.state.visible || self.state.readyRealFrameId == 0 ||
        self.state.backpressureExpired) return false;
    if (!self.state.softBackpressure) self.state.softHoldStartedFrame = self.state.readyRealFrameId;
    self.state.softBackpressure = true; return true;
}

ChildImageDisposition EvaluateChildImage(const ChildWindowConfig& config,
    const ChildWindowStatus& state, uint64_t now)
{
    const uint64_t rendered = config.renderFrameId != 0 ? config.renderFrameId : config.realFrameId;
    if (!config.enabled || !state.acceptingPresents || !state.leaseRetained || state.backpressureExpired ||
        state.readyRealFrameId == 0 || state.readyRealFrameId > rendered || rendered > config.realFrameId ||
        config.realFrameId - rendered > 8 || now < state.readyObservedMilliseconds)
        return ChildImageDisposition::Unavailable;
    if (now - state.readyObservedMilliseconds > 250)
        return state.softBackpressure && state.visible ? ChildImageDisposition::Expired : ChildImageDisposition::Unavailable;
    if (rendered - state.readyRealFrameId <= 2) return ChildImageDisposition::Fresh;
    return state.softBackpressure && state.visible ? ChildImageDisposition::SoftHold : ChildImageDisposition::Unavailable;
}
