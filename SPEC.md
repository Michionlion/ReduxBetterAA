# Redux Better AA — Engineering Specification

**Status:** Draft for implementation  
**Project:** Redux Better AA  
**Target:** Kerbal Space Program 2 Redux  
**Audience:** Rendering engineers, Unity/C# mod developers, shader developers, coding agents, Redux maintainers

## 1. Executive summary

KSP2 currently exhibits objectionable spatial aliasing and temporal shimmer, especially on thin vessel geometry, terrain edges, planetary silhouettes, and detailed surfaces. Redux already provides supersampling, but supersampling scales rendering cost rapidly and cannot provide the temporal stability of a motion-aware reconstruction algorithm.

This project will establish a backend-neutral temporal rendering layer in five phases:

1. Probe and document the runtime renderer.
2. Stabilize Unity Post Processing Stack v2 TAA as a working prototype.
3. Implement a modern custom TAA backend.
4. Add NVIDIA DLAA on supported systems.
5. Integrate DLSS Super Resolution into Redux's scene render-scale/presentation path.

The central technical risk is KSP2 Redux's multi-camera scene composition. Flight rendering uses separate scaled-space and physics-space camera stacks. Temporal reconstruction requires scene color, depth, motion vectors, jitter, and matrices that all describe the same final scene. Phase 1 is a mandatory architecture-discovery phase intended to determine whether one unified final resolve is possible or whether additional composite buffers or synchronized per-stack resolves are required.

## 2. Problem statement

The renderer needs an anti-aliasing option that:

- materially reduces jagged edges and subpixel shimmer
- remains stable during camera motion
- preserves thin vessel geometry
- does not blur or ghost UI
- handles scene transitions and camera discontinuities correctly
- works across scaled-space and physics-space scene content
- supports a cross-vendor baseline
- optionally provides DLAA and DLSS on supported NVIDIA hardware
- integrates with Redux's existing mod and render-scale infrastructure

A simple full-screen spatial filter is insufficient. A simple TAA toggle is also insufficient because independent camera stacks can advance jitter or history separately and because KSP2 frequently changes camera mode, vessel, origin, field of view, render scale, or scene.

## 3. Objectives

### 3.1 Primary objectives

- Build a deterministic runtime representation of Redux's camera graph.
- Identify or construct coherent scene color, depth, and motion-vector inputs.
- Centralize projection jitter for all scene cameras contributing to one output.
- Centralize temporal-history reset policy.
- Define a backend-neutral temporal frame contract.
- Deliver production-usable PPv2 TAA and custom TAA backends.
- Add managed Unity DLAA when supported.
- Refactor scene scaling/presentation around a general upscaler interface for DLSS SR.

### 3.2 Secondary objectives

- Provide diagnostic overlays and machine-readable capability reports.
- Establish a repeatable visual and performance benchmark protocol.
- Make the architecture extensible to FSR2 or another temporal upscaler.
- Produce changes that can be upstreamed to Redux where renderer integration is more appropriate than an external mod.

### 3.3 Non-goals

- Frame Generation.
- Ray Reconstruction.
- HDRP/URP migration.
- Ray-tracing integration.
- Reconstruction of native-resolution UI.
- General graphics overhaul unrelated to temporal AA.
- Permanent support for arbitrary stock-KSP2 mod loaders; target the current Redux mod system.

## 4. Constraints and assumptions

### 4.1 Known constraints

- Flight scene rendering is divided into scaled-space and physics-space camera stacks.
- Each stack can expose a main camera, render-camera collection, and post-process layer through Redux APIs.
- Redux has a render-scale presenter capable of redirecting scene cameras into a scaled target while retaining native-resolution UI.
- Temporal reconstruction requires matching color, depth, motion vectors, matrices, and jitter.
- Vendor feature availability depends on shipped Unity modules, GPU, driver, operating system, graphics API, and vendor runtime.
- The exact Redux SDK/template and Unity editor version must match the target Redux build.

### 4.2 Assumptions to verify in Phase 1

The following are hypotheses, not design facts:

- The final scene color can be accessed before native-resolution UI composition.
- A final depth texture exists and covers both scaled-space and physics-space pixels.
- A final motion-vector texture exists and covers both spaces.
- The presentation camera can host or trigger a temporal resolve.
- Unity's NVIDIA module is present in the distributed player or can be enabled by Redux maintainers.
- Motion-vector values and orientation are compatible with Unity's PPv2/NVIDIA APIs after normalization.

No later phase may silently depend on an unverified hypothesis.

## 5. Terminology

**Scene camera:** A camera rendering 3D world content intended for temporal reconstruction.

**Scaled space:** Distant celestial/body representation rendered by Redux's scaled-space camera stack.

**Physics space:** Nearby vessel, terrain, and physics-world content rendered by the physics-space camera stack.

