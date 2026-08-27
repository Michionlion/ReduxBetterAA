# Redux Better AA

Experimental temporal anti-aliasing and reconstruction for **Kerbal Space Program 2 Redux**.

The project begins with a safe, diagnostic rendering probe; progresses to a synchronized implementation of Unity Post Processing Stack v2 temporal anti-aliasing; then replaces that prototype with a purpose-built modern TAA backend. The same camera, depth, motion-vector, jitter, and history infrastructure is reused for managed NVIDIA DLAA and an experimental native-resolution Unity AMD FSR2 path before lower-resolution reconstruction changes are attempted.

> [!IMPORTANT]
> This repository should be developed phase by phase. Do not start with DLSS. The difficult part is not invoking an upscaler; it is proving that KSP2 Redux can provide a coherent scene-color, depth, motion-vector, and camera-jitter data set across its scaled-space and physics-space camera stacks.

## Project status

**Phase 2 / Phase 3 / Phase 4 plus native-resolution FSR2 comparison build — disabled by default.**

Version 0.5.19 assigns the engineering panel to `Ctrl+F10` and the combined
same-moment report and screenshot to plain `F10`. Modifier checks are exclusive,
so opening the panel cannot also arm a capture.

Version 0.5.18 removes all cloud-renderer overrides, shaders, settings,
diagnostics, and history manipulation from Redux Better AA. Those independent
features now live in Redux Better Clouds.

Version 0.5.17 exposes the stock spatial modes alongside the temporal and
vendor paths in Better AA settings. `FXAA Low` is KSP's fast PPv2 FXAA variant,
`FXAA High` is its quality variant, and `SMAA` is the existing PPv2 SMAA effect
using its shipped High quality preset. They are mutually exclusive with TAA,
DLAA, and FSR2 and do not create temporal history.

Version 0.5.16 makes Redux Better AA the sole owner of scene anti-aliasing while
the mod is loaded. KSP's separate MSAA selector is disabled and points users to
`Settings > Mods > Redux Better AA`; runtime MSAA is held at Off to prevent
double filtering. The normal mod settings contain exactly Mode, Sharpness, TAA
stability, and DLAA preset. Mode is hardware-aware, calls the custom backend
`TAA`, hides PPv2, omits DLAA unless Unity reports DLSS/DLAA support on the
active NVIDIA GPU, and orders DLAA before FSR2 when both are available. A single
0-to-1 Sharpness value drives every backend that supports sharpening, with zero
disabling it. DLAA preset K is the default and the legacy Default choice is
removed. Ctrl+F10 retains all engineering and comparison controls.

Version 0.5.15 corrects the FSR2 dispatch-jitter sign for the PPv2-style
projection helper used by Redux Better AA. FSR2 now receives the unit-pixel
offset that describes the projection sample actually rendered, matching AMD's
contract and the already-correct DLAA mapping. F10 schema 17 reports record both the
projection-helper input and FSR2 dispatch value. The output-aligned depth view
is also relabeled as a jitter-compensated point sample: it cannot reconstruct
missing coverage at a single-sample hard edge, so matching raw edge toggles do
not by themselves demonstrate camera or geometry shake.

Version 0.5.14 synchronizes temporal jitter across the main menu's predecessor
`Skybox` camera and `Camera.Scaled` resolve. Background objects whose color is
preserved while depth is cleared now receive the same subpixel sequence as the
resolve, allowing Custom TAA, DLAA, and FSR2 to reconstruct their edges with a
valid far-plane depth convention.

Version 0.5.13 aligns Custom TAA depth tests and history depth with its
de-jittered current color, and makes camera-motion fallback reconstruct raw
jittered depth in non-jittered coordinates. The Buffers tab now exposes paired
raw-jittered and output-aligned depth views so residual upstream camera or
geometry motion can be distinguished from the expected Halton sequence.

Version 0.5.12 replaces the absolute 64-pixel motion cutoff with a coherence-aware
policy: camera motion may exceed 256 pixels when it agrees with project-tracked
reprojection, while unverified motion above 256 pixels and disagreement above 96
pixels are rejected. Custom TAA now consumes the same sanitized input as DLAA and
FSR2 using non-jittered matrices. PPv2 remains an internal comparison path.

