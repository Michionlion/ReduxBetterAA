#include "ChildWindowHost.h"
#include <cstdio>
#include <cstdlib>
#include <future>
#include <thread>

namespace
{
    int checks = 0;
    void Check(bool value, const char* text)
    { ++checks; if (!value) { std::fprintf(stderr, "FAIL: %s\n", text); std::exit(1); } }
    const wchar_t* parentClass = L"RbaChildHostOwnedTestParent";
    HWND Parent(uint32_t width = 640, uint32_t height = 360)
    {
        RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
        Check(AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE) != FALSE, "parent test extent");
        HWND result = CreateWindowExW(WS_EX_NOACTIVATE, parentClass, L"ReduxBetterAA Win32 host lifecycle test",
            WS_OVERLAPPEDWINDOW, 30, 30, rect.right - rect.left, rect.bottom - rect.top,
            nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        Check(result != nullptr, "create owned parent");
        Check(SetWindowPos(result, nullptr, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW) != FALSE,
            "show own parent without activation");
        return result;
    }
    void Resize(HWND parent, uint32_t width, uint32_t height)
    {
        RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
        Check(AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE) != FALSE, "resize extent");
        Check(SetWindowPos(parent, nullptr, 0, 0, rect.right - rect.left, rect.bottom - rect.top,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOMOVE) != FALSE, "resize owned parent");
    }
    ChildWindowConfig Config(HWND parent, uint64_t frame = 1)
    { return {parent, 640, 360, frame, true, true}; }
    ChildWindowHost* releaseOnShow = nullptr;
    ChildWindowTarget releaseTarget{};
    WNDPROC originalChildProc = nullptr;
    bool releasedInsideShow = false;
    LRESULT CALLBACK ReentrantChildProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_WINDOWPOSCHANGING && releaseOnShow != nullptr &&
            (reinterpret_cast<WINDOWPOS*>(lParam)->flags & SWP_SHOWWINDOW) != 0)
        {
            releasedInsideShow = releaseOnShow->ReleaseRenderLease(releaseTarget);
            releaseOnShow = nullptr;
        }
        return CallWindowProcW(originalChildProc, window, message, wParam, lParam);
    }
}

