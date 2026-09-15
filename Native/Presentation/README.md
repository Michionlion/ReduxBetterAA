# Unity D3D11 presentation and child host

This separately built, disabled-by-default native experiment tests the first
presentation prerequisite for frame generation. It generates **no frames** and
contains no vendor SDK, interpolation, generated-frame pacing or managed settings.
An additional explicit diagnostic mode copies the final D3D11 backbuffer to a
same-adapter D3D12 DirectComposition chain attached to Unity's existing HWND.
It is not included in the mod or runtime packages.

This directory also contains a separately enabled frame-generation coordinator,
described below. Its provider integration is distinct from the pass-through
probe and requires its own player validation.

The exact Unity 6000.5.8f1 headers expose `GetSwapChain`, `GetSyncInterval`,
`GetPresentFlags` and `kUnityRenderingExtQueryOverridePresentFrame`. They do **not**
document when or on which thread Unity calls that query, whether it caches the
answer, or a corresponding Present event. Treat this as a conditional experiment,
not a production integration proven by those interface declarations.

## Build and tests

Use the exact editor headers outside this repository; the CMake project verifies
their hashes. No Unity headers or binaries are copied into source. On Windows
x64 with Visual Studio 2022 Build Tools and CMake:

```powershell
cmake -S Native/Presentation -B G:/KSP2-ReleaseTesting/presentation/build -G 'Visual Studio 17 2022' -A x64 -DUNITY_PLUGIN_API='S:/Development/Unity/6000.5.8f1/Editor/Data/PluginAPI'
cmake --build G:/KSP2-ReleaseTesting/presentation/build --config Release
ctest --test-dir G:/KSP2-ReleaseTesting/presentation/build -C Release --output-on-failure
```

The DLLs and test executables build with `/W4 /WX`. Tests exercise the actual DLL
against mock Unity interfaces and a mock COM swapchain: passive startup, exact
Present arguments, duplicate/stale tickets, disable, failure HRESULT, missing
interfaces/swapchain, wrong-thread queries before/during/after Present,
concurrent ownership decisions, device reset/shutdown and balanced
COM ownership. They establish native control behavior, not player compatibility,
visible presentation, frame rate, latency or NVIDIA/AMD FG support.

`CompositionGpuTests.exe` is built separately and runs a hardware-only test on
one hidden standalone HWND. It checks exact RGBA8/BGRA8 pixel transport at two
extents, zero original-chain calls during composition, one composition call per
frame, detach failure/retry, restoration, resize and recreation. Run it only in
a coordinated GPU window. To include it in CTest, configure with
`-DRBA_PRESENT_GPU_TESTS=ON`. It does not create or launch a Unity player.
`SourceMaintenanceTests.exe` exercises the same probe implementation through
`RbaPresentationTestRuntime.dll`, a separate test-only build with a detach-failure
hook. It creates one visible standalone HWND and a real D3D11 source with the
player's `0x842` flags. It checks 120 original and 120 composition calls with
separate count deltas, continued original latency waits, duplicate suppression,
failed detach, mode changes, disable during acquisition, wrong-thread ownership
and failed original Present. It is also registered only with the GPU option.
The Unity-preloaded runtime DLL has no failure-injection export. Never stage the
test runtime or test executables in a player.

The 2026-09-13 fresh-source run passed 197 mock checks, 118 composition GPU
checks, and 1,234 maintained-source checks. These are bounded transport and
control tests; they do not establish generated-frame pacing or image quality.

## Reusable child-window host