**Presentation camera:** Camera or component that presents a composed scene render target to the display.

**Output:** Final scene image at the target display resolution before native-resolution UI.

**Render resolution:** Internal scene resolution. Equal to output resolution for TAA/DLAA; lower for DLSS SR.

**Temporal history:** Persistent prior-frame data used for reprojection and accumulation.

**Jitter:** Subpixel projection offset applied to scene cameras to sample different locations across frames.

**Reactive mask:** Per-pixel signal reducing trust in temporal history for rapidly changing or transparent content.

**History reset:** Invalidation of temporal history after a discontinuity or incompatible configuration change.

## 6. System architecture

### 6.1 High-level data flow

Preferred architecture:

```text
Scaled-space scene cameras ─┐
                            ├─> coherent scene color/depth/motion vectors
Physics-space scene cameras ┘                    │
                                                 ├─> TemporalCoordinator
                                                 │      ├─ jitter
                                                 │      ├─ matrices
                                                 │      └─ reset state
                                                 │
                                                 └─> active temporal backend
                                                        ├─ PPv2 TAA
                                                        ├─ Custom TAA
                                                        ├─ NVIDIA DLAA
                                                        └─ DLSS SR
                                                               │
                                                        scene output
                                                               │
                                                  native-resolution UI
                                                               │
                                                           display
```

### 6.2 Architecture branches after Phase 1

#### Branch A — Unified final buffers available

One temporal resolve runs after scaled/physics composition and before UI. This is the preferred architecture.

Requirements:

- final scene color available as texture
- final scene depth covers all reconstructed pixels
- final motion vectors cover all reconstructed pixels
- shared output dimensions and coordinate conventions

#### Branch B — Color is unified; depth or motion vectors are per-stack

Create coherent composite depth and motion-vector buffers, or modify the composition path to preserve them.

Potential techniques:

- allocate explicit depth/MV targets shared by both stacks
- copy or composite stack outputs using depth-aware rules
- modify `RenderScalePresenter` to own scene color, depth, and MV together
- add renderer instrumentation for objects that do not write motion vectors

One final resolve remains the target.

#### Branch C — Unified final resolve is not practical

Run synchronized temporal resolves per scene stack, then composite their resolved outputs.

Requirements:

- one shared jitter value per output frame
- synchronized frame index and reset state
- no second temporal resolve after composition
- documented handling of stack boundaries and silhouettes

This branch is acceptable for Phase 2 experimentation but may limit DLAA/DLSS quality and should be revisited before Phase 5.

#### Branch D — Motion vectors are unusable

Do not proceed directly to DLAA/DLSS.

Options:

- create a project-owned motion-vector pass
- instrument relevant renderers/materials
- use depth/camera-motion-only reprojection as a temporary custom-TAA mode
- fall back to spatial AA or supersampling while the missing data is addressed

## 7. Core components

### 7.1 `ReduxBetterAAMod`

Responsibilities:

- mod lifecycle
- configuration registration
- service construction
- Harmony patch installation
- graceful disablement
- shutdown cleanup

It must not perform per-frame renderer discovery.

### 7.2 `CameraDiscovery`

Responsibilities:

- locate active Redux camera views/stacks
- identify scaled-space and physics-space stacks
- enumerate render cameras in order
- identify post-process layers
- identify presentation camera and render-scale presenter
- classify UI/non-scene cameras
- produce `CameraGraph`

Discovery is event-driven or rate-limited during transitions. The steady-state renderer uses cached references.

### 7.3 `CameraGraph`

Proposed model:

```csharp
public sealed class CameraGraph
{
    public ICameraRenderStack? ScaledSpaceStack { get; init; }
    public ICameraRenderStack? PhysicsSpaceStack { get; init; }
    public Camera? ScaledMainCamera { get; init; }
    public Camera? PhysicsMainCamera { get; init; }
    public IReadOnlyList<Camera> SceneCameras { get; init; }
    public Camera? PresentationCamera { get; init; }
    public IReadOnlyList<Camera> UiCameras { get; init; }
    public RenderScalePresenter? RenderScalePresenter { get; init; }
    public int Revision { get; init; }
}
```

Use the exact types available in the pinned Redux SDK. The model must tolerate absent stacks in menu/VAB/map contexts.

### 7.4 `SceneBufferProvider`

Responsibilities:

- expose scene color, depth, motion vectors, optional exposure, and masks
- normalize dimensions, formats, and texture-coordinate conventions
- own any project-created composite buffers
- recreate resources when output descriptors change
- provide validation diagnostics

The provider returns an invalid result rather than a partially mismatched input set.

### 7.5 `TemporalCoordinator`

Responsibilities:

- own global temporal frame index
- generate one jitter sample per output frame
- apply jitter consistently to contributing scene cameras
- preserve non-jittered matrices
- collect current/previous matrices
- aggregate reset reasons
- select and execute active backend
- manage backend transitions and fallback

Only this component advances the jitter sequence.

### 7.6 `HistoryResetTracker`

Required reset triggers:

- first valid frame
- backend change
- scene/view change
- camera cut or large discontinuity
- quickload or revert
- vessel/focus change where camera/world transforms jump
- origin rebase/floating-origin discontinuity
- teleport
- significant FOV/projection change
- render/output resolution change
- render-scale change
- invalid or missing temporal input
- context recreation

Reset thresholds for matrix/position changes must be configurable for diagnostics and conservative by default.

### 7.7 `CapabilityReport`

Produces structured output containing:

- mod version/commit
- Redux/game version
- Unity version
- graphics API
- GPU/vendor/device information where available
- camera graph
- target texture names, formats, and dimensions
- active PPv2 AA state
- depth/MV support and validation
- render-scale presenter state
- Unity NVIDIA module presence
- NVIDIA API load/feature status
- AMD module status where probed
- chosen architecture branch
- selected backend and fallback reason

Output locations must use the mod's supported data/log directory.

## 8. Backend contracts

### 8.1 Temporal capability model

```csharp
public readonly struct TemporalCapabilities
{
    public readonly bool HasSceneColor;
    public readonly bool HasDepth;
    public readonly bool HasMotionVectors;
    public readonly bool HasExposure;
    public readonly bool HasReactiveMask;
    public readonly bool SupportsPpv2Taa;
    public readonly bool SupportsNvidiaDlaa;
    public readonly bool SupportsNvidiaDlss;
    public readonly GraphicsDeviceType GraphicsApi;
}
```

### 8.2 Output descriptor

```csharp
public readonly struct TemporalOutputDescriptor
{
    public readonly int RenderWidth;
    public readonly int RenderHeight;
    public readonly int OutputWidth;
    public readonly int OutputHeight;
    public readonly RenderTextureFormat ColorFormat;
    public readonly bool Hdr;
    public readonly int CameraGraphRevision;
}
```

Backends recreate immutable resources when this descriptor changes materially.

### 8.3 Frame inputs

```csharp
public readonly struct TemporalFrameInputs
{
    public readonly RenderTargetIdentifier Color;
    public readonly RenderTargetIdentifier Depth;
    public readonly RenderTargetIdentifier MotionVectors;
    public readonly RenderTargetIdentifier Exposure;
    public readonly RenderTargetIdentifier ReactiveMask;
    public readonly Matrix4x4 CurrentView;
    public readonly Matrix4x4 CurrentProjection;
    public readonly Matrix4x4 PreviousViewProjection;
    public readonly Vector2 JitterPixels;
    public readonly Vector2 JitterNormalized;
    public readonly float DeltaTimeSeconds;
    public readonly HistoryResetReason ResetReasons;
    public readonly TemporalOutputDescriptor Output;
}
```

Use explicit validity flags or optional wrappers appropriate to the project's C# version rather than relying on a default `RenderTargetIdentifier` to mean absent.

### 8.4 Backend interface

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

### 8.5 Fallback policy

Default fallback order:

```text
Requested DLSS SR
  → NVIDIA DLAA or custom TAA at native resolution if SR setup fails
  → custom TAA
  → PPv2 TAA
  → existing spatial AA / off

Requested NVIDIA DLAA
  → custom TAA
  → PPv2 TAA
  → existing spatial AA / off

Requested custom TAA
  → PPv2 TAA
  → existing spatial AA / off
```

Fallback is logged once with a machine-readable reason.

## 9. Phase 1 specification — render probe

### 9.1 Purpose

Convert renderer uncertainty into measured facts and select an integration architecture.

### 9.2 Required code

#### Mod/lifecycle layer

- Redux mod entry point.
- Harmony patch installer.
- Scene/view lifecycle listeners.
- Rate-limited discovery coroutine/service.

#### Camera graph probe

For each camera, record:

- name and instance ID
- enabled/active state
- depth/order
- clear flags
- culling mask
- rendering path
- target texture
- pixel rect/dimensions
- HDR/MSAA/dynamic-resolution flags
- depth texture mode
- post-process layer and AA mode
- command buffers by event where accessible
- associated render-scale presenter/presentation camera

#### Buffer visualizers

Build shaders/materials for:

- linearized depth
- raw and normalized motion vectors
- motion-vector magnitude/angle
- camera contribution ID/mask
- final color inspection

The visualizer must not permanently modify the production renderer. It should attach only while a debug mode is active.

#### Capability probe

Probe without hard failure:

