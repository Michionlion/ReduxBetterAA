# Phase 1 runtime probe

## Scope

This diagnostic implementation addresses SPEC Section 9.2. It observes cameras
and Redux render objects, writes reports only after stable lifecycle changes or
an explicit request, and attaches a command buffer only while an operator has
selected a debug view. Phase 2 now adds a separate, disabled-by-default PPv2
backend; the diagnostics remain usable with that backend Off.

Implemented components:

- Redux `MonoBehaviourMod` lifecycle entry and one Harmony patch group.
- Scene, game-state, camera, resolution, and `RenderScalePresenter` invalidation.
- Debounced camera graph capture; no hierarchy scan occurs every frame.
- Scaled-space, physics-space, map, OAB/VAB, presenter, UI-candidate, target,
  post-processing, and camera-command-buffer inventory.
- Final color, linear depth, three motion-vector views, and per-camera depth
  contribution visualization in one project-owned shader.
- Cached NVIDIA and AMD managed-module/runtime probes. No vendor feature or
  native context is created.
- Indented JSON reports next to the installed mod assembly under `diagnostics/`.

The diagnostic pass is off by default. It never replaces the production
resolve.

## Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F8` | Write a report immediately. |
| `Ctrl+F10` | Open or close the diagnostic control panel. |
| `F10` | Write a same-moment report, then capture the current screen or active debug view; identical to the panel capture button. |
| `F12` | Cycle Off, FXAA Low, FXAA High, SMAA, TAA, and the vendor modes supported by the active hardware/runtime. DLAA is ordered before FSR2 when both are available. |

The panel has Off, FXAA Low, FXAA High, SMAA, PPv2, Custom, DLAA, FSR2 AA, and Buffers tabs. The Buffers tab
directly selects Off, final color, raw-jittered or output-aligned linear depth,
raw motion, normalized motion,
motion magnitude/angle, combined motion validity/magnitude, raw sign agreement,
camera-reference orientation audit, sanitized vendor input, sanitizer decision,
contribution mask, or other AA
depth/motion diagnostics, and directly selects any enabled game camera. The AA
tabs own their respective runtime controls. Every tab can
write reports and capture screenshots.

Version 0.5.17 keeps that full Ctrl+F10 surface for comparison and diagnostics and
adds the stock FXAA Low/High variants plus PPv2 SMAA as selectable spatial
backends. The normal Redux settings page now includes those modes before TAA;
the exact stock High setting is FXAA quality, while SMAA is exposed separately
using PPv2's shipped High-quality preset.

Version 0.5.16 keeps that full Ctrl+F10 surface for comparison and diagnostics, but
the normal Redux settings page has only Mode, Sharpness, TAA stability, and
DLAA preset. Its public Mode list calls the project backend `TAA`, never exposes
PPv2, and omits DLAA unless the active GPU is NVIDIA and Unity's runtime reports
the DLSS/DLAA feature available. KSP's separate MSAA control is disabled and
contains the navigation hint `Settings > Mods > Redux Better AA`; MSAA is forced
off while the mod owns scene AA.
The cursor is unlocked while the panel is open and its prior state is restored
when the panel closes. KSP's UI event system is temporarily suppressed so panel
clicks cannot activate controls behind it, and its prior enabled state is
restored on close or shutdown. A screenshot capture hides the panel for the
captured frame, saves a PNG under `diagnostics/screenshots/`, and then reopens
the panel. When the combined motion validity/magnitude view is active, either
capture control also writes a sibling `-motion-stats.json` file from a uniform
point-sampled GPU readback of the same selected-camera render. The report
separates depth-covered and no-depth pixels and records moving/outlier counts,
signed component ranges, and magnitude mean/P50/P95/P99/maximum in pixels.
Schema 3 also records raw X/Y motion, depth, and validity at the 16 fixed
classifier anchors.

Version 0.5.14 treats the main menu as a two-camera scene: `Skybox` contributes
background color first and `Camera.Scaled` preserves that color while clearing
depth. Both cameras now receive the same temporal jitter and the resolve still
runs once on `Camera.Scaled`, before Flow/UI. Schema 16 records the shared-jitter
camera plus every camera's near/far clip, field of view, and projection mode.

Version 0.5.15 corrects an over-strong interpretation of the paired depth
diagnostic. `Linear Depth (raw jittered)` shows the single-sample raster depth.
`Linear Depth (jitter-compensated sample)` shifts the point-sample coordinate
back into output space, but cannot reconstruct geometric coverage that was not
rasterized. A hard edge may therefore toggle in both views without any camera
or geometry shake. To isolate upstream stability, select AA Off and inspect raw
depth, which removes temporal projection jitter. Schema 17 also records the
FSR2 projection-helper and dispatch jitter values; they must be equal in
magnitude and opposite in sign on both axes. The stationary launchpad control
was stable with AA Off apart from occasional isolated pixel flicker, supporting
raster coverage rather than continuous upstream camera motion as the cause.

Version 0.5.13 added those paired depth diagnostics and schema 15 jitter
telemetry beside the sanitizer matrices. Its output-aligned view remains useful
for inspecting sampling coordinates, but not as a definitive silhouette
stability test.

Version 0.5.12 moves this readback into a dedicated one-pass addressable shader.
Managed reads of Unity's internal motion matrices returned identity in 0.5.11;
schema 14 marks those values unavailable instead of reporting them as valid.
The Buffers tab also has a one-click motion-diagnosis burst. It closes the panel,
waits one second for the operator to begin a smooth horizontal then vertical
pan, and captures raw, normalized, validity, raw-sign-agreement, sanitized-input,
and sanitizer-decision frames with matching reports at fixed intervals.

