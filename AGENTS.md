# AGENTS.md

Instructions for coding agents and automated contributors working on **Redux Better AA**.

This file is normative for repository work. Human maintainers may override it in an issue or pull request, but agents must not silently ignore it.

## Read order

Before editing code, read:

1. `README.md`
2. `SPEC.md`
3. This file
4. Any decision records in `docs/decisions/`
5. The current issue or task description

When documents conflict, the current task and explicit maintainer instructions take precedence, followed by `SPEC.md`, then this file, then `README.md`.

## Mission

Build a safe, measurable, backend-neutral temporal reconstruction system for KSP2 Redux. The project must progress from renderer discovery to PPv2 TAA, custom TAA, DLAA, and DLSS Super Resolution without prematurely coupling the renderer to a vendor API.

The central engineering objective is to establish and preserve a coherent set of scene inputs:

```text
color + depth + motion vectors + jitter + matrices + reset state
```

across Redux's scaled-space and physics-space rendering paths.

## Non-negotiable rules

### Work only in the active phase

Do not implement a later phase before the prior phase's exit gate is satisfied and documented.

- Phase 1 may observe and visualize rendering but must not replace the production resolve.
- Phase 2 may use PPv2 TAA but must not introduce a native bridge.
- Phase 3 may add custom HLSL temporal reconstruction.
- Phase 4 may add DLAA only after Phase 1 confirms the required buffers and runtime module status.
- Phase 5 may alter render scale and presentation only after DLAA/custom TAA infrastructure is stable.

A prototype behind an explicitly disabled experimental flag is acceptable when it is needed to answer a phase question. It must not become the default path.

### One active temporal backend

Never run more than one temporal reconstruction backend on the same scene output. PPv2 TAA, custom TAA, DLAA, and DLSS are mutually exclusive.

Spatial AA may only coexist when the resulting order is intentional, documented, and visually justified. Default behavior should avoid double filtering.

### Keep UI out of temporal history

Do not include native-resolution UI, text, menus, map icons, or overlays in scene temporal history. Temporal passes must execute before UI composition unless a narrowly scoped exception is documented.

### Runtime observation beats assumption

Do not infer the final camera, buffer ownership, or render event solely from type names or standard Unity behavior. Use Phase 1 logs, Frame Debugger, RenderDoc, and runtime texture inspection.

### No per-frame garbage

Production rendering paths must allocate zero managed memory per frame after warm-up.

Avoid in hot paths:

- LINQ.
- String formatting.
- New arrays, lists, delegates, closures, or boxed values.
- Repeated reflection.
- `FindObjectOfType` or hierarchy searches.
- Recreating command buffers, materials, or render textures.

Cache references and use preallocated storage.

### Own and release resources

Every created resource must have a clear owner and deterministic release path:

- `RenderTexture`
- `CommandBuffer`
- `Material`
- `ComputeBuffer`
- event subscription
- Harmony patch group
- native DLSS/DLAA/FSR context
- native plugin/device handle

Release on backend switch, resolution change where required, scene teardown, mod shutdown, and game shutdown. Cleanup must be idempotent.

### Do not patch files on disk

Use supported Redux mod APIs, runtime Harmony patches, and the Redux build pipeline. Do not edit or replace `Assembly-CSharp.dll` or other game files as the normal implementation strategy.

### Do not ship proprietary binaries casually

Do not add NVIDIA, AMD, or other proprietary redistributables until:

1. The managed in-player path has been tested and found insufficient.
2. The required files are identified exactly.
3. Licensing and redistribution terms are documented.
4. A maintainer approves the addition.

## Source hierarchy

Prefer these sources, in order:

1. Current Redux source and API documentation matching the target build.
2. Current Redux template/SDK source.
3. Unity documentation for the exact editor/player version.
4. Unity package source matching the installed package revision.
5. NVIDIA/AMD primary integration documentation.
6. Runtime captures and logs from the target build.

Do not copy integration code from unrelated games without validating API version, render pipeline, resource states, coordinate conventions, and licensing.

## Repository boundaries

### Rendering core

`Assets/ReduxBetterAA/Code/Rendering/` owns:

- camera discovery and graph representation
- final scene-buffer access
- temporal frame data
- jitter coordination
- history-reset detection
- render lifecycle integration

This layer must not depend directly on NVIDIA or AMD types.

### Backends

`Assets/ReduxBetterAA/Code/Backends/` owns:

- PPv2 TAA control
- custom TAA execution
- DLAA execution
- DLSS execution
- disabled/fallback behavior

Vendor-specific assemblies and reflection are isolated here or in a vendor subfolder.

### Diagnostics

`Assets/ReduxBetterAA/Code/Diagnostics/` owns:

- capability reports
- debug overlays
- buffer visualizers
- one-shot capture metadata