- `UnityEngine.NVIDIAModule` assembly presence
- NVIDIA plugin load state/API types
- DLSS feature availability
- Unity AMD module presence and FSR2 types, optionally
- graphics API
- motion-vector support

Use cached reflection delegates if compile-time references would prevent loading on systems without the module.

#### Report writer

Write JSON plus concise human-readable log output.

### 9.3 Required captures

Capture at least:

1. Main menu or KSC scene.
2. VAB with a detailed vessel.
3. Launchpad flight.
4. Low-altitude terrain view.
5. Orbit with visible planet limb.
6. Map view with vessel and flag icons.
7. Flight-to-map transition.

For each, record camera graph changes and buffer validity.

### 9.4 Architecture decision output

Create `docs/decisions/0001-temporal-resolve-placement.md` documenting:

- chosen branch A/B/C/D from Section 6.2
- exact render event/hook
- buffer ownership
- UI composition point
- motion-vector conventions
- unresolved risks

### 9.5 Acceptance criteria

Phase 1 is complete when:

- final scene color before UI is identified
- depth and motion-vector coverage is demonstrated or proven missing
- scaled/physics camera order is documented
- render-scale presenter behavior is documented
- vendor module status is known on at least one supported and one fallback configuration
- no debug feature remains active by default
- the probe causes no steady-state allocation when idle

## 10. Phase 2 specification — PPv2 TAA

### 10.1 Purpose

Provide a fast production candidate and validate temporal lifecycle, jitter, and reset infrastructure.

### 10.2 Required code

#### `Ppv2TaaBackend`

- Locate relevant `PostProcessLayer` instances.
- Enable `TemporalAntialiasing` only on intended scene camera(s).
- Enable required `DepthTextureMode.Depth | MotionVectors` flags.
- Preserve and restore original AA configuration when disabled.
- Expose quality parameters:
  - jitter spread
  - sharpness
  - stationary blending
  - motion blending

#### Shared jitter integration

PPv2 instances normally maintain their own sample index. Replace or coordinate this behavior so every scene camera contributing to one output frame uses the same jitter sample.

Preferred order of approaches:

1. Use PPv2's supported custom jitter callback where sufficient.
2. Patch the smallest internal jitter generation point.
3. Take ownership of camera projection jitter in `TemporalCoordinator`.

Do not patch the entire PPv2 render method unless necessary.

#### History reset integration

Invoke PPv2 history reset on every aggregated reset reason. If the method is non-public, use a cached reflected delegate or a narrowly scoped Harmony access mechanism.

#### Backend mutual exclusion

When PPv2 TAA is active:

- custom TAA is disabled
- DLAA/DLSS are disabled
- existing spatial AA on the same resolve is disabled unless intentionally retained
- supersampling remains an independent render-scale choice only when the resulting order is understood and tested

### 10.3 Initial presets

Presets are tuning baselines, not contractual values:

```text
Balanced
  jitter spread: moderate
  stationary history: high
  motion history: lower
  sharpening: mild

Stable
  jitter spread: moderate-high
  stationary history: high
  motion history: moderate
  sharpening: low

Crisp
  jitter spread: lower
  stationary history: moderate-high
  motion history: lower
  sharpening: moderate
```

Store numeric values in configuration assets/code, not in documentation alone.

### 10.4 Acceptance criteria

- No independent jitter divergence between scaled and physics content.
- No persistent history after quickload, revert, camera cut, FOV jump, or render-scale change.
- UI and map icons remain acceptably crisp and are not included in temporal history.
- No steady-state managed allocations.
- Backend can be enabled/disabled repeatedly without leaking command buffers or histories.
- Visual captures show clear temporal-stability improvement over AA off/spatial AA.
- Known PPv2 limitations are documented for Phase 3.

## 11. Phase 3 specification — custom modern TAA

### 11.1 Purpose

Create a cross-vendor temporal AA implementation with explicit control over KSP2-specific ghosting, disocclusion, thin geometry, transparency, and sharpness.

### 11.2 Required resources

Per active output, allocate as needed:

- history color A/B
- optional history depth
- current resolve target
- optional moments/variance target
- optional reactive mask
- optional transparency/composition mask
- optional dilated motion/depth target

Resource formats must be selected based on scene HDR state and measured precision requirements. Document memory cost at 1080p, 1440p, and 4K.

### 11.3 Pass pipeline

Recommended initial pipeline:

```text
1. Validate inputs and reset state
2. Optional depth/motion dilation
3. Reproject history using motion vectors
4. Reject invalid/out-of-bounds/disoccluded history
5. Build current-neighborhood statistics
6. Clamp history to current neighborhood/variance bounds
7. Compute velocity/reactive/depth-adaptive history weight
8. Blend current and history
9. Optional sharpen
10. Write output and next history
```

### 11.4 Algorithm requirements

#### Jitter

