# AMD frame generation and shared inputs

This independent Windows x64 library includes a paced child-window provider,
the original texture-generation experiment, and reusable D3D11/D3D12 inputs.
The paced provider uses the official AMD swapchain and its scheduling threads.
The original texture APIs remain separate and keep `presentationSupported` zero.
These native tests alone do not enable frame generation in the Unity player.

The current SDK 2.3.0 pin contains FSR FG 4.0.1 and the FSR 3.1.6 compatibility
provider. Creation uses the SDK-selected supported provider and reports its actual
name/version; the explicit compatibility option selects an ID returned by the
SDK's provider enumeration. FSR FG 4.0.1 requires Windows 11, AMD 9000-series or
later and DX12 Agility SDK 1.4.9 or later. The existing Unity D3D11 device does
not establish those requirements. The signed DLL is hash-verified before load,
held against replacement, and rejected if another copy is already loaded.

Only 2× is modeled: one intermediate frame per real frame. The API's `outputs[4]`
storage is not evidence of supported AMD multi-frame generation. No GPU-name
heuristic or successful upscaling call grants FG support.

## Paced child-window provider

`AmdFrameGenerationProvider.h/.cpp` exposes a separate ABI1. Its fixed disabled
child HWND covers the parent's full client area. The host owns that HWND and
epoch and maintains Unity's original source Present exactly once per real frame.
The provider creates an actual same-adapter D3D12 device/DIRECT queue from the
source D3D11 device, or validates a caller-supplied native queue. `GetInterfaces`
exposes those native interfaces for `FrameGenerationInputPool`; there is no
additional private-device texture round trip.

Creation uses `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12` with
the 3.1.7 version descriptor, plus the SDK-selected FG effect/version and an
explicit HUD-less RGBA8 creation descriptor. The compatibility preference selects
an actually enumerated FSR3.1 provider. Status reports the selected provider name,
version, family, and the supported 2× bit, after real context creation.

Each accepted batch contains matching encoded SDR final/HUD-less RGBA8, raw
render R32 device depth, and RG16/RG32 normalized motion. The 304-byte camera
layout matches the NVIDIA provider's Unity column-vector convention. The worker
derives position/basis from the inverse rigid view matrix and converts normalized
motion scale to render pixels for PrepareV2. Frame time, reset, camera planes and
view-space units remain explicit. Configure precedes PrepareV2; the SDK callback
records actual generation, while the SDK owns interpolation queues and paced
generated/real child presentations. The implementation never substitutes two
immediate application Presents for SDK scheduling.

The pinned swapchain has unbounded internal waits, including `WaitForPresents`.
A persistent worker contains those calls. Create returns Busy with a live context
during initialization. Submit returns Ok when its one immutable batch is accepted;
it does not report a completed Present. Poll is nonblocking. Retirement requires
the SDK's game/interpolation/presentation fences, the exposed queue's fence, and
restoration of caller resources to COMMON. A private state records whether the
restoration commands were submitted, so a failed signal never causes the same
barrier to be recorded twice during cleanup. SDK failures poison the context and
retain every uncertain input and the child-window lease.

Destroy requests worker cleanup and remains Busy until completion. The host must
keep all accepted pixels immutable and retain the child HWND/parent/epoch until
the corresponding retirement ID or successful Destroy. A hung SDK cannot be
cancelled safely; its context and window lease remain quarantined. The callback
module is pinned against unloading. Host API calls, especially Destroy, must be
externally serialized. Resize and scene/metadata changes require a drained new
context; this provider does not create windows or install Unity hooks.

`AmdFrameGenerationProviderTests` creates an owned parent with the player's
observed waitable flags `0x842` and a real disabled child. On RTX5070Ti, the SDK
selected FSR3.1.6: 30 real child calls produced 60 native presentations; explicit
compatibility recreation produced 16 native presentations from 8 real calls.
The parent advanced exactly once per real frame. Tests also cover actual
same-adapter and caller-queue identity, invalid cameras/aliases/epochs, parent
resize/child-origin rejection, unsignaled producer backpressure, caller reference
release while inputs are pending, and bounded Destroy while GPU work is blocked.
This establishes native paced execution and retirement, not complete Unity UI
coverage, player image quality, latency, or FG4 on unavailable AMD hardware.