Phase 1 selected a unified final-scene resolve before UI composition. The mod keeps the Phase 2 PPv2, Phase 3 project-owned Custom TAA, and Phase 4 managed NVIDIA DLAA backends for direct comparison, adds an opt-in Unity AMD FSR2 Native AA experiment, and exposes KSP's two stock FXAA variants plus the existing PPv2 SMAA effect. `F12` cycles the supported public modes: Off, FXAA Low, SMAA, FXAA High, TAA, then hardware-supported vendor modes with DLAA preferred before FSR2. PPv2 remains available only in Ctrl+F10. The `Ctrl+F10` panel uses one spatial/PPv2/Custom/DLAA/FSR2/Buffers toolbar: choosing an AA mode both activates it and opens its settings, while Buffers leaves the current AA mode unchanged. Its content area scrolls independently so screenshot, report, and close controls remain visible. Advanced AA quality, exposure, supersampling, stability, and diagnostic controls remain in Ctrl+F10 rather than the normal settings page. Version 0.5.11 investigates the intermittent launchpad motion failure: DLAA and FSR2 negate both components of Unity's previous-to-current motion, then a 16-anchor same-frame GPU classifier replaces the observed screen-wide radial field before vendor execution. A 64 px/frame hard ceiling and bounded camera reprojection remain as local safeguards. The raw, sanitized-vendor, and sanitizer-decision views stay separate, and Ctrl+F10 can capture a fixed six-view diagnostic burst while the user pans. Matching reports record Unity's internal `_NonJitteredVP` and `_PreviousVP` beside project-tracked matrices to distinguish an engine previous-matrix fault from buffer reuse or sign configuration. Version 0.5.10 extended the temporal camera graph to KSC and the main menu and added same-scene state rediscovery. Automatic exposure prefers PPv2's asynchronously read 1x1 GPU result at a bounded 10 Hz and falls back to vendor auto exposure. DLAA defaults to preset K and may optionally run on Redux render scales above 100% before Redux downsamples the scene; FSR2 remains native-scale-only. Vendor paths provide a moving solid-depth-edge bias mask while leaving broad no-depth transparent and volumetric regions available for temporal accumulation. The exact KSP floating-origin snap is an explicit lightweight temporal reset without vendor-context recreation. Every AA page and Off baseline can run a fixed 240-frame performance profile. This workstation's test installation uses explicitly approved local copies of the signed Unity 6000.4.1f1 NVIDIA and AMD player runtimes; a future distributable must source licensed files from Redux core's matching Unity export. XeSS remains deferred. See [`docs/performance-profiling.md`](docs/performance-profiling.md), [`docs/decisions/0013-state-specific-camera-discovery-and-motion-consistency.md`](docs/decisions/0013-state-specific-camera-discovery-and-motion-consistency.md), and the later motion-diagnosis decision record.

## Goals

The project has five primary goals:

1. Determine exactly how Redux composes scaled-space, physics-space, presentation, map, VAB, and UI cameras at runtime.
2. Deliver a usable, stable TAA option without requiring native code.
3. Build a higher-quality, cross-vendor custom TAA backend designed around KSP2's thin geometry, large camera ranges, camera discontinuities, transparent effects, exhaust, and multi-camera scene composition.
4. Add NVIDIA DLAA when the required Unity runtime module and buffers are available.
5. Integrate DLSS Super Resolution into Redux's scene render-scale/presentation path while preserving native-resolution UI.

## Non-goals

The initial project does **not** include:

- DLSS Frame Generation.
- DLSS Ray Reconstruction.
- Conversion of KSP2 to HDRP or URP.
- Applying temporal reconstruction to native-resolution UI, text, map icons, or other elements that should remain crisp.
- Shipping unlicensed NVIDIA or AMD redistributables.
- Patching generated/publicized game assemblies on disk as the normal development workflow.
- Replacing Redux's renderer before the Phase 1 probe identifies the actual integration points.

## Why this is plausible

Redux exposes separate camera render stacks for scaled space and physics space, with access to their main cameras, camera lists, and post-process layers. Redux also has a render-scale presenter that can render scene cameras to a scaled target while leaving UI cameras at native resolution. Those are the fundamental access points required by TAA, DLAA, DLSS, and FSR2-style reconstruction.

The architectural uncertainty is whether Redux already has, or can create, a **coherent final depth and motion-vector representation** for the composited scene. Phase 1 exists to answer that question before any backend-specific work begins.

## Roadmap

### Phase 1 — Render probe and capability discovery

**Purpose:** Establish ground truth about the runtime renderer.

