# Streamline capability bootstrap and child-window provider

This directory builds two independent Windows x64 DLLs. The capability probe
retains its original diagnostic ABI. `ReduxBetterAA.StreamlineProvider.dll`
implements fixed DLSS FG/MFG 2x-4x on a separate, actual disabled child HWND,
using a D3D12 device and intercepted queue on the source D3D11 adapter. Neither
DLL installs a Unity hook, creates the host window, or enables a settings choice.
The host coordinator and its verified input/presentation contracts control that.

The separate capability target queries the official NVIDIA Streamline 2.14.1
DLSS Frame Generation and Reflex capabilities on the source D3D11 adapter. It
creates a temporary D3D12 device on that same adapter and reports the SDK's
actual maximum generated-frame count and Dynamic MFG flag. Initial selectable
fixed multipliers are scoped to 2x, 3x and 4x; the raw SDK count is also retained.
One generated frame means a 2x displayed-frame multiplier.

**The capability probe remains a standalone diagnostic.**
`available` and `presentationReady` always remain zero. It does not create a
swapchain, intercept Present, dispatch interpolation, change the Unity device,
or enable a settings choice. A successful query is not player validation.
Do not invoke this synchronous probe during rendering or from `DllMain`.
The managed mod does not automatically load this target.

## Build

Use an external checkout of
`https://github.com/NVIDIA-RTX/Streamline` at v2.14.1 commit
`2122257e0fce486f91b385aa63b9a09b0a34b363`. The independent CMake project checks
the exact revision and unchanged headers. SDK and build output must remain
outside the Better AA checkout. This target does not need vendor binaries to
compile or run the contract tests.

```powershell
cmake -S Native/Streamline -B G:/build/BetterAA-Streamline `
  -G 'Visual Studio 17 2022' -A x64 `
  -DSTREAMLINE_SDK_ROOT=G:/dependencies/Streamline-2.14.1
cmake --build G:/build/BetterAA-Streamline --config Release --parallel
ctest --test-dir G:/build/BetterAA-Streamline -C Release --output-on-failure
```