## Provider-owned input pool

`FrameGenerationInputPool.h/.cpp` is independent of the AMD SDK. Its ABI 1
uses Unity's actual D3D11 device and a caller's native DIRECT D3D12 queue. It
allocates three fixed slots on that queue's device, after checking the actual
adapter LUID. The standalone CMake target is a static library; another native
coordinator can compile the source with `RBA_FG_INPUT_STATIC`.

Each slot contains display-sized final/HUD-less RGBA8, raw render-sized R32
device depth and RG16 motion. RG32 can be requested, but the actual D3D11 import
must succeed: this host rejected RG32 sharing with `E_INVALIDARG`, so creation
returns `RbaFgInputUnsupported` and no pool. The caller must explicitly produce
RG16 motion and recreate; the transport never silently changes precision.
Cross-API format sharing depends on the runtime/driver's supported resource
formats, beyond ordinary texture-format support.
[Microsoft shared-resource tiers](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_shared_resource_tier),
[D3D12 cross-API resource sharing](https://microsoft.github.io/DirectX-Specs/d3d/ResourceHeaps.html).

`Acquire → CopyScene → CopyFinal → QueueReady → Retire → QueryLease` separates
the real scene borrow from the final backbuffer join. Copies run on the original
D3D11 render thread. `QueueReady` inserts a GPU fence dependency on the supplied
queue and exposes its four resources in COMMON state, without a CPU wait.
The consumer restores COMMON state and supplies a same-device GPU completion
fence covering every asynchronous reader. A private D3D12 fence then proves
slot retirement; a separate private D3D11 fence proves source-copy retirement.
Loss of one device never falsely retires pending work on the other.

The coordinator must report each token's retirement before recycling its slot.
Old tokens become invalid on reuse; they cannot inherit a newer frame's result.
Cancellation is allowed before the resources are handed to the consumer.
Destroy is nonblocking, requires exclusive caller access, and returns Busy while
leases or uncertain queue work remain. A failed signal retains/quarantines the
pool and inputs until completion can be proved or the relevant device is lost.

`FrameGenerationInputPoolTests` exercises all three slots with different image
bytes, blocks the D3D11 producer and an asynchronous D3D12 consumer independently,
drops caller source references during pending copies, and checks exact readback,
device/format validation, cancellation, backpressure and stale-token rejection.
RG16 transport is verified on the local GPU; RG32 rejection is verified, not
reported as successful RG32 transport. This pool performs no FG dispatch or Present.

## Build and run

Use an external clean SDK checkout at the commit in `runtime-manifest.json`.
Sparse checkouts need both `Kits/FidelityFX/api/include` and
`Kits/FidelityFX/framegeneration/include` materialized. Keep the build directory
outside this checkout:

```powershell
cmake -S Native/FrameGeneration -B G:/KSP2/_research/amd-fg-build `
  -G 'Visual Studio 17 2022' -A x64 `
  -DFFX_SDK_ROOT=G:/external/FidelityFX-SDK
cmake --build G:/KSP2/_research/amd-fg-build --config Release
ctest --test-dir G:/KSP2/_research/amd-fg-build -C Release --output-on-failure
# Optional: fails explicitly when the Windows Graphics Tools debug layer is absent.
G:/KSP2/_research/amd-fg-build/Release/AmdFrameGenerationTests.exe `
  G:/external/FidelityFX-SDK/Kits/FidelityFX/signedbin --debug
```

The original texture test selects a hardware D3D12 adapter, creates the actual vendor
context and feeds moving 640×360 scene/depth/motion textures. It checks RGB
interpolation differs from both real inputs while retaining scene content,
resource alias, invalid-camera and duplicate-frame rejection, discontinuity reset,
timeout/poll recovery followed by a second delayed submission, completed GPU work,
destruction and explicit FSR 3.1 provider selection. It never opens a game
window or performs a Present. The separate paced-provider test opens its own
non-activating test window and performs actual source/child presentations.

## Ownership contract

`AmdFrameGeneration.h` defines the versioned ABI. This prototype accepts only
display-sized RGBA8 UNORM LDR scene/output, render-sized R32 float depth and
RG16 float motion without jitter. Motion scale and camera data are explicit.
Inputs/output must belong to the exact D3D12 device owning the DIRECT queue,
enter and leave `COMMON`, and remain unmodified during the call. Submit producer
work onto that queue before dispatch; synchronize other producer queues first.
The caller is responsible for complete scene depth/motion and matching color
content. The original dispatch accepts HUD-less scene color; the additive optional
UI path below accepts both HUD-less scene color and final color with UI.

Each call follows the mandatory `Configure → PrepareV2 → Dispatch` order and sets
`FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY`. Creation descriptor storage
survives for the context's lifetime. The library serializes context operations,
retains all input/output COM references and waits at most five seconds for its own
fence. Timeout retains resources; polling can confirm later completion. Completion
events only wake the wait loop; the actual current fence value is checked before
retiring resources. A removed device invalidates generated output and reports a
device error; it permits resource destruction, never a successful dispatch. Destroy
refuses to release a context while live-device queued work may still reference
it. A queue signal failure without device loss cannot prove completion and
retains the context. The caller must serialize lifetime, especially destruction.
Native dispatch failures require a fresh context; invalid inputs do not.
Poll returns coherent result/status values: ordinary retired work or rejected
arguments leave `Ok`, while a poisoned context retains its terminal failure.

This synchronous, one-frame-at-a-time implementation is an algorithm and resource
lifetime prototype. It is not a production pacing architecture. A complete
provider still needs player validation of the D3D11/D3D12 texture transport,
bounded asynchronous leases, one Present owner, window/resize transitions,
complete UI input coverage and real/generated-present measurements. The existing
scene eligibility gate remains authoritative.

### D3D11 texture transport

`AmdFrameGenerationD3D11.h` adds separate create/dispatch/poll/destroy entry points
without changing the D3D12 ABI. The 56-byte create description accepts the actual
scene `ID3D11Device`, fixed render/display extents and explicit
`RbaAmdFgSrgbEncoded` color metadata. It creates D3D12 on that same DXGI adapter,
checks both adapter LUIDs, and requires D3D11 shared-resource/shared-fence support.
The existing `RbaAmdFgFrame` metadata is reused; its four texture pointers identify
`ID3D11Texture2D` resources when passed to the D3D11 entry point.

The formats remain explicit: display-sized RGBA8 encoded sRGB, render-sized R32
float depth and RG16 float motion. Compatible typeless storage and RGBA8 sRGB
storage are accepted without format conversion. Textures need single-mip,
single-sample default storage. Resize requires drained destruction and recreation.
No shader color conversion, scene capture or depth repair occurs. Optional SDK UI
handling uses the matching color pair described below.

Dispatch copies D3D11 inputs into persistent shareable textures, signals a shared
fence and orders the independent D3D12 queue after that signal. The existing FG
implementation generates into the shared output. Once its actual completion fence
is reached, the wrapper copies generated color into the caller's D3D11 output.
Both APIs share resources in `COMMON`. A separate D3D11 retirement fence tracks
copies, so D3D12 device removal cannot falsely prove D3D11 copy completion.

Dispatch and Poll run on the original render thread. A timeout retains the caller's
texture references and all shared/native resources. Poll may finish the generated
output copy after native completion. The caller must also keep every frame texture
unmodified until Poll or Destroy returns `Ok`; holding COM references does not stop
an owner from overwriting GPU storage. A native/device failure poisons the context
and invalidates its output: retain all frame textures until Destroy returns `Ok`,
even if Poll reports an error. If signaling the retirement fence fails after copies
were submitted on a live device, completion cannot be proved; destruction stays
`GpuBusy` and resources remain held until device removal. Invalid arguments are
checked before new copies, and do not alter any earlier pending frame's lease.
Destruction can run after other calls stop;
it drains work and discards any generated output not yet copied, without issuing
D3D11 immediate-context calls from another thread. Busy destruction retains the
transport and prohibits further dispatch. It never takes presentation ownership.

The additional `AmdFrameGenerationD3D11Gpu` CTest performs actual GPU round trips
with unequal render/display extents and recreated native extents. It checks RGB
interpolation, device identity, alias/extent rejection, render-thread ownership,
and a deliberately blocked D3D11 producer followed by timeout/Poll recovery after
the caller releases input references. Production dispatch uses no CPU readback;
the test reads back output solely to validate the generated image.

### Optional HUD-less compatibility input

`RbaAmdFgDispatchWithHudLess` and `RbaAmdFgD3D11DispatchWithHudLess` are additive
entry points; existing structures, ABI versions and dispatch functions are
unchanged. They accept an optional fifth texture pointer. With that pointer set,
the frame's existing `color` texture contains the final image including UI and
the fifth texture contains the corresponding scene before UI was drawn. Both
need the same real-frame identity, display extent and encoded sRGB RGBA8 content.
The fifth resource must belong to the same device and differ from final color
and output. Null selects the original path. Switching between those strategies
resets temporal history.

The native adapter validates all resources and frame metadata before submission,
passes the fifth texture to `ffxConfigureDescFrameGeneration.HUDLessColor`, and
retains all five resources through the same GPU retirement and terminal-error
rules. The D3D11 transport adds a persistent fifth shared texture and copies the
additional scene input before signaling its producer fence. Texture descriptors
and frame IDs cannot prove content freshness: capturing the color pair from the
same actual frame remains the caller's responsibility.

The pinned SDK uses HUD-less color for optical flow/interpolation and compares it
with final color to identify static UI differences. Its inpainting shader blends
those regions toward the final color. This is a documented compatibility strategy
with heuristic detection, not exact alpha recovery. It avoids a separate alpha UI
texture when the integration can provide a complete, matching pre-UI/final pair.
It does not prove that the mod has captured every Unity overlay.

Both GPU tests exercise a moving scene behind a static high-contrast checker HUD,
check HUD RGB preservation and continued scene interpolation, and reject aliased,
stale or incompatible inputs. The D3D11 test also deliberately times out, releases
every caller-held source reference including HUD-less color, then checks the late
generated output after Poll completes. These checks establish the synthetic SDK
path only; no game UI coverage, alpha-composition equivalence or presentation
cadence is inferred.

The official AMD swapchain API accepts D3D12 queues and exposes wrap/new/HWND
creation, UI registration, wait-for-presents and pacing support. Unity's D3D11
`GetSwapChain` and Present override do not provide a setter for replacing Unity's
swapchain or prove D3D11 compatibility with that API. The paced child provider
uses a separate host-owned child and leaves the source swapchain intact. HUD-less color comparison
can satisfy the UI strategy only after the integration proves both captures cover
the intended scene and all visible overlays.

## Validation limits

These are synthetic execution tests; they do not measure game image quality,
latency, presentation cadence or AMD hardware support on a different adapter.
The optional debug run requires Windows Graphics Tools and fails explicitly if
the D3D12 debug interface is unavailable. Record actual GPU, runtime provider,
test output and unperformed checks in the change review. No game installation
or vendor package is modified by these tests.

Official pinned sources:

- [Frame generation API](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/docs/techniques/frame-interpolation-api.md)
- [FSR FG 4.0.1 requirements](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/docs/techniques/frame-interpolation-ml.md)
- [FSR FG 3.1.6 requirements](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/docs/techniques/frame-interpolation.md)
- [Swapchain 3.1.7](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/docs/techniques/frame-interpolation-swap-chain.md)
- [DX12 swapchain create/version/wait API](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/framegeneration/include/dx12/ffx_api_framegeneration_dx12.h)
- [Swapchain callback, pacing, wait and native counters](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/framegeneration/fsr3/dx12/FrameInterpolationSwapchainDX12.cpp)
- [HUD-less configuration and provider dispatch](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/framegeneration/fsr3/internal/ffx_provider_fsr3framegeneration.cpp)
- [Compatibility UI reconstruction shader](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/framegeneration/fsr3/include/gpu/frameinterpolation/ffx_frameinterpolation_inpainting.h)