Build a normal Redux code mod that inventories active cameras and render stacks, visualizes depth and motion vectors, identifies final scene-composition and presentation points, and probes for NVIDIA/AMD Unity runtime modules.

Primary deliverables:

- Runtime camera graph and render-target inventory.
- Debug views for scene color, depth, motion vectors, and camera contribution masks.
- Capability report written to log and JSON.
- RenderDoc/Frame Debugger capture notes for representative scenes.
- A documented architecture decision: unified final temporal resolve, synchronized per-stack resolves, or additional composite-buffer construction.

**Exit gate:** We can identify the exact textures and render event needed for a temporal resolve, or we have documented precisely which missing buffer must be generated.

### Phase 2 — Post Processing Stack v2 TAA prototype

**Purpose:** Deliver the fastest credible visual improvement while validating temporal coordination.

Enable the TAA implementation already present in Unity's Post Processing Stack v2, then add the KSP2-specific work that a simple toggle does not provide:

- One shared jitter sequence across all scene cameras participating in one output frame.
- Coordinated history resets after camera cuts, quickloads, reverts, vessel changes, FOV changes, render-scale changes, origin rebases, teleports, and other discontinuities.
- Mutual exclusion with FXAA, SMAA, supersampling resolves, DLAA, and custom TAA where appropriate.
- Native-resolution UI preservation.
- Configurable quality and diagnostic controls.

**Exit gate:** TAA is visually stable in flight, map transitions, VAB, and representative camera motions; it produces no persistent double images after discontinuities; and it causes no steady-state managed allocations.

### Phase 3 — Custom modern TAA

**Purpose:** Replace the old PPv2 resolve with a controllable, cross-vendor backend designed for KSP2.

Build a C# render backend and HLSL resolve pipeline with:

- Shared low-discrepancy jitter.
- Motion-vector reprojection.
- Depth/disocclusion rejection.
- Neighborhood or variance clipping, preferably in YCoCg or another luminance-aware space.
- Catmull-Rom history sampling.
- Velocity-adaptive history weighting.
- Reactive/transparency masks for exhaust, particles, and rapidly changing transparent shading.
- Optional mild sharpening.
- Explicit history ownership and deterministic reset behavior.

**Exit gate:** The custom backend is at least as stable as PPv2 TAA, materially reduces ghosting or softness in identified KSP2 problem scenes, and remains usable on non-NVIDIA hardware.

### Phase 4 — NVIDIA DLAA backend

**Purpose:** Reuse the established temporal inputs to provide the highest-quality native-resolution AA path available on supported NVIDIA GPUs.

Preferred implementation:

- Use Unity's managed `UnityEngine.NVIDIA` API.
- Create one persistent DLSS/DLAA context per active scene output.
- Run with equal input and output dimensions and DLAA quality mode.
- Supply scene color, depth, motion vectors, jitter, reset state, exposure configuration, and optional masks.
- Fall back cleanly when the module, driver, GPU, or feature is unavailable.

Contingency implementation:

- Only if the managed module cannot be used, evaluate a separate native bridge based on NVIDIA Streamline/NGX.
- Keep the bridge isolated behind the same backend interface.
- Do not commit redistributables until their licensing and distribution requirements are documented.

**Exit gate:** DLAA initializes and shuts down safely; survives resolution and scene transitions; produces no UI reconstruction; and automatically falls back to custom TAA or PPv2 TAA on unsupported systems.

### Phase 5 — DLSS Super Resolution

**Purpose:** Replace Redux's scene scaling/presentation blit with temporal reconstruction at lower internal resolution.

This phase should preferably be implemented upstream in Redux, or in close cooperation with Redux maintainers, rather than as a permanently invasive external Harmony patch.

Build:

- A general `IRenderUpscaler` abstraction adjacent to `RenderScalePresenter`.
- DLSS Quality, Balanced, and Performance modes.
- Recommended input-size selection and context recreation.
- Correct motion-vector scaling, projection jitter, texture LOD bias, exposure, and history resets.
- Native-resolution UI composition after reconstruction.
- Bilinear/native and temporal fallback backends.
- An extension point suitable for FSR2 or another cross-vendor backend.

**Exit gate:** DLSS correctly controls internal scene resolution, reconstructs to the requested output resolution, leaves UI native, survives all renderer lifecycle events, and improves GPU-bound performance without introducing unacceptable instability.

## Architectural principles

### One temporal owner per output

Exactly one active backend owns temporal jitter, history, and the final resolve for a scene output. Do not stack PPv2 TAA, custom TAA, DLAA, FSR2, or DLSS on top of one another.