Diagnostics may observe all layers but must not become a required dependency of production execution.

### Patches

`Assets/ReduxBetterAA/Code/Patches/` owns Harmony patches. Patches should call stable project services rather than contain rendering logic.

A patch should be:

- minimal
- guarded
- reversible where possible
- logged once on installation/failure
- version-sensitive only when necessary

### Shaders

`Assets/ReduxBetterAA/Shaders/` owns project shaders. Keep algorithm constants exposed through named parameters rather than magic numbers embedded across passes.

## Required architecture contracts

### `CameraGraph`

Represents the discovered scene renderer without assuming a single camera. At minimum, track:

- scaled-space main camera
- physics-space main camera
- all scene cameras in render order
- presentation/final camera
- UI cameras
- active render-scale presenter
- scene-output dimensions
- target textures
- post-process layers

The graph is rebuilt or invalidated on scene/camera lifecycle changes, not every frame.

### `TemporalCoordinator`

Owns:

- active backend
- shared jitter sequence
- frame index
- current/previous matrices
- output descriptor
- reset propagation
- backend switch lifecycle

No backend may independently advance the global camera jitter when multiple cameras contribute to one output.

### `HistoryResetTracker`

Produces explicit reset reasons, not a single opaque boolean. Use an enum such as:

```csharp
[Flags]
public enum HistoryResetReason
{
    None = 0,
    FirstFrame = 1 << 0,
    BackendChanged = 1 << 1,
    SceneChanged = 1 << 2,
    CameraCut = 1 << 3,
    ProjectionChanged = 1 << 4,
    ResolutionChanged = 1 << 5,
    RenderScaleChanged = 1 << 6,
    QuickloadOrRevert = 1 << 7,
    VesselChanged = 1 << 8,
    OriginRebased = 1 << 9,
    Teleport = 1 << 10,
    InvalidInput = 1 << 11
}
```

Log reset reasons in diagnostics, but do not format strings every frame.

### `ITemporalBackend`

Backends must implement a common lifecycle:

```text
construct
→ probe support
→ configure
→ create resources for output
→ execute per frame
→ reset history as needed
→ recreate on immutable output change
→ dispose
```

Backends must never assume they are supported merely because their assembly loads.

## Phase discipline

### Phase 1 permitted work

- Camera and render-target discovery.
- Runtime module discovery.
- Debug shaders.
- Capability-report serialization.
- RenderDoc/Frame Debugger instructions.
- No-op render hooks.

Required evidence before exit:

- Camera graph for flight, map, and VAB.
- Depth visualization.
- Motion-vector visualization.
- Identification of the final scene color before UI.
- NVIDIA module/API/feature status.
- Written integration decision in `docs/decisions/`.

### Phase 2 permitted work

- Enable/disable PPv2 TAA.
- Coordinate jitter across camera stacks.
- Reset PPv2 histories through supported or reflected APIs.
- Add settings and fallbacks.
- Fix lifecycle errors discovered in representative scenes.

Do not fork the PPv2 shader in this phase unless a blocker prevents evaluation.

### Phase 3 permitted work

- Persistent project-owned history textures.
- Custom HLSL/compute temporal resolve.
- Reactive/transparency mask generation.
- Quality presets and sharpening.
- Algorithm-focused tests and benchmark captures.

Keep PPv2 TAA available as a comparison/fallback until custom TAA passes the acceptance gate.

### Phase 4 permitted work

- Managed `UnityEngine.NVIDIA` capability detection.
- Managed DLAA context lifecycle.
- Optional reflection wrapper to keep assembly absence non-fatal.
- Native bridge investigation only in an isolated branch after maintainer approval.

Do not add DLSS Super Resolution render-scale control in this phase.

### Phase 5 permitted work

- General upscaler abstraction.
- Internal render-size recommendation and allocation.
- Integration with `RenderScalePresenter` or its replacement.
- DLSS SR quality modes.
- Native-resolution UI composition.
- Upstream-ready Redux patches.

Do not include Frame Generation or Ray Reconstruction.

## Work loop for agents

For each task:

1. State the phase and requirement being addressed in the PR/commit description.
2. Inspect existing implementation and relevant decision records.
3. Identify the smallest change that advances the phase gate.
4. Implement without broad unrelated refactors.
5. Add tests or a reproducible manual verification procedure.
6. Run build/static checks available in the repository.
7. Inspect logs for warnings, exceptions, and resource leaks.
8. Record visual or capability evidence where the change affects rendering.
9. Update documentation when behavior, architecture, or phase status changes.

## Coding standards

### C#