Version 0.5.22 tested separate screen-wide and local disagreement policies, but
player validation found the adaptive 8-pixel or 10%-of-camera-motion classifier
too sensitive. Version 0.5.23 restores the validated 96-pixel envelope for both
the 16-anchor classifier and full-resolution local replacement. The raw sign
view still requires 0.75 pixel on the tested component, so an axis zero-crossing
is undecided dark blue rather than a misleading view-angle-dependent red result.
Sanitizer views also render dark blue when the selected Off or PPv2 path has no
live sanitizer texture; red can no longer mean an unavailable input.

Version 0.5.24 makes sign diagnosis a two-step check. `Motion: Sign Reference
Orientation Audit` repeats the scene in four quadrants during a vertical pan:
left/right compare screen and render-texture GPU projections, while upper/lower
compare Unity's explicit top-origin Y conversion with a no-conversion control.
The quadrant matching schema 19's
`automaticReferenceUsesRenderTextureProjection` must be coherently green in the
upper row over static, depth-covered terrain. Its lower control in the same
projection column should be red. The diagonally opposite quadrant may be green
because changing projection orientation and omitting the explicit Y conversion
can cancel. Only after the automatic reference passes should `Motion: Raw Sign
Agreement` be used to judge the configured vendor component flips. The latter
remains split X on the left and Y on the right.

The sign views sample `_CameraMotionVectorsTexture` and `_CameraDepthTexture`
using their own texel-size orientation instead of `_MainTex_TexelSize`. Their
camera reference chooses a render-texture GPU projection when the selected
camera has a target or `forceIntoRenderTexture` is true, reconstructs in
projection UV, and mirrors the top-origin Y conversion in Unity's built-in
motion-vector shader. Schema 19 records those inputs alongside the configured
backend and explicit X/Y texture flips.

Version 0.5.11 retains the state-specific camera discovery and adds same-frame
launchpad motion classification. `KerbalSpaceCenter` uses the observed
scaled/physics flight camera graph; the main menu explicitly selects
`Camera.Scaled` while
excluding Flow/UI and sky cameras. Game-state transitions invalidate the graph
even when Unity does not load a different scene. These paths remain subject to
the runtime verification in Decision 0013.

While a view is active and the panel is closed, a small overlay names the view
and selected camera. Select Off before leaving a test scene. Disabling the view
detaches and releases its command buffer and restores the camera's original
depth flags.

## Report contents

`phase1-latest.json` and timestamped reports include:

- mod, game, Redux, and Unity versions;
- operating system, graphics API, GPU identity, IDs, memory, and driver string;
- render-texture, motion-vector, and asynchronous-readback support;
- NVIDIA/AMD managed assembly, API, native plugin, device, and feature-query
  status, including safe error fields;
- active game state and Unity scene;
- every loaded camera's order, flags, masks, path, dimensions, target, PPv2 AA,
  requested depth modes, components, and command buffers;
- scaled/physics stack membership and main/debug/cubemap cameras;
- render-scale presenter sources, target, presentation camera, event, and scale;
- conservative evidence statements that distinguish a candidate from visual
  proof;
- requested and selected temporal backend, resolve camera, active/fallback
  status, and last explicit history-reset reason;
- the latest Unity `_NonJitteredVP` / `_PreviousVP`, project-tracked current /
  previous view-projection matrices, their maximum absolute differences, and
  resolve-camera projection and transform state.

Reports contain no save contents and do not serialize filesystem paths.

Schema 20 also records whether the discovered scene graph supports coherent
projection jitter. `Map3DView` resolves on `MapCamera` and the main menu resolves
on `Camera.Scaled` after its shared `Skybox` predecessor, but both report
`projectionJitterSupported=false` and effective zero projection/dispatch
jitter. Flight, KSC, and VAB report the capability as supported and retain the
shared temporal jitter sequence. Decision 0029 records the isolation evidence
for this scene-level contract.

## Build and load verification

Target used on 2026-08-22:

- Game/Redux `0.2.8.5.103184-beta` (`ffc94930`)
- Unity `6000.4.1f1`
- Redux template `0.2.8.5` (`455b945`)
- SDK `beta-6` (`3e05f9b`)
- Windows, Direct3D 11, NVIDIA GeForce RTX 5070 Ti

The official SDK/ThunderKit player and zip pipelines both completed. The
installed player package contains `ReduxBetterAA.dll`, `swinfo.json`, and the
mod Addressables catalog/bundle. SpaceWarp registered the mod, loaded its
catalog, initialized it, and loaded the diagnostic shader without a
ReduxBetterAA-correlated exception.

The first complete report at `MainMenu` contained 11 cameras, no scene render
stacks or presenters (expected outside a gameplay view), and reported Unity
motion-vector support. Both Unity vendor managed APIs were present; neither
native Unity vendor plugin loaded. The NVIDIA result is therefore an explicit
unsupported/fallback state, and no DLSS/DLAA feature context was attempted.

## Known editor diagnostics

The imported template emits existing editor diagnostics involving the Timeline
extension, Burst editor hashing, and duplicate test-only
`System.Runtime.CompilerServices.Unsafe` assemblies. They did not fail script,
shader, Addressables, assembly, player, or zip builds. Runtime log review must
still distinguish unrelated Redux/game warnings from messages prefixed
`[ReduxBetterAA/...]`.

## Phase 1 exit

The completed capture evidence is summarized in
[`capture-checklist.md`](capture-checklist.md). Decision 0001 accepts a unified
final-scene resolve and records the intermittent near-launchpad motion-vector
defect as a constraint on Phase 2 and a blocker for DLAA.