- Low-discrepancy sequence such as Halton 2/3.
- Skip the zero sample if it causes instability.
- Sequence length configurable, with a tested default.
- Express jitter in pixel and normalized/device coordinates.
- Apply identically to all contributing scene cameras.

#### Reprojection

- Confirm whether motion vectors encode current-to-previous or previous-to-current displacement.
- Normalize scale and Y orientation once in `SceneBufferProvider`.
- Use current and previous matrices for validation and camera-only fallback.
- Reject out-of-bounds history.

#### History sampling

- Use bicubic/Catmull-Rom sampling or another validated high-quality filter.
- Avoid ringing at high-contrast silhouettes.
- Handle render/output dimension changes through reset/recreation, not undefined resampling, until explicitly implemented.

#### History clipping

Implement at least one robust method:

- 3x3 neighborhood min/max clamp
- variance clipping
- clip-to-AABB

Prefer luminance-aware/YCoCg space to reduce color leakage. Validate the chosen space against HDR values.

#### Disocclusion

Use current depth and previous/reprojected depth where available. History weight approaches zero when depth disagreement exceeds a scale-aware threshold.

#### Motion adaptation

High velocity receives lower history weight. Stationary surfaces may use higher weight for stability.

#### Reactive/transparency handling

Generate or infer a mask for:

- engine exhaust and plumes
- particles
- atmospheric and volumetric effects where history is unreliable
- transparent cockpit/canopy elements
- emissive rapidly changing effects
- UI-like world-space markers if present in scene color

The mask lowers history contribution. It must not indiscriminately erase temporal stability across the whole image.

#### Sharpening

Provide optional mild sharpening after resolve. Avoid dark halos and excessive noise. Sharpening must be disabled or adjusted when the output is subsequently reconstructed by another backend.

### 11.5 Debug modes

- current color
- history color
- reprojected history
- motion vectors
- depth rejection
- reactive mask
- history weight
- clamp extent
- final resolve

Debug modes must compile out or remain inactive in release configuration.

### 11.6 Acceptance criteria

- Equal or better temporal stability than Phase 2 in all benchmark scenes.
- Reduced ghosting in at least the Phase 2 documented failure cases.
- No severe flicker introduced by over-aggressive history rejection.
- Thin geometry remains more stable than spatial AA.
- Camera discontinuities recover within the reset frame without lingering trails.
- Works on non-NVIDIA hardware supported by Redux.
- No steady-state managed allocations and no unbounded history growth.

## 12. Phase 4 specification — NVIDIA DLAA

### 12.1 Purpose

Provide native-resolution NVIDIA temporal reconstruction using the same validated scene inputs and lifecycle infrastructure.

### 12.2 Preferred managed implementation

Use Unity's `UnityEngine.NVIDIA` module when present and supported.

The backend must:

1. Load or access the Unity NVIDIA plugin through supported APIs.
2. Create the Unity NVIDIA graphics-device wrapper once per process as required.
3. Check DLSS/DLAA feature availability for the active adapter.
4. Create one persistent context per active output descriptor.
5. Initialize equal input and output dimensions with DLAA quality mode.
6. Supply mandatory textures:
   - color input
   - color output
   - depth
   - motion vectors
7. Supply execution data:
   - jitter offsets
   - motion-vector scaling/orientation
   - pre-exposure or auto-exposure mode
   - history reset
   - subrect information if used
8. Execute through a Unity command buffer at the selected scene-resolve point.
9. Destroy/recreate context on immutable descriptor changes.
10. Destroy context before graphics-device shutdown.

### 12.3 Assembly-absence strategy

The core mod must load when `UnityEngine.NVIDIAModule` is absent.

Allowed approaches:

- isolated optional assembly loaded only when dependencies exist
- reflection wrapper with cached delegates
- compile-time feature assembly separated from core

Do not place unconditional NVIDIA type references in an assembly required on every system unless the Redux player always ships the dependency and this is verified.

### 12.4 Input validation

DLAA execution is skipped and fallback selected if:

- color/depth/MV dimensions mismatch
- required texture is null/invalid
- context output descriptor is stale
- graphics API is unsupported
- feature availability changes or initialization fails
- camera graph revision invalidates buffer ownership

Errors are logged once with vendor result information where available.

### 12.5 Optional masks

If Unity's API/backend supports bias color or transparency masks, map project reactive-mask information deliberately. Do not bind arbitrary masks without validating expected meaning and format.

### 12.6 Native bridge contingency

A native bridge is a separate approved workstream, not the default Phase 4 implementation.

Required deliverables before native coding:

- written proof that managed Unity integration is impossible for the target build
- graphics API identification
- method to obtain native device/command list/resources safely
- Streamline initialization timing plan
- state restoration plan
- licensing/distribution plan
- no-native fallback build