### Scene reconstruction before UI

Temporal reconstruction must operate on scene color before native-resolution UI, text, icons, and menus are composed. UI must not contribute to motion-vector or temporal history buffers.

### Shared frame data

Every backend consumes the same normalized frame description:

```text
TemporalFrameInputs
├─ scene color
├─ depth
├─ motion vectors
├─ optional exposure
├─ optional reactive/transparency masks
├─ current and previous camera matrices
├─ jitter
├─ render and output dimensions
├─ frame delta
└─ reset reason/state
```

### Capability-driven behavior

Backends advertise support at runtime. Unsupported backends never crash the game or prevent the settings menu from loading. The fallback order is explicit and logged.

### Probe before assumption

KSP2 Redux has a non-trivial multi-camera renderer. Runtime observations and frame captures take precedence over assumptions based on stock Unity behavior.

## Proposed repository layout

This layout assumes development from the current Redux mod template. Adapt folder names to the template rather than moving template-owned infrastructure unnecessarily.

```text
ReduxBetterAA/
├─ AGENTS.md
├─ README.md
├─ SPEC.md
├─ Assets/
│  └─ ReduxBetterAA/
│     ├─ Code/
│     │  ├─ ReduxBetterAAMod.cs
│     │  ├─ Configuration/
│     │  │  ├─ BetterAAConfig.cs
│     │  │  └─ BackendSelection.cs
│     │  ├─ Rendering/
│     │  │  ├─ CameraGraph.cs
│     │  │  ├─ CameraDiscovery.cs
│     │  │  ├─ SceneBufferProvider.cs
│     │  │  ├─ TemporalCoordinator.cs
│     │  │  ├─ TemporalFrameInputs.cs
│     │  │  ├─ HistoryResetTracker.cs
│     │  │  └─ RenderHooks.cs
│     │  ├─ Backends/
│     │  │  ├─ ITemporalBackend.cs
│     │  │  ├─ DisabledBackend.cs
│     │  │  ├─ Ppv2TaaBackend.cs
│     │  │  ├─ CustomTaaBackend.cs
│     │  │  ├─ NvidiaDlaaBackend.cs
│     │  │  └─ DlssUpscaler.cs
│     │  ├─ Diagnostics/
│     │  │  ├─ RenderProbe.cs
│     │  │  ├─ CapabilityReport.cs
│     │  │  ├─ DebugOverlay.cs
│     │  │  └─ BufferVisualizer.cs
│     │  └─ Patches/
│     │     ├─ CameraLifecyclePatches.cs
│     │     ├─ RenderScalePresenterPatches.cs
│     │     └─ Ppv2JitterPatches.cs
│     ├─ Shaders/
│     │  ├─ DepthDebug.shader
│     │  ├─ MotionVectorDebug.shader
│     │  ├─ CameraContributionDebug.shader
│     │  ├─ TemporalResolve.shader
│     │  ├─ ReactiveMask.shader
│     │  └─ Sharpen.shader
│     ├─ UI/
│     │  └─ BetterAASettings.uxml
│     └─ Tests/
│        ├─ EditMode/
│        ├─ PlayMode/
│        └─ GoldenCaptures/
├─ Native/                 # Created only if the managed NVIDIA path is impossible.
│  └─ ReduxDLSSBridge/
└─ docs/
   ├─ captures/
   ├─ capability-reports/
   ├─ benchmarks/
   └─ decisions/
```

## Core interfaces

The project should converge on backend-neutral interfaces early:

```csharp
public interface ITemporalBackend : IDisposable
{
    string Id { get; }

    bool IsSupported(
        in TemporalCapabilities capabilities,
        out string unsupportedReason);

    void Configure(in TemporalBackendConfig config);

    void OnOutputChanged(in TemporalOutputDescriptor output);

    void Execute(
        CommandBuffer commandBuffer,
        in TemporalFrameInputs inputs,
        RenderTargetIdentifier output);

    void ResetHistory(HistoryResetReason reason);
}
```

Full DLSS uses a related upscaler contract because render and output sizes differ:

```csharp
public interface IRenderUpscaler : IDisposable
{
    string Id { get; }

    UpscalerRecommendation GetRecommendation(
        in UpscalerRequest request);

    void Execute(
        CommandBuffer commandBuffer,
        in TemporalFrameInputs inputs,
        RenderTargetIdentifier output);

    void ResetHistory(HistoryResetReason reason);
}
```

