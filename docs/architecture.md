# Architecture and rendering fixes

This describes the current source, including changes since the published v0.6.1
binary. Keep it current when a design changes; Git records earlier choices.
[Release notes](releases/v0.6.1.md) describe the shipped beta, and
[standard tests](../tests/README.md) define release validation.

## Modes and ownership

Better AA owns scene anti-aliasing while loaded. It disables MSAA and redirects
the stock AA and render-scale controls to the mod settings so two systems cannot
apply conflicting filters. Off actively disables scene AA; it is also the safe
fallback when a backend cannot run. It does not restore the stock AA selection
until Better AA releases ownership.

| Mode | Implementation |
| --- | --- |
| FXAA Low / High | Redux's PPv2 FXAA in fast / quality mode. |
| SMAA | Redux's PPv2 SMAA at High quality. |
| TAA | The mod's temporal resolve, described below. |
| NVIDIA DLAA | Unity's NVIDIA bridge at equal input/output resolution; RTX support and matching runtimes required. |
| FSR Native AA | Pinned AMD FSR SDK through the project bridge, equal input/output resolution; reports its active provider. |
| Supersampling | Redux's render-scale presenter at 125–200% per dimension, bounded by texture limits. |

All other modes use 100% scene resolution. Supersampling preserves native UI
and falls back to Off in map and menu scenes. Better AA uses Redux's presenter
instead of replacing presentation or pointer-coordinate handling. If another
owner changes render scale, the player must explicitly reselect a mode to reclaim it.

The old PPv2 TAA backend is removed. Its saved name and numeric ID 4 migrate to
custom TAA; the other numeric IDs remain stable. Redux's PPv2 library is still
needed for spatial AA, exposure and reversible post-process state. No separate
PPv2 source package or type-scan patch is needed.

Normal settings and F10 use the same mode policy and persistence callbacks,
including the conversion between saved labels and backend IDs. Settings callbacks
queue normalization until Update, after Redux UI callbacks unwind, preventing
recursive replay of stale values. Provider-specific native-AA labels migrate to
the available provider; removed experimental labels resolve to Off. New installs
default to custom TAA; saved Off selections and invalid/unavailable-mode fallbacks
remain Off. Legacy temporal labels and numeric ID 4 migrate to custom TAA.
Sharpness is shared by temporal modes; TAA stability controls stationary history.
When DLAA is selected, entering the main menu resets its preset to K for that
visit. Menu preset edits preserve the saved gameplay preset; other controls keep
their normal persistence. Map AA and foliage repair have separate switches.

Sources: [mod entry point](../Assets/ReduxBetterAA/Code/ReduxBetterAAMod.cs),
[settings policy](../Assets/ReduxBetterAA/Code/Configuration/UserSettingsPolicy.cs),
[graphics patches](../Assets/ReduxBetterAA/Code/Patches/GraphicsSettingsPatches.cs),
[render-scale ownership](../Assets/ReduxBetterAA/Code/Rendering/RenderScaleOwnership.cs).

## Frame flow and lifetime

1. `TemporalCoordinator` discovers the scene cameras, acquires their state and
   activates one backend. Recognized scenes include flight, KSC, VAB, map and menu.
2. Temporal backends use one shared jitter sample per rendered frame. Color,
   depth, motion vectors, projection and history-reset state must describe that
   same frame. The early terrain draw uses the upcoming sample without advancing it.
3. Custom/vendor AA resolves scene color before UI composition through
   `TemporalRenderHook`. Map sprites are replayed after AA with unjittered matrices.
4. Projection state is restored after rendering. Backend resources and scene
   claims are released on mode changes, teardown and shutdown.

Flight/KSC use the physics resolve camera and scaled-space predecessor. Main
menu support verifies `Camera.Scaled` and a preceding `Skybox` sharing its target
and viewport. Map uses the known `MapCamera`. Camera names alone do not establish
a safe render order or justify adding jitter to an unknown graph.

`SceneCameraState` owns depth/motion flags and PPv2 settings for a backend's
lifetime; `CameraProjectionState` owns projection and transparent-jitter fields
for a render. Restore a value only if it still equals what Better AA applied.
Cleanup must be idempotent, including partial activation failure.

Both the camera render and early terrain-depth scope share `ProjectionOverride`.
It records Unity's projection mode as well as the matrix: automatic/physical
projections release through `ResetProjectionMatrix`, so altitude-dependent clip
distances, FOV and aspect keep updating after a draw and after switching to Off.
Explicit projections restore their exact matrix, even when it equals an automatic
projection. A cached read-only mode delegate avoids per-draw reflection/boxing;
temporal modes decline activation if that Unity API cannot be bound. Cleanup only
restores a projection still owned by the scope, preserving external changes.
The terrain and main passes retain their shared jitter sample and the existing
transparent-jitter policy for coastlines, map and menu rendering.