### 12.7 Acceptance criteria

- Unsupported systems load and run using fallback.
- Supported NVIDIA system initializes DLAA without native crashes.
- Equal input/output dimensions are used.
- UI remains native and outside history.
- Context survives normal camera motion and resets correctly on discontinuities.
- Resolution/backend switches recreate resources safely.
- DLAA is visually compared against custom TAA using identical camera paths.
- Shutdown and backend switching leak no contexts/resources.

## 13. Phase 5 specification — DLSS Super Resolution

### 13.1 Purpose

Use temporal reconstruction to render the 3D scene below output resolution and reconstruct to native display resolution, improving GPU-bound performance while maintaining or improving image quality.

### 13.2 Project boundary

Phase 5 should target Redux core or an upstreamable renderer extension. A temporary external-mod prototype is acceptable, but the final architecture should not depend on fragile invasive patches to private presentation internals.

### 13.3 `IRenderUpscaler`

Introduce a general interface adjacent to scene presentation:

```csharp
public interface IRenderUpscaler : IDisposable
{
    string Id { get; }

    bool IsSupported(
        in TemporalCapabilities capabilities,
        out string unsupportedReason);

    UpscalerRecommendation GetRecommendation(
        in UpscalerRequest request);

    void OnOutputChanged(in TemporalOutputDescriptor output);

    void Execute(
        CommandBuffer commandBuffer,
        in TemporalFrameInputs inputs,
        RenderTargetIdentifier output);

    void ResetHistory(HistoryResetReason reason);
}
```

Backends may include:

- native/no scaling
- bilinear scaling
- DLSS SR
- future FSR2

### 13.4 Render-scale integration

For DLSS SR:

1. User selects output resolution and DLSS mode.
2. Query recommended internal render dimensions.
3. Configure scene camera targets to those dimensions.
4. Ensure scene color, depth, and motion vectors share expected render dimensions.
5. Apply jitter appropriate to render/output dimensions.
6. Execute DLSS into native-size output.
7. Prevent a second bilinear upscale by the presentation path.
8. Composite native-resolution UI afterward.

### 13.5 Redux presenter changes

`RenderScalePresenter` or its successor should own a coherent set of scene targets rather than only color:

```text
SceneRenderTargets
├─ color
├─ depth
├─ motion vectors
├─ optional exposure
├─ optional reactive mask
└─ native output
```

The presenter should expose lifecycle events or APIs so external backends do not rely on frame-by-frame reflection.

### 13.6 Quality modes

Initial modes:

- DLSS Quality
- DLSS Balanced
- DLSS Performance

Ultra Performance is optional and should be hidden unless validated at high output resolutions. DLAA remains a separate native-resolution mode.

### 13.7 Texture LOD bias

Rendering below native resolution changes texture sampling requirements. Phase 5 must investigate and apply an appropriate mip bias or equivalent policy, with safeguards for materials that should not be globally modified.

### 13.8 Post-processing order

Document and validate the order of:

- temporal reconstruction
- bloom
- depth of field
- motion blur
- color grading/tonemapping
- sharpening
- UI composition

The correct order depends on whether the input is HDR/pre-exposed and on the vendor API expectations. Do not move all post-processing across the upscaler without visual validation.

### 13.9 Dynamic resolution

Dynamic resolution is optional for the first DLSS release. Start with fixed recommended render dimensions. Add dynamic resolution only after:

- context/API support is verified
- min/max dimensions are respected
- history behavior is stable
- presenter allocation avoids per-frame recreation

### 13.10 Acceptance criteria

- Internal scene resolution matches the selected DLSS recommendation.
- Output matches display resolution.
- UI is rendered at display resolution after reconstruction.
- No additional bilinear scaling occurs.
- Motion-vector scale and jitter are correct at every quality mode.
- Backend survives output-resolution, window-mode, scene, and quality changes.
- Fallback restores a valid native/spatial/temporal path.
- Performance claims are made only in GPU-bound scenes and include CPU/GPU data.
- Image quality is compared against native custom TAA, DLAA, and Redux supersampling.

## 14. Optional extension — FSR2

Once the shared buffer and upscaler architecture is stable, an FSR2 backend may be added for cross-vendor temporal upscaling.

It is not required for the five primary phases, but the architecture must avoid NVIDIA-specific assumptions in:

- scene target ownership
- jitter generation
- reset policy
- frame-input representation
- settings UI
- render-scale presenter APIs

## 15. Configuration specification

### 15.1 Persistent settings

Suggested schema:

```json
{
  "backend": "CustomTAA",
  "fallbackPolicy": "Automatic",
  "qualityPreset": "High",
  "jitterSequenceLength": 8,
  "historyStability": 0.9,
  "motionResponse": 0.75,
  "sharpness": 0.15,
  "reactiveMaskStrength": 1.0,
  "resetSensitivity": "Conservative",
  "debugView": "Off",
  "writeCapabilityReport": true
}
```