- Follow the C# language level supported by the pinned Unity version.
- Use nullable annotations only if the template enables them consistently.
- Prefer explicit lifecycle methods over finalizers.
- Use `readonly` fields where ownership is stable.
- Avoid public mutable fields except where Unity serialization requires them.
- Keep Unity object null semantics in mind; do not rely only on CLR reference checks.
- Cache shader property IDs with `Shader.PropertyToID`.
- Use structured log prefixes, for example `[ReduxBetterAA/Probe]`.
- Log initialization, backend selection, fallback, output recreation, and fatal disablement.
- Rate-limit recurring warnings.

### HLSL / ShaderLab

- Document texture coordinate conventions.
- Document motion-vector direction and scale.
- Handle reversed Z and platform UV orientation explicitly.
- Avoid sampling outside valid history extents.
- Validate NaN/Inf behavior.
- Keep debug visualization passes separate from production resolve passes.
- Expose algorithm constants through a constant buffer/material properties.
- Add comments for non-obvious math, not line-by-line narration.

### Native code

Native code is optional and exceptional.

- Use an explicit C ABI for P/Invoke.
- Never pass managed Unity objects directly to native code.
- Define resource ownership and graphics API support.
- Validate D3D11/D3D12 assumptions against the actual Redux player.
- Ensure shutdown occurs before the graphics device is destroyed.
- Restore command-list state when required by the vendor SDK.
- Provide a no-native build configuration.

## Render safety checklist

Before merging any render-hook change, verify:

- The hook runs at the intended camera event.
- Scene color is complete at the hook point.
- UI has not yet been rendered.
- Depth corresponds to scene color.
- Motion vectors correspond to scene color and use documented scale/direction.
- Jitter is applied exactly once.
- Current and previous matrices exclude/include jitter as required by the backend.
- The output is not accidentally filtered again by the presentation path.
- History resets on all tested discontinuities.
- Resources are recreated on dimension/format changes.
- Disabled mode restores the original renderer state.

## Testing requirements

### Automated tests

Where practical, add tests for:

- Halton sequence determinism.
- Jitter normalization.
- camera-cut thresholds.
- history-reset reason aggregation.
- output descriptor equality/change detection.
- capability fallback ordering.
- resource-lifecycle idempotence.
- configuration migration.

Shader correctness still requires runtime image tests.

### Manual visual tests

Each rendering PR must list:

- game/Redux build
- mod commit
- GPU and driver
- resolution and render scale
- backend and settings
- scene/save used
- reproduction steps
- observed result

Required scenes are defined in `SPEC.md`.

### Performance tests

At minimum record:

- average and percentile CPU frame time
- GPU frame time where available
- backend pass time
- managed allocations after warm-up
- render-target memory estimate
- history recreation spikes

Do not claim a performance improvement from DLSS unless the tested scene is GPU-bound.

## Logging and diagnostics

The capability report should be machine-readable and include:

- project/mod version
- Redux and game version
- Unity version
- graphics API
- GPU name/vendor/device ID where available
- active camera graph
- render target formats/dimensions
- depth/motion-vector support
- post-process AA state
- render-scale presenter state
- NVIDIA module/API/feature status
- selected backend and fallback reason

Do not include personal data, save contents, or filesystem paths beyond what is necessary for debugging.

## Dependency rules

- Pin dependencies used by the build.
- Avoid adding a package for functionality available in Unity or the Redux SDK.
- Do not upgrade the Redux SDK, template, Unity editor, or rendering packages as part of an unrelated feature.
- Vendor SDK updates require a separate compatibility review.
- Keep optional vendor dependencies out of the core assembly where possible.

## Pull request requirements

A rendering PR must include:

- phase and specification requirement IDs
- architectural impact
- lifecycle/resource impact
- test evidence
- screenshots or short captures for visual changes
- benchmark data for performance claims
- fallback behavior
- known limitations
- documentation changes

Do not merge a visually significant algorithm change based on a single stationary screenshot.

## When blocked

Do not guess around missing renderer information.

Instead:

1. Add or extend a diagnostic.
2. Capture the relevant frame.
3. Record the unknown in `SPEC.md` or a decision record.
4. Implement the safest fallback.
5. Stop before adding vendor-specific complexity that does not resolve the unknown.

Examples:

- If final motion vectors are invalid, do not proceed to DLAA; determine whether per-stack resolves or a new motion-vector pass is required.
- If the NVIDIA module assembly exists but feature initialization fails, log exact capability/error information and fall back.
- If UI is already present in scene color at the only available hook, find or create an earlier hook rather than accepting UI ghosting.

## Definition of done

A task is done only when:

- the requested behavior is implemented
- the project builds through the supported pipeline
- no new recurring exceptions or warnings are introduced
- resources are released correctly
- steady-state allocation requirements are met for hot paths
- phase-specific verification is documented
- relevant docs are updated
- unsupported configurations fail safely