History resets on camera/scene changes, significant projection or output changes,
quickload/revert, vessel changes and floating-origin snaps. Ordinary incremental
zoom and fast pans retain history. Origin rebasing resets accumulation while
keeping the camera graph and vendor contexts. Lost GPU storage invalidates
history even if texture dimensions have not changed.

Backends own their history/output textures and native contexts. Recreate them
when storage is lost or immutable context settings change. Cache resources,
reflection and lookups; do not allocate managed memory in steady-state AA
callbacks. Discovery and failed acquisition use bounded retries, not scene scans
on every draw. Failures retain a diagnostic reason and fall back to Off.

Sources: [coordinator](../Assets/ReduxBetterAA/Code/Rendering/TemporalCoordinator.cs),
[camera discovery](../Assets/ReduxBetterAA/Code/Rendering/TemporalCameraDiscovery.cs),
[state ownership](../Assets/ReduxBetterAA/Code/Rendering/SceneCameraState.cs),
[reset policy](../Assets/ReduxBetterAA/Code/Rendering/HistoryResetTracker.cs).

## Game rendering fixes

**Terrain depth and jitter.** `PQSRenderer.DrawPqsDepthNow` draws terrain depth
before ordinary camera rendering. Its projection previously differed from the
jittered color pass, producing coherent terrain flicker under temporal AA.
`TerrainDepthJitterPatch` temporarily applies the upcoming raster projection to
that draw and restores it in a Harmony finalizer, including exceptions. It
changes only `camera.projectionMatrix`: no extra draw, terrain shader replacement,
physics change or reduction in temporal sample coverage.

**Terrain motion.** `TerrainMotionCompatibility` observes the existing PQS
generation, depth and color submissions on the inspected Unity 6000.5.8f1
renderer. Consecutive matching frames provide a previous-world transform. The
motion sanitizer repairs only depth-matched terrain, preserving foreground vessel
vectors. Missing/ambiguous observations, origin rebases and lifecycle resets
disable the repair until coherent history is available. No additional terrain
draw or physics change is made.

**Distant water/coastline flicker.** The recognized flight/KSC physics and scaled
camera stack uses coherent transparent as well as opaque jitter. Validation
requires active cameras, matching targets and viewports, the known depth-clear
resolve and render order. Unrecognized stacks retain ordinary behavior. This
correction applies to all temporal AA modes without a new setting or pass.

**Menu and map planet flicker.** Recognized menu and map cameras apply matching
opaque and transparent jitter. Jittering only opaque rendering left overlapping
planet passes misaligned. Keeping them coherent permits jittered AA in both
scenes. The published v0.6.1 binary predates restored map jitter.

**Map icons.** Map ship icons are scene `SpriteRenderer`s, so ordinary temporal
AA would accumulate them as geometry. `MapIconOverlay` claims known map sprites
on layer 27 using `KSP2/UI/Sprite (Depth Offset)`, suppresses their normal draw,
and replays their original materials at `AfterImageEffects` with unjittered
matrices and camera depth. Visibility, sorting and distance stacking are retained.
New icons register through the map-icon patch; claims restore after rendering
and teardown. This applies to spatial AA too.

**Foliage motion.** The compatible direct vegetation branch is rerouted through
`Graphics.RenderMeshIndirect` with camera-only motion; command-buffer draws stay
with Redux. The repair shader discards the specific invalid object-motion pass
with an identity previous transform and camera-centred current translation,
leaving valid fullscreen camera motion underneath. Unity-version/signature and
shader-ownership checks bound this repair. It defaults on for active AA except
supersampling, and turns off with AA Off. This is separate from the terrain fix.

Sources: [terrain patch](../Assets/ReduxBetterAA/Code/Patches/TerrainDepthJitterPatch.cs),
[projection scope](../Assets/ReduxBetterAA/Code/Rendering/AuxiliaryProjectionScope.cs),
[map overlay](../Assets/ReduxBetterAA/Code/Rendering/MapIconOverlay.cs),
[foliage compatibility](../Assets/ReduxBetterAA/Code/Rendering/VegetationMotionCompatibility.cs),
[foliage shader](../Assets/ReduxBetterAA/Shaders/VegetationMotionVectorRepair.shader).

## Temporal reconstruction and vendor inputs

Custom TAA keeps separate ping-pong color/depth histories. It aligns current
sampling with jitter, selects motion from the nearest surface, reconstructs
current color and cubic history, clips history against a YCoCg neighborhood,
and weights it by motion, depth agreement and reactive change. Camera reprojection
provides a fallback for unusable vectors. Reset frames avoid uninitialized history;
sharpening affects output, never accumulated color. Depth-edge handling protects
thin moving geometry without giving every pixel the history of a nearby surface.