[ChildWindowHost.h](ChildWindowHost.h) and its `RbaChildWindowHost` static library
provide a Win32 host for a future HWND-based presenter. It does not install a
Unity hook, create a swapchain or load a provider. `Tick(ChildWindowConfig)` runs
on the original window's owner thread, with the HWND and physical extent from
the native DXGI snapshot. It creates a borderless `WS_CHILD | WS_DISABLED` window
initially hidden, without changing parent styles, focus, DPI settings or input
registration. Windows routes disabled-child input to its parent. The separate
NVIDIA disabled-child control demonstrated 669 original source calls and 1,338
SDK presentations, with an actual click and wheel delivered to the parent; that
does not by itself validate this host's integration with Unity or a provider.
[Windows child input](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#messages),
[disabled-window behavior](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enablewindow).

The coordinator acquires one `ChildWindowTarget` lease for a provider context.
`TryBeginPresent` and `EndPresent` bracket each actual Present; invalidation
rejects new permits while allowing an already-started call to finish.
`MarkFrameReady` records a completed real-frame ID outside the Present call.
The owner-thread tick compares that image with the actual native render EOF
frontier, normally allowing at most two render frames of age. The main thread
may lead that frontier by at most the eight queued EOF packets. During eligible
soft backpressure, `HoldFrameReady` can retain an already visible completed
image beyond two render frames, but never beyond 250 ms from its actual
completion observation. It cannot show a hidden image or refresh the deadline.
The hold records its starting completed frame and ends only when a newer
completed frame catches up to the render frontier. Owner ticks of the same
image cannot erase a hold; a newer but still lagging completion preserves it.
Repeated polls of the same completed ID cannot extend its time or resurrect it
after a hard invalidation clears readiness. A show rechecks freshness and the lease/epoch after
Win32 message dispatch and hides again if retirement raced that operation.

Player05's AMD compatibility phase produced4467 real child calls and8934 native
presentations, but all95 retained-child telemetry samples were hidden by the
old main-frame age check: completed images were consistently three main frames
old and only one or two native render frames old. The dual timeline rule fixes
that observed eligibility mismatch; a subsequent player run still needs to
verify sustained child visibility and image quality.

Disable, resize and DPI/geometry changes hide the child and close its epoch.
The main thread never waits for the renderer. `EndPresent` is not GPU retirement:
only `ReleaseRenderLease`, after all provider and queue users have retired,
permits a later owner-thread tick to destroy/recreate the HWND. Release clears
image readiness; the next context cannot inherit an old image stamp. Parent
destruction invalidates the child through its `WM_NCDESTROY`, while retaining
the outstanding retirement obligation. An unexpected destruction or changed
parent claim requires a complete disabled tick before rearming. Cleanup removes
only a parent property still owned by this host, protecting a changed or reused
window identity. There is no parent subclass or input-message forwarding.

Unsupported exclusive presentation, a foreign window/process/thread, mismatched
native extent, unrecognized DPI context, and frame-ID rollback fail closed.
Keep the host alive and continue owner-thread ticks until the lease is released
and `Status().window == nullptr` before destroying its coordinator. Violating
shutdown quarantines a live WindowProc context instead of freeing it or destroying
another thread's window. No runtime failure-injection API exists for this host.
`ChildWindowHostTests` performs 139 real Win32 checks for lifecycle, stale-image
rejection, resize, owner-thread enforcement, parent destruction, conditional
claim cleanup, retirement during window-show dispatch, bounded soft holds and
non-resurrection after deadline expiry. These tests make no
graphics calls, create no player, change no parent focus and inject no input.

## Diagnostic integration

`GfxPluginReduxBetterAAPresentation.dll` exports the Unity plugin lifecycle,
`UnityRenderingExtQuery` and `UnityRenderingExtEvent` entry points. The Unity
header documents `GfxPlugin` as the preload naming convention. First test the
DLL in the native plugin location of a **disposable** exact-version player and
verify `status.loaded == 1`; copying it there or successfully resolving a P/Invoke
is not evidence that Unity discovered/preloaded it. Never manually spoof
`UnityPluginLoad` with a fabricated interface registry in a player.

The owned C ABI is in [PresentationProbe.h](PresentationProbe.h):

- `RbaPresent_GetStatus`: provide `structSize=312`, `abiVersion=2`, Pack=8. Safe
  from the main thread; returns a recent completed-state snapshot and atomic query
  telemetry. Fields can describe adjacent instants while rendering is in progress.
- `RbaPresent_GetRenderEventFunc`: a `UnityRenderingEventAndData` pointer. Once
  loaded, `status.renderEventId` is Unity's reserved event ID.
- `RbaPresent_Request(0, 0)`: disable. Enabling requires **all** diagnostic
  contract flags and is never automatic. Flags are assertions by the caller,
  not evidence or a runtime capability query.
- `RbaPresent_Observe(1, RbaPresentContract_Unity6000_5_8f1)`: opt in to read-only
  `GetLastPresentCount` sampling after verifying the exact player ABI. This never
  requests takeover. `RbaPresent_Observe(0, 0)` stops the observations.

Begin entirely passive. From an external diagnostic addon, add
`CommandBuffer.IssuePluginEventAndData` to a cached command buffer and execute it
with `Graphics.ExecuteCommandBuffer` once per real frame after `WaitForEndOfFrame`,
using the returned pointer/event ID and an unmanaged `RbaPresentFrame` (16 bytes,
`structSize=16`, `abiVersion=1`, strictly increasing nonzero `realFrameId`). Keep
the packet allocated until `status.finalFrameId` acknowledges its consumption.
The plugin copies its fields during the callback and retains no pointer. Device
reset clears the frame sequence and requires a new explicit enable request.

Observe `presentQueries`, `lastQueryThreadId`, `renderThreadId`,
`lastQueryFrameId`, `queriesWithoutFinalFrame` and `wrongThreadQueries`. Establish
that every real Present is preceded by exactly one final-frame ticket and that
the query happens on that same render thread at the actual Present seam. A
startup-only, cached or different-thread query rejects this design. Counters
alone do not prove the seam: compare ordering and actual presents with a
Present-aware trace in the exact player. Confirm there is no other Present owner.

The optional count observations provide an in-process check without ETW or
administrator access. `lastBeforeCount` samples the current chain before each
render-thread query; `lastAfterCount` samples it after an owned Present.
`ownedSingleCallSamples` counts continuous before/after intervals with exactly
one call, and `ownedCallMismatches` records other deltas. An increment between an
owned Present and the next query increases `unexplainedCallDeltas`, exposing an
extra Unity/other caller. Passive intervals report `passiveDeltaSamples`,
`passiveCallDeltas`, `lastBeforeDelta` and `lastDeltaFrameSpan`. Compare consecutive
real-frame tickets and raw counts; cumulative totals alone do not prove cadence.

Every device event, bad ticket, wrong-thread query, swapchain identity/description
change, count rollback/wrap, failed Present or failed count query breaks continuity.
`countEpoch`, `countResets` and `countResetReason` identify those boundaries;
`countValid=0` means no usable current baseline. A successful subsequent read
starts a new baseline and cannot bridge the old epoch. Sample counters remain
cumulative so failures and earlier mismatches stay visible. The observation
retains no swapchain references between samples and does not modify its state.
`GetLastPresentCount` counts API calls to Present/Present1, **not displayed FPS**;
it cannot establish scanout cadence, generated-frame quality or input latency.
[Microsoft count contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getlastpresentcount).

If that driver/runtime cannot supply reliable count samples, instrument the
actual Unity swapchain's Present and Present1 call paths in a separate diagnostic
player build. A proxy used only by our own code would miss Unity's calls. ETW/PresentMon
is another option, but an elevated trace is not intrinsically required for call
count verification. Physical presentation/latency measurements still need
appropriate display-aware evidence. `GetFrameStatistics` has presentation-mode
and multi-monitor limitations; it is not interchangeable with call counts.
[Microsoft frame statistics](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics).

Only after those checks can a harness attest `RbaPresentContract_All` and request
a short pass-through test. The query then obtains the current Unity swapchain,
calls `Present` **once** with Unity's unchanged sync interval/flags, releases its
temporary COM reference and returns true so Unity skips its own call. Repeated
queries on the same ticket return true without another Present, including an
unexpected-thread query during or after the attempted Present. Ownership is
claimed atomically: a ticket already yielded to Unity cannot later be taken by
the probe, even if another query or enable request arrives before the next
ticket. Wrong-thread queries disable future takeover. Failures also
return true for that attempted frame, then disable takeover for subsequent
frames. Missing resources return false without attempting a Present. There are
no cached backbuffers, intercepted vtables or asynchronous GPU resources in the
original D3D11 pass-through mode.

The frame-event pump must continue during disable, scene changes and minimization.
After requesting disable, publish at least one later final-frame ticket and
verify normal presentation has resumed before removing the pump. Stopping tickets
while their last frame is owned violates the experiment's contract; the query
has no frame identifier with which to distinguish a duplicate call from a new
frame. Do not expose this protocol as a player setting. Close the disposable
player before adding/removing its native DLL. Exercise resize, window transitions
and device recovery before treating the pass-through path as established.

Passing this experiment still does not satisfy Streamline's initialization and
swapchain-interception requirements, AMD's FG swapchain contract, an independent
UI layer, complete scene depth/motion, input leases or generated-frame pacing.
Those remain separate provider/presentation work described in the main
[architecture](../../docs/architecture.md).

## D3D12 composition route and provider blockers

`RbaPresent_RequestComposition(1, 15)` enables the separate strict copy mode
only after observation is enabled and the exact-player ownership contracts
have been established. A current valid count baseline and matching query/render
thread are required. It sets the legacy status `requested=2`; ordinary native
pass-through is `requested=1`. Strict composition suppresses all original-chain
calls and still rejects the player's `0x842` source flags.

`RbaPresent_RequestCompositionWithSourceMaintenance(1, 15)` is the explicit
alternative diagnostic, setting `requested=3`. The probe becomes the single
Present caller for **both** retained Unity source and opaque composition chains:
one original call per real ticket preserves Unity's latency wait progress, and
one composition call presents the copied final image. Unity skips its own call
for an owned ticket. This is an expanded ownership contract, not a claim that
there is only one chain call. No managed product setting or runtime package
loads or enables either experiment.

`RbaPresent_GetCompositionStatus` takes Pack=8, `structSize=200`, `abiVersion=1`.
Its layout is in the header. The render query caches source width/height, format,
swap effect, descriptor flags, windowed state, samples, buffer count, HWND and
sync/Present flags even while passive. Main-thread getters never invoke COM.
Inspect these before arming. Initial gates require a same-process HWND, matching
client/backbuffer extents, windowed/borderless SDR RGBA8/BGRA8 storage, one sample,
sync interval 0 or 1 and Present flags 0. Strict mode permits only the optional
ALLOW_MODE_SWITCH descriptor flag. Maintained-source mode additionally permits
FRAME_LATENCY_WAITABLE_OBJECT and ALLOW_TEARING, including the verified `0x842`
combination. Creation flags do not permit a nonzero Present flag. Fullscreen
exclusive, unsupported flags and formats reject before takeover. A free topmost
DirectComposition target is required.

The disposable exact player reported source flags `0x842` (ALLOW_MODE_SWITCH,
FRAME_LATENCY_WAITABLE_OBJECT and ALLOW_TEARING). A standalone chain with those flags and latency 1
returned ready for its first frame wait, timed out after 100 ms on another wait
without a source Present, then returned ready after a source Present. Therefore
an original-chain wait cannot simply be assumed to stay ready during suppression.
The exact local UnityPlayer and matching PDB were then inspected offline.
UnityPlayer SHA256 is
`5DCD2AF9E14F6416D73C361369559666C31263E4863822E1241CBDBC00C2F667`,
PDB GUID `7141C22D-795D-4040-AD7A-9D754B27047F`, age 1. DbgHelp required exact
symbols from the editor's `win64_player_nondevelopment_mono` variation.
`GfxDeviceD3D11::PresentFrame` (RVA `0x113B630`) calls the override query at
`0x113B6C0`; true skips the swapchain Present at `0x113B719`. Its earlier call
to `D3D11SwapChain::BlitColorBackBufferToSwapChainBuffer` confirms that the query
follows Unity's final swapchain blit. The separate
`GfxDeviceD3D11::WaitForLastPresentationAndGetTimestamp` (RVA `0x113B0F0`)
waits on the original latency handle with
`WaitForMultipleObjectsEx(2, handles, FALSE, INFINITE, TRUE)` at `0x113B2BB`.
The wait has no rendering-extension query. The matching maximum-latency setup
stores that DXGI handle at the exact offset consumed by the wait; `OnAfterPresent`
does not replenish it. Suppressing the original call does not bypass this wait.

Maintained-source mode preserves that dependency by making the original Present
itself. It does not alter the wait or infer its behavior from one suppressed
frame, which a spare latency token could conceal. The probe never waits
on, resets or signals Unity's original latency handle. The standalone test owns
and closes only its own handle.
[DXGI wait contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject).

The final D3D11 logical backbuffer zero is copied after Unity's final frame into
a D3D12 shared DEFAULT texture with SIMULTANEOUS_ACCESS and RENDER_TARGET flags.
ID3D11Device5/context4 and a same-LUID D3D12 DIRECT queue are required. A shared
fence orders the transfer; separate D3D11 retirement and D3D12 completion fences
protect lifetimes, including partial submission and device removal. CPU waits
are intentionally synchronous and bounded to five seconds each. The Windows
Present and DirectComposition commit-completion calls have no timeout argument.
This is a correctness experiment with all final UI already in the copied image.

Preparation completes the D3D11 copy and D3D12 transfer before any original
Present can rotate the logical buffer, and does not attach the visual. After
the atomic ticket claim, strict mode attaches and presents only the composition
chain. Maintained-source mode first calls the original Present with Unity's
unchanged interval and flags, then attaches/presents the composition chain.
The full-client visual uses alpha IGNORE, so source alpha does not reveal the
chain underneath. The standalone opacity control copied blue with alpha zero,
then changed the source to red: the displayed center and edges remained blue;
detaching restored the red source.

In strict mode original `ownedSingleCallSamples` is not incremented and the
original raw count stays unchanged. In maintained-source mode the legacy status
describes original attempts, results and single-call samples; composition status
describes only overlay attempts and samples. Both should increase by one per
successful real ticket, with no unexplained original calls. Never sum these
counts as displayed frames. A failed original Present stops future takeover and
skips that ticket's overlay Present; its owned ticket cannot be retried by Unity.

Disable using the corresponding request export with `(0, 0)` and continue final tickets.
Check `requested=active=attached=draining=0` and normal original-chain count
increments before unloading the harness. Source changes, preparation failures,
wrong-thread queries and Present failures stop the request. Detach commits a null
root and waits for DirectComposition acknowledgement before releasing the target;
GPU resources are released only after retirement. If strict-mode detach fails,
the ticket is held without another Present and detach is retried. A maintained
source continues exactly one original Present on each new ticket while detach
is pending; duplicate queries never add a call. This keeps Unity's wait moving
so it can reach another detach attempt. Control reads use one mode snapshot per
query, and mode changes preserve the attached context's maintenance requirement
until it has detached and retired. If detachment succeeds but
GPU retirement remains unproven, Unity may resume while the context remains
retained with `draining=1`. Never unload such a context. A terminal driver failure
can retain it until process teardown. Resize requires explicit re-enable after
the old context detaches; original swapchain/backbuffer pointers are never
released to force engine handoff. The probe retains no original buffer after a
successful copy and does not replace Unity's swapchain pointer. A wrong-thread
query for an unclaimed maintained-source ticket yields the original call to
Unity and disables future takeover; an already-owned ticket remains suppressed.

The disposable Unity 6000.5.8f1 player on RTX 5070 Ti completed an eight-second
maintained-source interval on 2026-09-13: 914 original calls and 914 composition
calls, each with 914 single-call samples, zero failures/mismatches/extra calls or
wrong-thread queries, and 16 sampled attached states. Disable completed one
detach, cleared attached/draining state and restored one Unity call per real
ticket. The actual native source was 2560x1440 RGBA8, FLIP_SEQUENTIAL, `0x842`,
sync 1, flags 0; managed `Screen` was 1920x1080. Use native dimensions for final
output transport rather than assuming the managed logical extent matches.
The accompanying harness passed 22 AA assertions: TAA before/after, then DLAA,
FSR Native AA and DLSS at 67%, with profile restoration. Its report is
`G:/KSP2-ReleaseTesting/frame-generation-20260913/maintenance/2026-09-13_040243_122_47c032602062/maintenance/report.json`.
Raw native observations are in
`G:/KSP2-ReleaseTesting/frame-generation-20260913/probe-maintenance/maintenance.jsonl`,
with the passive arming checkpoint in the same folder's `armed-from.json`.
This run proves that the exact player's frame pump and call ownership continued
through attach/detach. It did not capture active SkyboxScreenshot output, generate
frames, measure displayed FPS/latency, or validate resize, minimize/restore,
device removal, VRR, pacing or NVIDIA/AMD provider integration in the player.

The diagnostic `operation` values identify the last native stage: 1 D3D11
interfaces/adapter; 2 same-adapter D3D12 device; 3 queue/list; 4 fences; 5 shared
texture creation; 6 opening it in D3D11; 7 composition swapchain; 8 DComp device;
9 HWND target; 10 visual; 11 frame copy. `reason` and `lastResult` give the failure
category and HRESULT. A request is not evidence that preparation succeeded.

The inspected Unity 6000.5.8f1 D3D11/D3D12 interfaces expose swapchain getters,
but no setter, release/handoff operation or HWND ownership transfer. Calling
Release on Unity's borrowed pointer to force destruction would invalidate the
engine's own references. Its Present override only suppresses Present.

Windows offers a composition route that does not require a second user window
or another HWND-bound flip chain: create a D3D12 DIRECT-queue swapchain with
`CreateSwapChainForComposition`, attach it as `IDCompositionVisual` content,
bind a DirectComposition target to the existing same-process Unity HWND and
commit the visual. Microsoft explicitly layers DirectComposition above content
already drawn to that HWND by DirectX Present. `DCompositionCreateDevice2` accepts
a null rendering device when no DirectComposition-created surfaces are needed.
The standalone test validates this Windows plumbing and opacity; the player
run above validates bounded call ownership and restoration. Broader visual and
lifecycle validation remains necessary.
[Composition swapchain](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition),
[existing HWND target/layers](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondevice-createtargetforhwnd),
[composition device](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositioncreatedevice2).

Start such an experiment in windowed/borderless mode, require an unused composition
target layer, retain Unity's chain and prove suppression/restoration. A composition
chain requires FLIP_SEQUENTIAL and STRETCH; its GetHwnd, SetFullscreenState and
ResizeTarget operations are invalid. Focus, pointer mapping, hardware/software
cursor, overlay composition, resize, minimize/restore, tearing/VRR and pacing
must be tested separately. A chain is created only after an explicit composition
request; passive observation and original-chain pass-through create none.

The pinned vendor presentation layers are not interchangeable with that chain:

- AMD SDK `60f4ea81909200d8542eca14dccb2628b763a9a3` WrapDX12 ultimately calls
  `ffxReplaceSwapchainForFrameinterpolationDX12`. It requires successful GetHwnd
  and fullscreen description queries, releases the old chain and recreates one
  with CreateSwapChainForHwnd. A composition chain fails that contract. Using
  AMD's existing texture-only FG adapter with owned pacing/UI, or adapting the
  MIT swapchain source with a separate audit, remains additional work.
  [Pinned implementation](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/framegeneration/fsr3/dx12/FrameInterpolationSwapchainDX12.cpp#L453).
- Streamline 2.14.1's manual factory proxy forwards CreateSwapChainForComposition
  directly without installing the FG swapchain path. Its alternative vtable hook
  does wrap the result, but no composition-specific feature creation hook or
  DLSS-G composition/GetHwnd guarantee was found. Successful creation of a proxy
  would not establish FG compatibility. The guide supports selectively attaching
  FG to one chain while other chains remain ordinary; that does not establish
  support for this composition chain.
  [Manual factory proxy](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/source/core/sl.interposer/dxgi/dxgiFactory.cpp#L492),
  [vtable hook](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/source/core/sl.interposer/dxgi/dxgi.cpp#L250),
  [FG guide](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/docs/ProgrammingGuideDLSS_G.md#40-handle-multiple-swap-chains).

Accordingly, DirectComposition is a concrete presenter experiment; the current
vendor wrappers do not turn it into a ready shared NVIDIA/AMD FG route. The
subsequent disabled-child HWND route avoids that composition incompatibility
while preserving Unity's original waitable chain. NVIDIA and AMD standalone
child-provider controls now have generated-presentation evidence; the optional
coordinator below still needs its own complete player and visual validation.

## Optional frame-generation coordinator

`-DRBA_BUILD_FRAME_GENERATION_COORDINATOR=ON` adds
`GfxPluginReduxBetterAAFrameGeneration.dll` and `FrameGenerationCoordinatorTests`.
It statically links the child host and backend-neutral input pool; the NVIDIA
provider is loaded from the explicit absolute path in
[FrameGenerationCoordinator.h](FrameGenerationCoordinator.h). The coordinator
has no runtime test exports. Its C ABI is version 1, x64 Pack=8: Config 32,
Camera 304, Capture 368, TicketStatus 40 and Status 792 bytes. Native mode IDs
are Off 0, NVIDIA 2x/3x/4x 1/2/3, reserved AMD 2x 4 (currently Unsupported),
and hidden-only NVIDIA discovery 5. These are distinct from managed enums.
The additive V2 controls below implement AMD modes; ABI1 keeps its original
mode validation so an old binding cannot silently select a new provider.

Unity must register this GfxPlugin through its normal native-plugin loader;
file presence alone did not trigger loading in the disposable player. The
managed binding invokes its DllImport entry point through Unity, then verifies
the actual module path and `loaded=1`. It does not call raw LoadLibrary or spoof
UnityPluginLoad. Registration handles a D3D11 device that already exists.
Keep the pass-through presentation probe unloaded during coordinator tests.
Configure copies the absolute provider/runtime paths and requires the explicit
four verified presentation contracts (15). Main-thread Tick owns the disabled
child window; it never waits for SDK or GPU retirement.

The producer queues a capture event immediately after converting its owned
textures to display-sized encoded-sRGB RGBA8, render-sized raw R32 depth and
RG16 normalized motion. A separate immutable EOF event follows all cameras/UI
on every real frame, including frames without a scene capture. The native EOF
ends the actual render marker; the Present hook joins that frame's final
backbuffer. Both copies and their fences precede the original source Present.
One atomic ticket owner calls the original chain once per real frame, retaining
its waitable-chain progress, and exclusively calls the provider's child chain.
Duplicate queries and failed owned attempts cannot retry the source call.
Source call counts and provider presentation counts are separate observations.

The first provider frame, gaps in submitted real IDs and explicit camera cuts
reset provider history. A child image becomes eligible for visibility only
after provider Poll proves its queue and SDK input readers retired. QueueReady
exposes pool resources on the provider's actual same-adapter native D3D12 queue;
only a subsequently GPU-signaled completion fence retires that batch. COM
references alone never authorize source-pixel reuse. QueryTicket separately
reports event consumption, source-pixel retirement and full metadata retirement;
unconsumed eventData remains retained across disable and queue backpressure.

The source gate requires actual windowed RGBA8 FLIP_SEQUENTIAL, known `0x842`
creation flags, Present flags 0 and sync interval 0 or 1. The exact
`DXGI_PRESENT_ALLOW_TEARING` flag is also accepted only when windowed, sync0 and
the source was created with `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING`; extra Present
flags are rejected. The original interval/flags are forwarded unchanged, while
the child provider controls its own supported presentation flags. Unity's eligible capture
attests its public HDR output is inactive. An HDR desktop is accepted: Output6
describes the display's active capabilities, not the swapchain's color space.
Every diagnostic snapshot preserves that monitor color-space/bits observation;
each descriptor/output observation failure has an explicit reason. DXGI has no
current swapchain color-space getter; monitor information is not represented
as one. Native DXGI dimensions determine output extent, independently
of logical Unity Screen dimensions. Unexpected geometry/DPI, another presenter,
unsupported source state, a thread violation, or uncertain retirement blocks
new provider work and retains outstanding leases.
[Output6 display capabilities](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/ns-dxgi1_6-dxgi_output_desc1),
[Windows Advanced Color and SDR swapchains](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range).

Mode 5 creates only a hidden 2x context to obtain the actual NVIDIA multiplier
mask, then drains it without submitting or showing an image. The mask survives
Off until graphics-device generation changes. Complete Off is Disabled; a
finished discovery can report Ready with actual multiplier 0. Disposal must
keep a small owner-thread retirement tick and genuine no-scene EOF pump alive
until all packets, input batches, provider context and child lease retire.
The exact Unity rendering-extension ABI has no FrameEnd event; AfterDrawCall
cannot substitute for a real EOF identity. An uncertain device/provider drain
retains its resources instead of claiming completion.

The actual-DLL CPU suite passed241 checks for separate immutable events,
physical source telemetry while Off, no source/device/provider calls beyond
passive observation, correct capture pin transfer, disable with unconsumed
packets, queue capacity/backpressure, wrong-thread rejection, reset and empty
drain. These tests use mocked Unity/DXGI and establish neither in-player FG nor
image quality. Standalone provider and input-pool GPU evidence is documented in
their sibling directories. Player09 exercised the combined Unity coordinator
with the final runtime DLLs, including NVIDIA 2x/3x/4x, AMD compatibility 2x,
AA backend changes, Off, and a window-geometry fallback/restore cycle. These
are observed SDK/native presentation results, not displayed-FPS measurements
or a claim that every moving scene is artifact-free.

When a valid current capture is waiting behind the preceding SDK batch, the
render-thread join uses one50ms retirement budget to preserve consecutive real
frames and their one-frame motion. NVIDIA receives that bounded Poll timeout;
AMD uses nonblocking Poll under a monotonic deadline with1ms sleeps and requires
PollOk, not pending0 alone. Off, discovery, menus and invalid captures never
enter this wait. Mode/epoch/eligibility are rechecked before proceeding; timeout
retains the old batch and drops the current frame, preserving gap resets. No
global target frame rate or vSync setting is changed by the native coordinator.

Missing captures, a busy provider and that bounded timeout are soft presentation
backpressure only while the same control epoch and scene remain eligible. They
retain an already visible completed child instead of repeatedly revealing
Unity's newer underlying image and then returning to an older child image.
A failed input acquisition is soft only when the pool explicitly reports Busy
with no free slot, poison or error; invalid input and device failures remain hard.
SDK history reset remains independent: a reset caused by an input gap does not
turn a soft drop into a hard hide. Off, scene eligibility loss, window/epoch
invalidation and provider/device failure still hide immediately.

If a soft-held completed image reaches its 250 ms deadline without new completed
progress, the owner hides the child and closes new presents. The coordinator
latches ordinary rendering for that control epoch, drains existing provider and
pool work through the normal retirement protocol, and reports Disabled with
actual multiplier zero and an explicit Off/reselect retry message. The saved
request and capability masks are preserved. Repeated Poll, completed drain and
unchanged configuration cannot automatically show the lagging child again;
an explicit changed mode starts a new epoch. Disabled also rejects new managed
capture copies for both providers. This bounded policy has CPU and Win32
regressions; moving-scene/PresentMon comparison against the previous candidate
is still pending. It does not establish a fix for AA ghosting captured with FG Off.

Healthy teardown first proves SDK input retirement through Poll, drains every
remaining pool copy/queue tail, destroys the pool and coordinator completion
fence, then destroys the provider and releases queue/device/HWND leases.
Poisoned pending input can require provider cleanup to establish retirement
first. Successful pool destruction acknowledges every remaining ticket even
when the last ordinary poll preceded fence completion. A separate test-only
executable exercises 107 lifecycle fixture checks for this ordering, late-fence
acknowledgment, retained failed cleanup, the bounded NVIDIA Poll argument and
AMD's pending0/job-clear race. Numeric NVIDIA status diagnostics include SDK
runtime bits, the last SDK presentation count, native presentation delta/validity,
last Present HRESULT and asynchronous error count. They are appended to the
existing reason only when they fit, preserving the first SDK error on failure.
The fixture checks that precedence. Its fake boundaries and exports are never
linked into the runtime DLL.

Input acquisition can observe a completed private fence and reuse its slot
before an ordinary coordinator poll sees that completion. A successful new
lease therefore acknowledges older acquired tickets with the exact same
nonzero pool ID and slot and a strictly smaller serial. This preserves the
pool's positive full-retirement proof when the earlier token expires on reuse;
it does not retire an arbitrary invalid token or discard retained EOF references.
The fixture reproduces completion during acquisition and verifies foreign
pool/slot, equal/older serial, failed acquisition and invalid-token controls.
Player07 exposed two stranded timeout tickets through this race. Player08
validated the correction across 2,286 telemetry samples: borrowed managed
surfaces were at most three real frames old, including phases that exercised
the bounded retirement timeout. Player09 repeated that bound with the final
uninstrumented coordinator and recorded no stranded surfaces.

A provider's explicit `WindowChanged` rejection is recoverable only when it
reports no borrowed input and no poisoned state; NVIDIA must also report zero
asynchronous API errors and runtime failure bits. The coordinator suspends new
markers and captures, drains all pool/provider work, then requires a complete
disabled owner-thread tick before attempting a stable target again. The already
owned original Present is never retried on the rejected ticket. A device, SDK,
pending-input or prior terminal failure cannot enter this recovery path. This
addresses the player06 minimize/restore race without pretending uncertain GPU
work retired or clearing an SDK fault. The exact provider-rejection branch has
fixture coverage; it was not triggered by the subsequent player window test.

Player09 demonstrated the ordinary owner-thread geometry path. Changing Unity
to a 1280x720 window left its observed DXGI backbuffer at 2560x1440, so the host
hid and drained the child with `InvalidExtent` and let ordinary Unity output
continue. Restoring the original borderless mode automatically created a new
child epoch and resumed NVIDIA 3x without changing the FG selection. The
coordinator's explicit `WindowRecoveries` counter remained zero throughout;
this proves geometry fallback and stable rearming, not the separate
`WindowChanged` rejection path, minimize recovery, or FG in mismatched geometry.
Final Off released the child/context lease and input pool. Player09 recorded
11,165 owned source calls, all successful single count steps, with zero source
failures, count mismatches, unexpected calls, duplicate queries, or wrong-thread
queries. Its managed active label never reported true with a hidden child in
either its post-owner-tick status or the separate native telemetry snapshot.
The captured evidence is in the external `player09-diagnostics` directory under
`G:/KSP2-ReleaseTesting/frame-generation-20260913`; no readback hook or helper
was loaded in that final run. Exclusive fullscreen, device loss, unusual DPI,
and further GPU families remain outside this player validation.

`RbaFgGetDiagnostics` is a separate read-only336-byte Pack8 ABI1 export; ABI1/2
controls and status layouts remain unchanged. Process-lifetime counters separate
missing/rejected captures, eligibility gates, pending providers, wait timeouts,
transport/window errors, actual wait duration, successful/reset/gap submissions,
and actual marker failures. No-capture drops count only an otherwise eligible
active Present query; ineligible/source-gated frames are counted separately.
The snapshot also exposes main/render/retired IDs and the host's actual eligible
ready-image ID/age. `readyAgeMilliseconds=UINT64_MAX` means no eligible image.
These observations do not consume SDK counters or call any graphics API.

### Additive AMD coordinator ABI2

`RbaFgConfigureV2` takes 64 bytes with separate NVIDIA and AMD absolute provider
and runtime paths. `RbaFgGetStatusV2` returns 904 bytes; its embedded 792-byte
base retains ABI1 layout/version. AMD best 2x is mode4, hidden best discovery is
mode6, compatibility 2x is mode7, and hidden compatibility discovery is mode8.
Modes7/8 require explicit compatibility preference1; modes4/6 require0. Best
and compatibility each cache the actual family/version and multiplier mask
from a distinct initialized provider context. Discovery never submits frames
and keeps its child hidden; choosing a family is not proof that it is present.

Active AMD initialization requires the actual render width/height and reversed
depth convention in ConfigV2. A discovery context with0/0 extents uses the
observed physical output dimensions; those dimensions do not authorize a later
active input geometry. A geometry or depth change drains the existing context.
The managed producer must update these fields and does not require NVIDIA
Reflex markers on the AMD route. Both routes consume the same immutable capture
and EOF packets and provider-device input pool.

The AMD provider owns its native same-adapter D3D12 device/queue and official
paced child swapchain. It creates and presents on a worker because the SDK has
unbounded waits. SubmitOk accepts a job only: the child Present permit remains
held until Poll proves that batch's actual presentation, input readers and
COMMON restoration retired, or DestroyOk proves complete cleanup. Owner-thread
Tick can hide/invalidate the child while that permit remains held; it never
waits for the worker or destroys a retained HWND. The original Unity chain
continues normally during initialization, backpressure and asynchronous drain.

AMD native count delta, generated dispatches, accepted jobs and actual child
calls have separate V2 counters; accepted jobs do not become displayed FPS or
SDK presentation totals. A completed image alone can become visible, and the
provider still needs actual generated-presentation observations for an active
FG claim. The V2 CPU tests additionally reject mixed ABI layouts, missing AMD
paths, incomplete geometry and mismatched compatibility/depth flags, and show
that unsupported source output creates no context or capability cache.
An actual mocked Output6 reports HDR ColorSpace12/10 bits while the RGBA8 source
passes its source gate and reaches optional provider loading. Its observation
remains separate from the ensuing missing-provider error, with balanced COM refs.