int main()
{
    const auto oldDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    Check(oldDpi != nullptr, "test thread DPI context");
    WNDCLASSW wc{}; wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = parentClass;
    Check(RegisterClassW(&wc) != 0, "test parent class");
    HWND parent = Parent(); const auto style = GetWindowLongPtrW(parent, GWL_STYLE);
    const auto extended = GetWindowLongPtrW(parent, GWL_EXSTYLE); const auto focus = GetFocus();
    ChildWindowHost host; auto config = Config(parent);
    Check(host.Tick(config), "prepare host on parent owner thread");
    auto state = host.Status(); HWND first = state.window;
    Check(first != nullptr && GetParent(first) == parent, "child belongs to exact parent");
    Check((GetWindowLongPtrW(first, GWL_STYLE) & (WS_CHILD | WS_DISABLED)) == (WS_CHILD | WS_DISABLED), "child is disabled for native parent input routing");
    Check(!IsWindowVisible(first) && !state.visible, "initially hidden before fresh image");
    Check(state.ownerThreadId == GetCurrentThreadId() && state.width == 640 && state.height == 360, "owner thread and physical extent");
    Check(GetWindowLongPtrW(parent, GWL_STYLE) == style && GetWindowLongPtrW(parent, GWL_EXSTYLE) == extended && GetFocus() == focus,
        "parent styles and keyboard focus preserved");
    ChildWindowTarget lease{}, duplicate{}, target{};
    Check(host.AcquireRenderLease(lease), "single provider lease");
    Check(!host.AcquireRenderLease(duplicate), "second provider cannot acquire target");
    Check(host.GetStableTarget(lease, target) && target.window == first, "stable target under lease");
    Check(host.TryBeginPresent(lease), "first present permit");
    Check(!host.TryBeginPresent(lease) && !host.ReleaseRenderLease(lease), "present cannot overlap or retire while active");
    Check(!host.MarkFrameReady(lease, 1), "no premature ready stamp during present");
    bool wrongEnd = true; std::thread endOther([&] { wrongEnd = host.EndPresent(lease); }); endOther.join();
    Check(!wrongEnd && host.Status().presenting, "only presenting thread ends its call");
    Check(host.EndPresent(lease), "end present");
    Check(host.MarkFrameReady(lease, 1), "completed fresh image stamp");
    Check(host.Tick(config) && IsWindowVisible(first), "show only after fresh image");
    config.realFrameId = 5;
    Check(host.Tick(config) && !IsWindowVisible(first), "slow initialization or stalled image becomes hidden");
    Check(host.MarkFrameReady(lease, 5), "new completed image after stale interval");
    Check(!host.MarkFrameReady(lease, 4), "reject image stamp rollback");
    Check(host.Tick(config) && IsWindowVisible(first), "fresh image restores visibility");
    Check(host.ClearFrameReady(lease) && host.Tick(config) && !IsWindowVisible(first), "dropped frame hides on next owner tick");
    Check(host.Status().leaseRetained && host.Status().acceptingPresents, "dropped frame does not falsely retire provider");
    Check(!host.MarkFrameReady(lease, 5) && host.Tick(config) && !IsWindowVisible(first), "old provider poll cannot resurrect a dropped image");
    config.realFrameId = 6;
    Check(host.MarkFrameReady(lease, 6) && host.Tick(config) && IsWindowVisible(first), "new completed frame restores same provider lease");
    Check(host.ReleaseRenderLease(lease), "provider retirement acknowledgment");
    Check(!host.AcquireRenderLease(duplicate) && !host.TryBeginPresent(lease), "released context cannot accept another present or inherit stamp");
    Check(host.Tick(config), "owner retires and recreates after lease release");
    state = host.Status();
    Check(state.creations == 2 && state.destructions == 1 && !state.visible && state.readyRealFrameId == 0, "new context starts hidden without predecessor stamp");
    Check(host.AcquireRenderLease(lease), "acquire recreated target");
    Check(host.MarkFrameReady(lease, 6) && host.Tick(config), "show recreated target");

    // UI disable must not wait for a renderer whose Present is in progress.
    std::promise<void> begun, finish; auto finished = finish.get_future();
    bool beginResult = false, endResult = false;
    std::thread render([&] { beginResult = host.TryBeginPresent(lease); begun.set_value(); finished.wait(); endResult = host.EndPresent(lease); });
    begun.get_future().wait(); Check(beginResult, "worker present starts");
    config.enabled = false; const auto beforeDisable = GetTickCount64();
    Check(host.Tick(config) && GetTickCount64() - beforeDisable < 1000, "UI disable does not wait for renderer");
    state = host.Status();
    Check(!state.acceptingPresents && !state.visible && state.presenting && state.leaseRetained && IsWindow(state.window), "disable hides but retains leased HWND");
    Check(!host.TryBeginPresent(lease) && !host.GetStableTarget(lease, target), "closed epoch prevents all new presents");
    Check(!host.ReleaseRenderLease(lease), "in-flight call blocks retirement");
    finish.set_value(); render.join(); Check(endResult, "in-flight call can finish after epoch invalidation");
    Check(host.Status().leaseRetained, "end present alone does not claim GPU retirement");
    Check(host.ReleaseRenderLease(lease) && host.Tick(config), "retirement lets owner destroy");
    Check(host.Status().window == nullptr && !host.Status().leaseRetained && GetPropW(parent, ChildWindowHost::ParentClaimPropertyName()) == nullptr,
        "complete disable removes only own parent claim");

    config = Config(parent, 8); Check(host.Tick(config) && host.AcquireRenderLease(lease), "prepare reentrant show test");
    Check(host.MarkFrameReady(lease, 8), "reentrant test fresh image");
    HWND reentrantWindow = lease.window;
    originalChildProc = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(reentrantWindow, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&ReentrantChildProc)));
    Check(originalChildProc != nullptr, "test subclasses only its owned child");
    releaseOnShow = &host; releaseTarget = lease;
    Check(host.Tick(config) && releasedInsideShow, "retirement occurs inside Win32 show dispatch");
    Check(!host.Status().visible && !host.Status().acceptingPresents && !IsWindowVisible(reentrantWindow),
        "show revalidation immediately hides retired provider image");
    Check(GetWindowLongPtrW(reentrantWindow, GWLP_WNDPROC) == reinterpret_cast<LONG_PTR>(&ReentrantChildProc), "test still owns child subclass");
    SetWindowLongPtrW(reentrantWindow, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(originalChildProc));
    config.enabled = false; Check(host.Tick(config), "retire reentrant test target");

    config = Config(parent, 10); Check(host.Tick(config) && host.AcquireRenderLease(lease), "re-enable after complete disable");
    Check(host.MarkFrameReady(lease, 10) && host.Tick(config), "show before resize");
    Resize(parent, 800, 450); config.physicalWidth = 800; config.physicalHeight = 450; config.realFrameId = 11;
    Check(host.Tick(config), "resize closes previous target");
    Check(!host.Status().acceptingPresents && !host.Status().visible && host.Status().leaseRetained, "resize hides and retains old lease");
    Check(!host.TryBeginPresent(lease) && host.ReleaseRenderLease(lease), "old size retires explicitly");
    Check(host.Tick(config) && host.AcquireRenderLease(lease), "new size created after retirement");
    Check(lease.width == 800 && lease.height == 450 && !host.Status().visible, "resized image requires a new stamp");
    Check(host.ReleaseRenderLease(lease), "release resized context"); config.enabled = false; Check(host.Tick(config), "disable resized host");

    Resize(parent, 640, 360); config = Config(parent, 20);
    Check(host.Tick(config) && host.AcquireRenderLease(lease), "prepare owner-thread error case");
    bool wrongTick = true; std::thread tickOther([&] { wrongTick = host.Tick(config); }); tickOther.join();
    Check(!wrongTick && !host.Status().acceptingPresents && host.Status().reason == ChildWindowReason::WrongThread, "wrong UI thread fails closed without Win32 mutation");
    Check(host.ReleaseRenderLease(lease), "wrong-thread closure still permits retirement"); config.enabled = false; Check(host.Tick(config), "owner cleans wrong-thread request");

    config = Config(parent, 30); config.windowed = false;
    Check(!host.Tick(config) && host.Status().reason == ChildWindowReason::UnsupportedExclusive, "DXGI exclusive state is gated");
    config.windowed = true; config.physicalWidth = 639;
    Check(!host.Tick(config) && host.Status().reason == ChildWindowReason::InvalidExtent, "logical versus physical extent mismatch is gated");
    config.physicalWidth = 640;
    const auto prior = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_UNAWARE);
    Check(!host.Tick(config) && host.Status().reason == ChildWindowReason::UnsupportedDpi, "unexpected DPI context is gated without changing it");
    Check(GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == DPI_AWARENESS_UNAWARE, "host preserves caller DPI context");
    SetThreadDpiAwarenessContext(prior);

    Check(host.Tick(config) && host.AcquireRenderLease(lease), "prepare conditional claim cleanup");
    HANDLE foreignClaim = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(7));
    Check(SetPropW(parent, ChildWindowHost::ParentClaimPropertyName(), foreignClaim) != FALSE, "test external claim replacement");
    Check(!host.Tick(config) && host.Status().rearmRequired, "changed parent identity requires rearm");
    Check(host.ReleaseRenderLease(lease), "release after claim loss"); config.enabled = false; Check(host.Tick(config), "cleanup after claim loss");
    Check(GetPropW(parent, ChildWindowHost::ParentClaimPropertyName()) == foreignClaim, "cleanup preserves foreign replacement claim");
    Check(RemovePropW(parent, ChildWindowHost::ParentClaimPropertyName()) == foreignClaim, "test removes its own claim");

    config = Config(parent, 40); Check(host.Tick(config) && host.AcquireRenderLease(lease), "prepare parent destruction");
    Check(host.TryBeginPresent(lease), "permit before parent destruction");
    Check(DestroyWindow(parent) != FALSE, "OS destroys parent and child");
    state = host.Status();
    Check(state.window == nullptr && state.leaseRetained && state.presenting && state.rearmRequired && !state.acceptingPresents,
        "NCDESTROY invalidates target while retaining provider retirement obligation");
    Check(!host.GetStableTarget(lease, target) && !host.TryBeginPresent(lease), "destroyed or reused HWND cannot acquire new permits");
    Check(host.EndPresent(lease) && host.ReleaseRenderLease(lease), "destroyed HWND does not prevent explicit retirement acknowledgment");
    parent = Parent(); config = Config(parent, 41);
    Check(!host.Tick(config), "new or reused parent requires disabled transition after destruction");
    config.enabled = false; Check(host.Tick(config), "acknowledge complete destroyed-parent shutdown");
    config.enabled = true; Check(host.Tick(config), "recreate on valid parent after explicit rearm");
    Check(host.AcquireRenderLease(lease) && host.ReleaseRenderLease(lease), "new parent target retires");
    config.enabled = false; Check(host.Tick(config) && host.Status().window == nullptr, "final child destruction");
    Check(DestroyWindow(parent) != FALSE, "test cleans its lifecycle parent");

    parent = Parent(); config = Config(parent, 103); config.renderFrameId = 102;
    Check(host.Tick(config) && host.AcquireRenderLease(lease), "prepare queued main/render timeline test");
    Check(host.MarkFrameReady(lease, 100) && host.Tick(config) && host.Status().visible,
        "completed AMD image three main frames old remains fresh two render frames behind actual EOF");
    const auto observedAt = host.Status().readyObservedMilliseconds;
    Check(host.TryBeginPresent(lease) && host.Tick(config) && host.Status().visible,
        "next asynchronous Present retains prior completed fresh image");
    Check(host.EndPresent(lease), "next asynchronous Present ends without a new completed image stamp");
    Sleep(280);
    Check(host.MarkFrameReady(lease, 100) && host.Status().readyObservedMilliseconds == observedAt,
        "repeated retirement poll cannot refresh completion observation time");
    Check(host.Tick(config) && !host.Status().visible, "stalled render frontier still hides after250ms wall-clock ceiling");
    config.realFrameId = 104; config.renderFrameId = 103;
    Check(host.MarkFrameReady(lease, 101) && host.Tick(config) && host.Status().visible, "new GPU-completed image refreshes the wall clock");
    Check(host.ClearFrameReady(lease) && !host.MarkFrameReady(lease, 101) && host.Tick(config) && !host.Status().visible,
        "same retired frame cannot restore a cleared image despite a fresh timestamp");
    Check(host.MarkFrameReady(lease, 102) && host.Tick(config) && host.Status().visible, "next completed frame restores visibility after a drop");
    config.renderFrameId = 105;
    Check(host.Tick(config) && !host.Status().visible, "future native frontier cannot certify image freshness");
    config.realFrameId = 105; config.renderFrameId = 104;
    Check(host.Tick(config) && host.Status().visible, "valid observed render frontier recovers without changing the completed image");
    config.realFrameId = 114;
    Check(host.Tick(config) && !host.Status().visible, "main/render lag beyond bounded EOF queue capacity hides image");
    config.realFrameId = 115; config.renderFrameId = 113;
    Check(host.MarkFrameReady(lease, 110) && host.Tick(config) && !host.Status().visible, "three-render-frame-old image remains rejected");
    Check(host.MarkFrameReady(lease, 111) && host.Tick(config) && host.Status().visible, "two-render-frame completed image meets bounded dual timeline contract");
    Check(host.ReleaseRenderLease(lease), "freshness test retains normal provider retirement handshake");
    config.enabled = false; Check(host.Tick(config) && host.Status().window == nullptr, "freshness test child retires completely");
    config = Config(parent, 200);
    Check(host.Tick(config) && host.AcquireRenderLease(lease), "prepare soft-backpressure host");
    Check(!host.HoldFrameReady(lease), "soft hold cannot reveal an unfilled hidden child");
    Check(host.MarkFrameReady(lease, 200) && host.Tick(config) && IsWindowVisible(lease.window), "show first completed image before soft hold");
    const auto softHides = host.Status().hides;
    Check(host.HoldFrameReady(lease), "eligible soft drop preserves existing completed image");
    config.realFrameId = 201;
    Check(host.Tick(config) && IsWindowVisible(lease.window), "one busy real frame never reveals the parent");
    Check(host.Status().softBackpressure && host.Status().softHoldStartedFrame == 200,
        "owner Tick cannot erase a hold before actual new completion despite a temporarily fresh frontier");
    config.realFrameId = 202;
    Check(host.Tick(config) && IsWindowVisible(lease.window), "two busy real frames never reveal the parent");
    config.realFrameId = 207;
    Check(host.HoldFrameReady(lease), "a later soft busy frame renews only hold permission, not image age");
    Check(host.Tick(config) && IsWindowVisible(lease.window) && host.Status().hides == softHides,
        "soft backpressure avoids a hide/show cycle when real-frame age exceeds two");
    Check(host.MarkFrameReady(lease, 202) && host.Tick(config) && IsWindowVisible(lease.window) &&
        host.Status().softBackpressure && host.Status().hides == softHides,
        "new completed progress still behind the frontier preserves the existing visible soft hold");
    Check(host.MarkFrameReady(lease, 207) && host.Tick(config) && IsWindowVisible(lease.window) && !host.Status().softBackpressure,
        "only newer completed output catching up to the frontier ends the soft hold");
    Check(host.HoldFrameReady(lease) && host.Status().softHoldStartedFrame == 207,
        "a later busy batch starts a hold anchored to its own completed image");
    const auto softCompletedAt = host.Status().readyObservedMilliseconds;
    Check(host.TryBeginPresent(lease), "pending SDK work retains the existing render permit");
    Sleep(280);
    Check(host.EndPresent(lease) && host.MarkFrameReady(lease, 207) && host.Status().readyObservedMilliseconds == softCompletedAt,
        "same completed batch after a long pending call cannot refresh the deadline");
    Check(host.Tick(config) && !IsWindowVisible(lease.window) && host.Status().backpressureExpired &&
        host.Status().leaseRetained && !host.Status().acceptingPresents,
        "deadline hides once and closes new work without claiming the pending context retired");
    const auto expiredHides = host.Status().hides;
    Check(!host.MarkFrameReady(lease, 207) && !host.HoldFrameReady(lease) && host.Tick(config) &&
        !IsWindowVisible(lease.window) && host.Status().hides == expiredHides,
        "late completion cannot resurrect the expired child or cause repeated visibility transitions");
    Check(host.ReleaseRenderLease(lease), "expired child still requires explicit provider retirement");
    config.enabled = false; Check(host.Tick(config) && host.Status().window == nullptr, "owner destroys expired child after retirement");
    config.enabled = true; config.realFrameId = 210;
    Check(host.Tick(config) && host.AcquireRenderLease(lease) && !host.Status().visible && !host.Status().backpressureExpired,
        "a new host lease resets only the old image eligibility after explicit rearm");
    Check(host.MarkFrameReady(lease, 210) && host.Tick(config) && host.HoldFrameReady(lease), "new epoch can show and soft hold its own completed image");
    Check(host.ClearFrameReady(lease) && !host.MarkFrameReady(lease, 210) && !host.HoldFrameReady(lease),
        "hard image invalidation rejects the previous completed image");
    config.realFrameId = 212;
    Check(host.Tick(config) && !host.Status().visible && host.MarkFrameReady(lease, 212) && host.Tick(config) && host.Status().visible,
        "only a completed post-reset image can resume the child");
    Check(host.HoldFrameReady(lease), "prepare soft hold before hard Off");
    config.enabled = false;
    Check(host.Tick(config) && !host.Status().visible && host.Status().leaseRetained,
        "hard Off or scene gate hides immediately despite an unexpired soft hold");
    Check(host.ReleaseRenderLease(lease) && host.Tick(config), "hard Off still retires the provider lease normally");
    Check(DestroyWindow(parent) != FALSE && UnregisterClassW(parentClass, wc.hInstance) != FALSE, "test owns and cleans its final parent class");
    SetThreadDpiAwarenessContext(oldDpi);
    std::printf("Passed %d Win32 child-host checks; no graphics, provider, synthetic input or Unity launch.\n", checks);
    return 0;
}
