# Redux Better AA

Experimental temporal anti-aliasing and reconstruction for **Kerbal Space Program 2 Redux**.

The project begins with a safe, diagnostic rendering probe; progresses to a synchronized implementation of Unity Post Processing Stack v2 temporal anti-aliasing; then replaces that prototype with a purpose-built modern TAA backend. If the Redux player exposes Unity's NVIDIA module, the same camera, depth, motion-vector, jitter, and history infrastructure will be reused for NVIDIA DLAA and, later, DLSS Super Resolution.

> [!IMPORTANT]
> This repository should be developed phase by phase. Do not start with DLSS. The difficult part is not invoking an upscaler; it is proving that KSP2 Redux can provide a coherent scene-color, depth, motion-vector, and camera-jitter data set across its scaled-space and physics-space camera stacks.

## Project status

**Planning / pre-implementation.**

The documents in this repository define the intended architecture, phase gates, testing requirements, and contributor workflow. The first executable milestone is the Phase 1 render probe.

## Goals

The project has five primary goals:

1. Determine exactly how Redux composes scaled-space, physics-space, presentation, map, VAB, and UI cameras at runtime.
2. Deliver a usable, stable TAA option without requiring native code.
3. Build a higher-quality, cross-vendor custom TAA backend designed around KSP2's thin geometry, large camera ranges, camera discontinuities, clouds, exhaust, and multi-camera scene composition.
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
- Reactive/transparency masks for clouds, exhaust, particles, and rapidly changing shading.
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

Exactly one active backend owns temporal jitter, history, and the final resolve for a scene output. Do not stack PPv2 TAA, custom TAA, DLAA, or DLSS on top of one another.

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
- Engine ignition, exhaust, reentry, clouds, atmosphere, and particles.
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