DLAA uses the late-bound Unity NVIDIA module. AMD native AA uses the pinned
FSR SDK through the project-owned D3D11/D3D12 interop bridge. The managed backend
requires equal input/output dimensions and 100% render scale. Provider probes
report FSR 3.1 or FSR 4; unavailable runtimes fall back to Off. The bridge preserves
PPv2 exposure or vendor-auto/manual fallback and defers native resource retirement
until GPU completion. See [the native bridge](../Native/README.md).
Both require matching color/depth/motion dimensions, device
depth direction and explicit motion signs. Dispatch jitter is the negative of
raster jitter; normalized motion is scaled to pixels once.
FSR uses unscaled frame time in normal play and Unity's fixed capture timestep
when offline recording is active, so image-encoding delays do not age its history.
Vendor output stays linear and random-write capable. The pre-dispatch blit
initializes the output and establishes its GPU resource transition; retain it.

`MotionVectorSanitizer` always provides the required component-sign conversion.
Its optional outlier rejection is **off by default**. When enabled, it can reject
invalid motion and substitute depth-derived camera reprojection; this is not the
terrain fix. `DepthDisocclusionMask` biases vendor history at moving solid edges
and depth breaks while leaving broad no-depth regions unmarked for transparent
and volumetric accumulation. A missing mask shader is nonfatal.

`Ppv2ExposureReader` caches access to Redux's existing 1×1 auto-exposure result
and reads it asynchronously, no more often than every 0.1 seconds. Vendor
auto-exposure or manual pre-exposure remain fallbacks. Output-only sharpening
changes keep history; temporal-input changes reset it, and immutable vendor
settings recreate the native context.

Sources: [TAA shader](../Assets/ReduxBetterAA/Shaders/CustomTaa.shader),
[backends](../Assets/ReduxBetterAA/Code/Backends),
[motion conversion](../Assets/ReduxBetterAA/Code/Rendering/MotionVectorSanitizer.cs),
[edge mask](../Assets/ReduxBetterAA/Shaders/DepthDisocclusionMask.shader),
[exposure reader](../Assets/ReduxBetterAA/Code/Rendering/Ppv2ExposureReader.cs).

## Diagnostics and distribution

F10 controls, buffer views, motion statistics, performance profiles and Issue
ZIPs are supported features. AA mode selection uses the settings menu or F10;
there is no mode-cycle hotkey. Reports identify capture stage/frame, selected and
active backend, settings, runtime versions and file hashes. Capture failures
leave normal rendering alive and mark partial results. Nothing uploads automatically.
Motion telemetry uses the report's capture camera, independent of the selected
debug view. Closing a debug view restores its camera depth flags only while
they still match the value the view applied.
Live A/B suspends normal AA ownership and renders two independent arms; use
ordinary mode selection to judge terrain stability or performance.

Builds use Unity's player-script compiler and pinned Addressables. Bundle
identities include the mod's group/project/entry identity to avoid collisions
with other mods. The internal managed ZIP contains only its assembly, manifest, shader bundle,
catalog and license files. Unity players reject bundles built by a newer editor,
so each supported Redux version has its own managed component, built from the
same commit by the editor matching that player and compiled against that Redux
installation. Public release ZIPs combine each component with the matching engine
NVIDIA libraries and the engine-independent AMD native-AA bridge/runtime. Each
Redux version has one complete download. Vendor bytes are pinned and original
notices are retained. The full release manifest covers every payload path/hash;
validators reject engine swaps (including a bundle from another editor), extra
binaries and malformed archives. Internal component ZIPs are never public release assets.
Build and release instructions live in [CONTRIBUTING.md](../CONTRIBUTING.md).
The candidate test pipeline clones the mod and external harness into a disposable
workspace, installs Redux into a clean game copy, and builds complete local release
packages. It installs the complete ZIP, temporarily withholds its manifest-listed
native libraries to test fallback, then reinstalls the complete ZIP to test vendor
modes with an isolated
profile and optional online services disabled. Launcher/profile state is restored
and exact inputs are recorded. Local
release preparation needs no GitHub access; publishing is an explicit separate step.

Upscaling and frame generation are preserved exclusively on the
[investigation branch](https://github.com/Michionlion/ReduxBetterAA/tree/investigate/upscaling-frame-generation).
The public AA build has no FG settings, player-loop hooks, resolved-frame capture,
native presentation companion or reduced-resolution scene-output ownership.
Supersampling retains its existing Redux presenter contract.

Better AA does not interpolate vessel motion, change physics, edit installed game assemblies,
control cloud rendering, upscale with DLSS, generate frames or reconstruct rays.
Temporal quality and performance claims require moving in-game evidence on the
stated setup; screenshots and unit tests alone do not establish either.
