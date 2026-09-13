# Optional AMD FSR runtime bridge

This Windows x64 bridge exposes native AA and temporal upscaling through the
official AMD FSR SDK 2.3.0 API. It derives the adapter from Unity's D3D11 texture,
creates an independent D3D12 device on that adapter, and lets AMD select its
supported provider. Both startup probing and the rendering context query the
actual provider version. The pinned runtime reports `3.1.5` on the tested NVIDIA
RTX 5070 Ti; AMD's supported ML path is FSR 4.1.1. A successful probe means a
context was created, not that player rendering was validated.

The bridge does not own Unity's swapchain, presentation, cameras, or UI. It does
not implement frame generation. Direct D3D12 and Vulkan Unity rendering are not
supported by this bridge.

## Build and package

Use an external checkout of
`https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` at
`60f4ea81909200d8542eca14dccb2628b763a9a3`, with API/upscaler headers,
`Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll`, `docs/license.md`,
and `3rdpartynotice.md` present. The build checks the revision and clean headers;
the runtime SHA256 is pinned in both the manifest and bridge loader. SDK files
and generated output must stay outside the Better AA checkout.

```powershell
./tools/Build-Native.ps1 -FidelityFxSdk 'G:/path/to/FidelityFX-SDK' `
    -BuildDirectory 'G:/path/to/native-build' -RunTests `
    -PackageDirectory 'G:/path/to/optional-packages'
```

Requires Windows x64, CMake 3.24+, Visual Studio 2022 C++ build tools, and a
Windows SDK. `-RunTests` executes standalone synthetic D3D11 GPU tests; it does
not start Unity or KSP2. The optional runtime archive contains exactly the bridge,
unmodified AMD upscaler DLL, full relevant notices, and a hash manifest under
`mods/ReduxBetterAA/native/`. It is separate from the public mod package. SDK
binary redistribution terms are preserved in that archive.

## Managed host contract

`FsrBridge.h` is the complete ABI. On Windows x64 ABI 1, create/dispatch/status
structures are 40/136/680 bytes. Compile-time assertions pin field offsets.
The host must keep the bridge loaded while Unity may hold callbacks and must
serialize context lifetime with its command stream.

1. On startup, call `RbaFsrProbe` with a representative D3D11 texture and actual
   intended create flags. It creates and destroys an actual provider context
   without submitting GPU commands or using the D3D11 immediate context.
2. `RbaFsrCreate` allocates a CPU context and verifies/loads the approved runtime.
   GPU initialization happens on the first render callback. Use `GetStatus` to
   verify the actual running provider matches the probe before retaining its
   settings label. Unsupported or failed contexts must use the managed fallback.
3. Prepare converted textures and call `RbaFsrPrepareDispatch` with a blittable
   descriptor. Pass the resulting immutable packet exactly once to render event
   1 with `CommandBuffer.IssuePluginEventAndData`. Eight preallocated packets
   hold COM references through submission; overflow returns `Busy`.
4. Queue event 2 with the context after its last event 1 on the same command
   stream. Poll for `Destroyed`, then call `RbaFsrRelease`. Never release a
   context while callbacks can still reference it. A GPU timeout retains native
   resources rather than releasing work that may be in flight; report failure
   and retain that context until process shutdown if destruction does not finish.

Required inputs are linear RGBA16F color/output, R32F **device depth**, and
RG16F/RG32F unjittered motion vectors. Only mip 1, array size 1, sample count 1,
and matching dimensions/device are accepted. Optional masks are render-sized
R8; manual exposure is a 1x1 R32F texture. Use exposure 1 and pre-exposure 1 for
the existing post-processing input, with HDR/auto exposure flags disabled.
Dispatch jitter is the negative raster jitter in render pixels; normalized
motion is scaled once to render pixels. Near/far values are forwarded unchanged.
All frame parameters must be finite, with positive timing/exposure/projection
scales. Recreate the context when render/output dimensions or input formats change.

## GPU ordering and validation limits

Each of three GPU slots owns shareable D3D12/D3D11 textures, allocator/list, and
borrowed Unity texture references. Resources start and end in `COMMON`. The
D3D11 input copies signal a shared fence; D3D12 waits, reconstructs, and signals;
D3D11 waits, copies the result to Unity's output, and signals retirement. The
next Unity work is ordered after that output copy. Slots are reused only after
retirement; no presentation hook or CPU image readback is used in rendering.

Standalone tests exercise provider probing, bounded packet overflow, input
lifetime after caller references are dropped, three-slot reuse, native AA, 2x
upscaling, HDR/automatic and LDR/manual exposure, per-pixel output readback,
invalid frame rejection, and cleanup. This does not establish Unity render
event scheduling, scene motion/depth correctness, visual quality, performance,
or AMD FSR 4.1 hardware behavior. Those still require targeted player/hardware
validation. Windows D3D11 shared fences and same-adapter D3D12 feature level 12.0
are required; unsupported devices fail back through the managed host.

The accompanying managed integration has also passed limited player native-AA,
SR, resize and scene-transition smoke tests on RTX 5070 Ti. See
[architecture validation](../docs/architecture.md#implementation-validation-2026-09-12)
for the exact scope; this does not expand AMD hardware or performance coverage.