Use the Redux configuration system rather than direct ad hoc JSON if the current SDK provides it. The example describes semantics, not required serialization format.

### 15.2 UI behavior

- Unsupported backends are disabled with a reason.
- Backend switch applies at a safe frame boundary.
- Advanced tuning is hidden behind an advanced toggle.
- A reset-to-defaults action is provided.
- Debug views are clearly marked and not persisted into normal play unless intentional.
- Backend/fallback selection is shown in diagnostics.

## 16. Render lifecycle

### 16.1 Initialization

```text
mod pre-initialize
→ register config
→ install lifecycle patches
→ wait for graphics/camera systems
→ discover camera graph
→ probe capabilities
→ select backend/fallback
→ allocate output-dependent resources
```

### 16.2 Per-frame lifecycle

```text
begin scene frame
→ validate camera graph revision
→ aggregate reset reasons
→ generate one jitter sample
→ apply jitter to scene cameras
→ render scaled/physics scene
→ acquire coherent scene inputs
→ execute active temporal backend
→ present scene output
→ render native-resolution UI
→ store previous matrices/frame state
```

Exact Unity callbacks/events are selected by Phase 1 and recorded in the decision document.

### 16.3 Reconfiguration

On backend, render-scale, resolution, format, HDR, or graphics-context change:

```text
disable execution
→ reset/release backend resources
→ rebuild output descriptor
→ recreate coherent scene buffers
→ create backend resources/context
→ force first-frame history reset
→ resume
```

### 16.4 Shutdown

```text
stop frame execution
→ detach command buffers/listeners
→ dispose backend/native context
→ release render textures/materials
→ unpatch or deactivate patches as supported
→ clear cached Unity object references
```

## 17. Visual validation plan

### 17.1 Test scenes

Maintain representative saves/camera scripts where licensing and repository size permit.

Required categories:

- launchpad with detailed vessel
- thin struts/antennae/landing gear
- engine plume and particle-heavy launch
- atmospheric flight with volumetric effects
- low-altitude terrain pan
- orbit with planet limb and stars
- rapid camera rotation
- FOV zoom
- time-warp transition
- vessel switch
- docking/undocking where camera jumps
- flight/map transitions
- VAB outlines and camera movement
- UI and map icons

### 17.2 Capture protocol

For each backend comparison:

- same game/Redux build
- same save and vessel
- same resolution/window mode
- same camera path
- same graphics settings other than AA/upscaling
- record backend configuration
- capture lossless or high-quality video where temporal behavior matters
- include stationary frames only as supplemental evidence

Compare:

- AA off
- existing spatial AA
- Redux supersampling at documented scale
- PPv2 TAA
- custom TAA
- DLAA
- DLSS SR modes

### 17.3 Evaluation dimensions

- static edge smoothness
- temporal shimmer
- thin-geometry persistence
- disocclusion trails
- exhaust/volumetric-effect ghosting
- planet-limb stability
- texture detail retention
- motion softness
- UI/text/icon crispness
- transition recovery
- artifacts at camera-stack boundaries

## 18. Performance validation plan

Record:

- CPU frame time average and percentiles
- GPU frame time average and percentiles
- backend pass GPU duration
- managed allocation per frame after warm-up
- native allocation/context recreation events
- render-target memory estimate
- output recreation time
- frame spikes during transitions

Benchmark at minimum:

- 1920×1080
- 2560×1440
- 3840×2160 where hardware permits

Separate GPU-bound and CPU/physics-bound scenes. DLSS SR performance conclusions are valid only for GPU-bound measurements.

## 19. Reliability requirements

- Backend initialization failure must not crash startup.
- Missing camera/buffer input must disable or fall back for that frame.
- Repeated backend toggles must be safe.
- Repeated resolution changes must be safe.
- Quickload/revert must not reference destroyed textures.
- Shutdown must not call vendor APIs after graphics-device destruction.
- Diagnostics must not expose sensitive user data.
- Release mode must not leave debug command buffers attached.

## 20. Risk register

### R1 — No coherent final motion vectors

**Impact:** Blocks ideal DLAA/DLSS and degrades custom TAA.  
**Mitigation:** Composite per-stack MV, create explicit MV pass, or use synchronized per-stack resolves while pursuing a unified path.

### R2 — Scaled-space depth incompatible with physics-space depth

**Impact:** Incorrect disocclusion and stack-boundary artifacts.  
**Mitigation:** Normalize depth into a common representation or resolve per stack; document precision and far-plane behavior.

### R3 — UI already composited at available hook

**Impact:** UI ghosting and blur.  
**Mitigation:** Move hook earlier, modify presenter, or render scene to explicit project-owned target before UI.