The exact interfaces may change after Phase 1, but backend-specific types must not leak throughout camera-discovery and lifecycle code.

## Development prerequisites

- A clean KSP2 installation compatible with the target Redux build.
- A separate Redux development/test installation.
- The current Redux mod template and SDK branch matching that build.
- The exact Unity editor version required by that SDK/template.
- Git and Git LFS where required by the template.
- A GPU debugger such as RenderDoc for Phase 1 and later shader work.
- At least one NVIDIA RTX machine for DLAA/DLSS testing.
- At least one non-NVIDIA or unsupported machine/configuration to verify fallback behavior.

Always follow the current Redux template documentation for project setup, publicization, building, and installation. Do not copy assemblies from a different Unity version into the player.

## Build and run workflow

1. Create or clone the project from the current Redux mod template.
2. Pin SDK/template dependencies to the branch or commit used by the target Redux build.
3. Publicize game assemblies using the template's supported workflow when required.
4. Open the project in the exact Unity editor version expected by the template.
5. Build through the existing Redux/ThunderKit pipeline.
6. Install into a disposable Redux test instance.
7. Capture logs and the generated capability report on first launch.
8. Test the smallest applicable phase gate before adding more rendering code.

Do not create a parallel custom deployment system unless the template pipeline cannot support a documented requirement.

## Configuration model

The eventual user-facing configuration should include:

```text
Backend
  Off
  PPv2 TAA
  Custom TAA
  NVIDIA DLAA
  DLSS Quality
  DLSS Balanced
  DLSS Performance

Quality
  Low
  Medium
  High
  Custom

Sharpening
History stability
Motion response
Reactive-mask strength
Debug view
Capability report
Fallback behavior
```

Backend availability is determined at runtime. Unsupported choices remain disabled with a concise reason.

## Testing

Every phase must be tested in at least these contexts:

- Flight at the launchpad.
- Slow and fast camera orbit around a vessel.
- Thin geometry such as struts, antennae, ladders, landing gear, and procedural edges.
- Engine ignition, exhaust, reentry, atmosphere, and particles.
- Surface terrain at low altitude.
- Orbit with planet-limb and distant-object edges.
- Flight-to-map and map-to-flight transitions.
- Quickload, revert, vessel switch, tracking-station transition, and resolution change.
- VAB camera motion and part outlines.
- UI-heavy scenes and map icons.

Each comparison should include the same camera path with:

- AA off.
- Existing spatial AA.
- Redux supersampling where available.
- Current project backend.

Record visual results, GPU/CPU cost, render-target memory, and any history-reset events. Release builds must have no steady-state managed allocations from this project.

## Compatibility and safety

- All render textures, command buffers, native contexts, and event subscriptions must be released deterministically.
- The mod must disable itself cleanly when required render objects are absent.
- Scene or resolution changes must never leave stale history resources bound.
- Native APIs must be feature-probed before use.
- The game must remain launchable when an optional backend fails to initialize.
- Debug passes must be off by default in release builds.
- Do not reconstruct native-resolution UI.
- Do not commit third-party proprietary binaries without confirmed redistribution rights.

## Contribution workflow

1. Read `AGENTS.md` and `SPEC.md` before changing rendering code.
2. Work within the current phase.
3. Add or update a decision record when changing the integration architecture.
4. Include before/after captures for visual changes.
5. Include a capability report and benchmark metadata for backend changes.
6. Keep PRs narrow: discovery, lifecycle, shader algorithm, and native integration should not be mixed without a strong reason.

## Reference material

- [KSP2 Redux](https://github.com/KSP2Redux/Redux)
- [Redux mod template](https://github.com/KSP2Redux/Redux.Template)
- [Redux API documentation](https://github.com/KSP2Redux/API-Documentation)
- [Unity Post Processing Stack v2](https://github.com/Unity-Technologies/PostProcessing/tree/v2)
- [Unity NVIDIA graphics-device API](https://docs.unity3d.com/ScriptReference/NVIDIA.GraphicsDevice.html)
- [Unity NVIDIA DLSS texture table](https://docs.unity3d.com/ScriptReference/NVIDIA.DLSSTextureTable.html)
- [NVIDIA Streamline](https://github.com/NVIDIA-RTX/Streamline)

## License

Choose and add a project license before accepting external contributions. Third-party SDKs, native plugins, shaders, and redistributables retain their own licenses and distribution conditions.