To perform an actual GPU query, obtain NVIDIA's
[official x64 production release](https://github.com/NVIDIA-RTX/Streamline/releases/tag/v2.14.1)
outside source, and copy exactly these six files from `bin/x64` into a separate
directory: `sl.interposer.dll`, `sl.common.dll`, `sl.pcl.dll`, `sl.reflex.dll`,
`sl.dlss_g.dll`, `nvngx_dlssg.dll`. Retain the release's licenses alongside any
local runtime staging. Do not copy the additional SR, Ray Reconstruction or
other plugin DLLs into this directory. The loader verifies all six pinned SHA256
hashes before loading the offered runtime and validates NVIDIA's interposer
signature. Optional OTA preferences are disabled, but NVIDIA driver DRS overrides
can still select alternate plugins. After `slInit`, device setup and the state
query, the probe audits the actual loaded modules against the locked files;
feature entry-point owners are checked as well. A different module or location
rejects the capability result. The SDK can execute an override during its own
initialization before that audit; the probe does not claim to prevent this.
Unlisted DLLs and pre-existing Streamline/NGX FG
modules are refused. The six verified files stay locked against writes/deletion
for the runtime lifetime. Runtime redistribution and player packaging belong to
the host integration; this CMake project never stages vendor binaries into the
checkout or a game.

```powershell
G:/build/BetterAA-Streamline/Release/StreamlineProbeTests.exe G:/dependencies/Streamline-probe-runtime
```

The optional query uses this repository's existing Unity product GUID
`5be2ef0c-dad9-b134-4ae1-03b0d475456b` from `ProjectSettings/ProjectSettings.asset`
and its pinned Unity version, not an NVIDIA sample/game ID. The ABI accepts
either the actual Unity version plus project GUID, or a legitimately assigned
NVIDIA application ID. Engine/version alone is insufficient for the production
NGX path: its implementation requires both the engine version and project ID,
or it selects the application-ID initialization path. No generated replacement
identity or unregistered application ID is substituted.

`RbaSlProbeDesc` is 48 bytes and `RbaSlProbeStatus` is 640 bytes with Windows x64
natural packing. `result=0` means the query completed, never that FG is enabled.
`queriedMultiplierMask` sets bit N for Nx. SDK errors retain both the numeric
result and its first error log. Feature checks run before and after setting the
actual D3D12 device because device initialization can narrow support.
Every attempted `slInit`, including a partial-load failure, receives a shutdown
attempt before device release. Only successful `slShutdown` proves cleanup;
`eErrorNotInitialized` can leave the SDK's previously published logging state,
so it also triggers quarantine. A failed shutdown retains the session, module,
verified files and device until process exit. The native module containing the
logging callback is pinned before SDK initialization, so it cannot be unloaded
under a quarantined SDK. Another probe refuses the still-loaded singleton.

The additional internal tests inject initialization and shutdown failures into
the actual owner implementation. They check retained file lifetime and shutdown
invocation, including `eErrorNotInitialized`. A harmless, isolated test DLL checks
that an identically named/identically copied plugin loaded from another location
is rejected. This fixture implements no Streamline or NGX interface and is never
staged with the production runtime.

## Provider ABI and host ownership

`StreamlineProvider.h` defines the independent `RbaSlFg*` C ABI. Natural Win64
sizes are CreateDesc 56, Interfaces 24, Camera 304, Frame 384 and Status 616 bytes.
The opaque uint64 handle is valid until `Destroy` returns Ok. Create pins the
actual Unity 6000.5.8f1/project identity internally, validates the real source
adapter and disabled full-client child, starts SL before creating its new
intercepted queue/chain, and queries the actual available multiplier mask.
An initially hidden child is supported for discovery and first-image startup.
`Create` itself never presents or claims generated presentation. Unsupported
multipliers still return the queried mask when that query was reached.

The host owns the child HWND, parent, size and window-lease epoch. Keep all of
them stable through successful destruction. The provider never changes window
styles, focus, visibility or input routing. It checks actual HWND identity;
there are no fake `GetHwnd` or foreground answers. Maintain exactly one original
unhooked D3D11 source Present per real frame before submitting its child frame.
The second HWND avoids placing two flip chains on Unity's same HWND. A missing
or busy frame must cause the host to hide/yield the child, never repeat its
previous image as a fresh frame. Resize/epoch changes require drain and recreate.

`GetInterfaces` supplies borrowed **native** D3D12 device/DIRECT queue pointers,
explicitly unwrapped from SL, with checked COM device identity. The common input
pool can create/import shared textures on this exact device and enqueue truthful
D3D11 producer-fence waits onto this queue. Keep those borrowed interfaces within
the provider lifetime. `Submit` requires distinct same-device COMMON-state
textures: final RGBA8, matching HUD-less RGBA8, raw R32 device depth, and normalized
RG16/RG32 motion. Depth/motion must match each other at render extent; colors are
at child display extent. Optional real UI alpha is R8. Formats are not silently
reinterpreted. A null input fence requires value0 and prior writes already
ordered on the borrowed queue; an actual fence may legitimately use value0.

SDK tagging starts the input lease. The provider retains all texture, producer
fence, SDK input-processing fence and backbuffer references. After actual Present
it queues a wait on `DLSSGState.inputsProcessingCompletionFence`, then signals
its own queue-completion fence. Only `Poll` observing that fence releases inputs
and advances `lastRetiredFrameId`. An event wake alone is insufficient, and device
removal is never treated as successful input retirement. `Submit(Busy)` before
tagging preserves the preceding batch and marker frame for retry/discard. Once
SDK tagging or queue submission has begun, failures retain uncertain leases and
poison the provider. Missing input-completion proof prevents unsafe teardown.
After successful Poll, a host may enqueue its own same-device GPU fence signal
and give that fence to the common pool's Retire operation; it must not forge a
CPU completion signal. SDK counts, actual native counts and real calls remain
separate observations; they do not measure latency or prove image quality.

Create/Submit/Poll/Destroy run on the same presentation thread. Status queries
and real marker APIs can run elsewhere. The host must call BeginSimulation,
EndSimulation, BeginRender and EndRender at the actual engine boundaries with
one increasing nonzero uint32 real-frame ID. Three token slots permit main/render
thread overlap. BeginSimulation obtains the SDK token and calls Reflex sleep
before SimulationStart, outside the short bookkeeping lock. Submit supplies only
the PresentStart/End markers around its own actual Present. Missing, duplicated
or out-of-order markers reject the frame; DiscardFrame drops unsubmitted frames
without inventing timing events. A lifecycle guard keeps active calls alive
through a concurrent shutdown request. No present mutex spans Reflex sleep.

Camera arrays are row-indexed Unity column-vector transforms, `clip=P*V*world`.
The provider verifies finite rigid perspective camera metadata, matching P*V,
depth convention, FOV/aspect, units and real elapsed time. It transposes into SL's
row-vector convention, derives world forward from negative view Z, scales
view-distance quantities together, and derives current/previous clip transforms
from the same supplied camera snapshots. Jitter is supplied separately in render
pixels. Motion scaling is explicit: `previousUV=currentUV+storedMotion*scale`.
For this checkout's sanitizer, stored motion is `(currentUV-previousUV)*sign`,
so the host conversion is `scale=-sign`; default vendor AA and Custom TAA store
different signs. Reset on first frame, discontinuities, strategy changes and
producer history resets. First reset-frame elapsed time may be zero.

Both DLLs share the hardened loader and an exclusive named process semaphore,
which prevents simultaneous probe/provider initialization before either touches
the SDK singleton. Partial init and unsuccessful shutdown preserve the existing
file/module/callback quarantine rules. Failed Create can return a nonzero handle:
keep its HWND lease and call Destroy, preserving it until process exit if SDK
cleanup remains uncertain. Poll/Destroy timeouts are bounded 0..5000 ms; SDK calls
such as the vendor's shutdown/Present remain governed by the SDK itself.

The NVIDIA SDK initializes once per process. Turning FG Off drains inputs, sets
`DLSSGMode::eOff`, calls `slFreeResources` for the viewport, and releases its
swapchain and window lease. A private cache retains the initialized native D3D12
device/queues, SDK modules, immutable initialization strings, six verified file
locks and process semaphore. It contains no child HWND, chain or caller input
lease. This retains some device/SDK memory even while Off; the OS reclaims the
bounded cache at process exit, without calling vendor code under the DLL loader
lock. The provider DLL is itself pinned before `slInit`, keeping both its logging
and API-error callbacks valid even if a caller releases its DLL reference while
the initialized session is cached. Device removal, adapter/thread changes and terminal SDK failures require a
process restart, rather than attempting an unsafe second SDK initialization.
Reactivation audits actual module identities and the same physical runtime path.
Unrecognized preloaded modules remain refused. Partial or failed cleanup retains
the existing quarantine contract and can never become a reusable device session.

The reason for this lifecycle is reproducible with the pinned NGX runtime:
after `slShutdown`, `nvngx_dlssg.dll` retains a logging callback into the unloaded
`sl.common.dll`. Reinitialization can execute that stale address before replacing
the callback if the module reloads elsewhere. An exact-player crash dump and a
standalone test reserving only the vacated module address reproduced the same
`slSetD3DDevice` exception. Keeping just common's DLL mapped also preserves stale
SDK globals, so the provider keeps the initialized device session instead.

## UI and validation boundary

The basic HUD-less-plus-final path is supported with UI recomposition disabled.
It is not a claim of exact UI preservation: the DLSS-G guide recommends HUD-less
and actual separate UI inputs for best quality. RealUiAlpha explicitly requires
the real captured alpha and enables recomposition; alpha cannot be reconstructed
uniquely by subtracting two color images. Matching tone/color transfer, full
depth/motion coverage, menus, transitions and UI quality require player evidence.
AA/SR choices stay independent of FG readiness. The standalone checkerboard
diagnostic has no UI and cannot establish these player contracts.

The official SDK currently reports up to six displayed frames and Dynamic MFG
on supported devices; these are separate from the initial 2x-4x scope. Use
`DLSSGState.numFramesToGenerateMax` rather than a GPU-name table to choose fixed
multipliers. `numFramesToGenerate` equals the selected multiplier minus one.
Dynamic MFG requires its separately queried capability and proper limiter
integration; this provider exposes only fixed 2x-4x.

References:

- [Manual hooking and lifecycle](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/docs/ProgrammingGuideManualHooking.md)
- [DLSS FG inputs, multipliers, UI and Reflex](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/docs/ProgrammingGuideDLSS_G.md)
- [SDK identity and secure loading](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/docs/ProgrammingGuide.md)
- [Actual production NGX initialization](https://github.com/NVIDIA-RTX/Streamline/blob/v2.14.1/source/plugins/sl.common/commonEntry.cpp)

## Validation

On 2026-09-13, MSVC Release compilation with `/W4 /WX` and the standalone
contract and lifecycle/module-audit tests passed. Provider CPU tests additionally
cover actual ABI layout, perspective/clip/unit conversion, interleaved markers,
three-token backpressure, Discard, active Reflex sleep during shutdown, failed
cleanup quarantine, asynchronous-error persistence, and exclusive process ownership.
The signed production-runtime probe on the local RTX
5070 Ti returned DLSS FG and Reflex support, `maxGeneratedFrames=5`, initial
2x-4x mask `0x1c`, and Dynamic MFG support. Both availability fields remained
zero.

The final opt-in provider DLL controls each used 120 real frames. The 2x run
reported 240 SDK presentations and 237 native presentations observed before
teardown; the 4x run reported 480 and 472 respectively. Both had 120 unhooked
parent Presents/latency waits and no source-count mismatches, occlusion or
asynchronous errors. The actual disabled child and parent were visible, and
the foreground window was the parent. Hidden Create, Destroy, and subsequent
Create all succeeded in one process, before any Present. A second
Submit returned Busy while preserving the input lease, and releasing all four
caller texture references after first Submit was safe through completed Poll.
An additional same-process restart control retained the original D3D11 device,
parent HWND and source swapchain across a rendered 2x session, provider Destroy,
new disabled child and rendered 3x session. Each session rendered 120 real frames;
SDK/native presentation counts were 240/237 and 360/355, with zero errors.
The later `restart-relocated` regression reproduced the exact-player callback
failure by reserving the vacated common-module address after the first session.
With the initialized device-session cache, hidden discovery followed by rendered
2x, Off and rendered 3x succeeds without unloading common. Its two 120-frame
sessions reported SDK/native counts 239/236 and 358/353, zero asynchronous errors
and clean process exit; the first reset frame need not generate an extra frame.
A fresh-source 4x, Off, then 2x control also passed with counts 477/468 and
239/236, respectively. The subsequent registry-holder change only avoids SDK/COM
destruction under the process-exit loader lock and passed the CPU lifecycle tests.
These counts establish provider execution, not generated pixel quality or
end-to-end latency. RTX40 fallback and AMD hardware were not tested here.

Run this visible diagnostic only in a coordinated idle GPU window. The executable
is built but intentionally excluded from default CTest; it uses real GPU work and
an actual 1280x720 parent/child window. Each run renders 120 real frames and exits
after releasing the feature and child. Use an external process timeout when automating it.

```powershell
G:/build/BetterAA-Streamline/Release/StreamlineProviderGpuTests.exe `
  G:/dependencies/Streamline-probe-runtime 2
G:/build/BetterAA-Streamline/Release/StreamlineProviderGpuTests.exe `
  G:/dependencies/Streamline-probe-runtime 4
G:/build/BetterAA-Streamline/Release/StreamlineProviderGpuTests.exe `
  G:/dependencies/Streamline-probe-runtime 2 restart
G:/build/BetterAA-Streamline/Release/StreamlineProviderGpuTests.exe `
  G:/dependencies/Streamline-probe-runtime 2 restart-relocated
```

The optional `restart` argument renders a second 120-frame session at the next
multiplier (4x wraps to 2x), reusing the original source device/parent/chain and
creating a new child only after the prior provider's Destroy succeeds.
`restart-relocated` additionally verifies that common remains at its owned address;
against the earlier stopped-runtime implementation it reserves only the vacated
module address to expose the stale callback deterministically. SDK
operation failures include the first SDK error logged during that initialization
when available; the error is diagnostic evidence, not an inferred root cause.

The optional `hidden-first` control keeps the child hidden through its first
actual Submit and completed Poll, then shows it without activation. It is a
separate opt-in diagnostic for visibility recovery, not a default GPU test.