### R4 — NVIDIA Unity module absent from player

**Impact:** Managed DLAA/DLSS unavailable.  
**Mitigation:** Coordinate with Redux maintainers to enable the exact-version module; only then consider native bridge.

### R5 — Floating-origin/camera discontinuities not detected

**Impact:** Large ghost trails or corrupted history.  
**Mitigation:** explicit lifecycle hooks plus matrix/position discontinuity detection and conservative resets.

### R6 — Transparent effects lack useful motion vectors

**Impact:** Plume or transparent-effect ghosting.
**Mitigation:** reactive masks, transparency masks, reduced history, or effect-specific instrumentation.

### R7 — Post-processing order conflicts with reconstruction

**Impact:** incorrect exposure, bloom, blur, or sharpening.  
**Mitigation:** inspect frame order in Phase 1; validate HDR/pre-exposure expectations; document backend-specific order.

### R8 — External mod hooks are too fragile for Phase 5

**Impact:** frequent breakage across Redux updates.  
**Mitigation:** upstream general renderer APIs and `IRenderUpscaler` integration.

### R9 — Vendor binary redistribution restrictions

**Impact:** cannot distribute native backend as planned.  
**Mitigation:** prefer Unity-shipped modules; document licenses before adding binaries; support user-supplied runtime only if legally and technically appropriate.

## 21. Phase deliverables summary

| Phase | Code deliverable | Documentation/evidence | User-visible result |
|---|---|---|---|
| 1 | Render probe, buffer visualizers, capability report | Camera graphs, captures, architecture decision | Diagnostic only |
| 2 | PPv2 TAA backend, shared jitter, reset tracker | Failure-case list, before/after captures | First usable TAA |
| 3 | Custom C# + HLSL TAA backend | Algorithm notes, debug views, benchmarks | Better cross-vendor TAA |
| 4 | Managed NVIDIA DLAA backend | Capability/fallback matrix, DLAA comparisons | High-quality NVIDIA native AA |
| 5 | General upscaler/presenter integration, DLSS SR | Upstream design, performance and quality matrix | Lower internal resolution with reconstructed output |

## 22. Release strategy

### Experimental builds

- Capability probe enabled.
- Detailed logging.
- Debug overlays available.
- Backend explicitly marked experimental.
- No automatic selection of unvalidated vendor backend.

### Beta builds

- Phase gate met in required scenes.
- Config migration supported.
- Automatic fallback verified.
- Debug features off by default.
- Known limitations published.

### Stable builds

- No recurring renderer exceptions.
- No steady-state allocations.
- Resource lifecycle validated through long sessions and repeated transitions.
- Visual regression suite reviewed.
- Vendor licensing/distribution requirements satisfied.
- Compatibility range documented.

## 23. Open questions

Phase 1 must answer:

1. What exact camera/render event contains final scene color before UI?
2. Do final depth and motion-vector textures cover scaled and physics space?
3. What are motion-vector direction, scale, resolution, and Y-axis conventions?
4. Does Redux's presenter preserve or discard depth/MV during scene composition?
5. Which cameras currently own PPv2 TAA/AA settings?
6. How are map icons and world-space markers composed?
7. Which lifecycle events correspond to origin rebases, camera cuts, quickload, revert, and vessel switches?
8. Is `UnityEngine.NVIDIAModule` present in the shipped Redux player?
9. Can the managed NVIDIA plugin load and report DLSS availability?
10. Which graphics APIs are supported by the target Redux build in practice?
11. Where should exposure/pre-exposure be sourced for DLAA/DLSS?
12. Which post-processing effects execute before and after the presenter?

Phase 5 must additionally answer:

13. Can `RenderScalePresenter` be extended upstream to own color/depth/MV targets?
14. What texture LOD bias policy is appropriate at each DLSS mode?
15. Is fixed resolution sufficient for the first release, or is dynamic resolution required?

## 24. References

Primary project references:

- [KSP2 Redux repository](https://github.com/KSP2Redux/Redux)
- [Redux template](https://github.com/KSP2Redux/Redux.Template)
- [Redux API documentation](https://github.com/KSP2Redux/API-Documentation)
- [Unity Post Processing Stack v2](https://github.com/Unity-Technologies/PostProcessing/tree/v2)
- [Unity NVIDIA GraphicsDevice API](https://docs.unity3d.com/ScriptReference/NVIDIA.GraphicsDevice.html)
- [Unity NVIDIA DLSSTextureTable API](https://docs.unity3d.com/ScriptReference/NVIDIA.DLSSTextureTable.html)
- [NVIDIA Streamline](https://github.com/NVIDIA-RTX/Streamline)

When implementation begins, pin links to exact commits/package versions in decision records and build configuration.
