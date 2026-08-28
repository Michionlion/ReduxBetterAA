# Decision 0029: disable projection jitter for map and main-menu composition

## Status

Accepted for 0.5.25 after deterministic TestHarness isolation on Redux
0.2.8.5.103184, Unity 6000.4.1f1, Direct3D 11, and an NVIDIA GeForce RTX
5070 Ti at 2560x1440.

## Problem

Custom TAA, DLAA, and FSR2 could produce large rectangular or triangular
flickering patches on planetary bodies in `Map3DView` and the main menu. The
artifact was temporal: AA Off showed a stable source image, while temporal
backends accumulated mesh- or tile-shaped differences.

The two scenes use different render graphs:

- Map view is a single final `MapCamera`. It has a valid raw depth texture and
  remains the correct pre-UI resolve camera.
- The main menu renders shared `Skybox` color before `Camera.Scaled` performs
  the final scene resolve. `Camera.Scaled` clears its own depth, so the shared
  background is legitimately represented as no depth at the resolve point.

Changing the selected camera would therefore either omit scene color or move
the resolve into UI composition. The diagnostic linear-depth view was not a
vendor input; the vendor backends already consumed the raw camera depth.

## Isolation result

TestHarness scripts reproduced both scenes and varied one input at a time.

| Experiment | Result |
| --- | --- |
| AA Off source sequence | Planetary color remained clean |
| Raw and linear depth inspection | Map depth was valid; menu background had expected no-depth composition |
| Force all vendor depth to far | Patches remained and changed orientation |
| Object, camera-only, and forced-zero motion modes | Patches remained |
| Extra scaled-planet motion submission | Patches remained |
| NVIDIA automatic versus fixed exposure | Patches remained |
| Smaller nonzero jitter spread | Patches remained |
| Zero projection and dispatch jitter | Patches disappeared in both scenes |

The scaled planetary and menu background paths do not respond coherently to a
subpixel offset applied only through `Camera.projectionMatrix`. The temporal
backend was nevertheless given the projection's subpixel sample offset. That
value does not claim that the camera or scene moved; it tells the resolve where
the current frame's raster sample lattice lies relative to nominal output pixel
centres. History reprojection then accumulated the renderer's unchanged or
differently sampled tiles at an incorrect subpixel location. This explains why
changing depth altered the shape without removing the cause.

On a fixed map-planet crop, the old normally jittered DLAA reproduction had a
mean frame-difference luma of 5.76 and a peak mean of 9.91. The isolated
zero-jitter control measured 0.16 and 0.19; the installed production 0.5.25
path measured 0.18 and 0.23. The remaining change is ordinary animation and
capture noise rather than rectangular history corruption.

## Jitter contract

Unity's PPv2 perspective helper accepts jitter in pixels and adds the following
terms to the projection matrix:

```text
projection[0,2] += 2 * jitterPixels.x / pixelWidth
projection[1,2] += 2 * jitterPixels.y / pixelHeight
```

After perspective division, the view-space depth term cancels. The result is a
constant subpixel screen offset at every scene depth, including scaled-space
geometry and sky directions. PPv2 and Redux's custom TAA de-jitter the current
color sample with the inverse normalized offset. Unity HDRP likewise supplies
the inverse projection offset to its DLSS and FSR2 dispatches, matching the
Redux vendor integration.

Projection jitter is therefore not motion and must not be folded into motion
vectors. It deliberately moves the current frame's sample positions so several
frames cover different subpixel locations; the temporal resolve combines them
at nominal output pixel centres. This provides stationary-edge supersampling in
addition to temporal stabilization.

Jitter must not be scaled by scene depth. Doing so would no longer describe a
single projective camera: color and depth at a silhouette could use different
sample lattices, motion reprojection would not have one global inverse offset,
and the DLSS/FSR2 APIs expose no per-depth jitter field. A scene with mixed
jittered and unjittered contributors must instead make those contributors obey
one common projection offset or resolve them separately before composition.

The map controller was also inspected in the installed KSP2 managed assembly.
Its render callbacks change observer/galaxy cubemap keywords but do not restore
or replace `Camera.projectionMatrix`, so the map incompatibility is downstream
of camera control: scaled-body, atmosphere, cloud, or related tiled rendering
does not derive every generated input from the same jittered projection. The
main menu additionally composes an earlier `Skybox` camera with
`Camera.Scaled`, making the same contract more fragile there.

## Decision

Projection jitter is an explicit camera-graph capability rather than a backend
assumption:

- Flight, KSC, and VAB support the shared Halton projection-jitter sequence.
- Map view and the main menu use zero projection jitter and zero vendor dispatch
  jitter.
- Custom TAA, DLAA, and FSR2 remain available in those scenes and still use
  color, depth, motion, matrices, exposure, and explicit history resets.
- PPv2 TAA is rejected in scenes without coherent projection jitter and follows
  the existing Off fallback policy.

This is a source-contract correction, not a sanitizer or spatial filter. Full
jitter quality is preserved in renderer paths that demonstrated coherent
subpixel projection. Schema 20 reports `projectionJitterSupported` and the
effective zero jitter so future camera-graph changes are observable.

## Reproducible verification

`ReduxTestHarness/tests/investigations/betteraa-menu-map-production-aa.lua`
captures consecutive Custom TAA, DLAA, and FSR2 frames in the main menu and map
view and emits a capability report in each scene.
`betteraa-flight-jitter-regression.lua` independently loads Flight and emits a
schema 20 report to verify that normal jitter remains enabled there.

The source-isolation runs are:

- `betteraa-menu-map-source-stability`
- `betteraa-menu-map-depth-ownership`
- `betteraa-menu-map-jitter-sensitivity`
- `betteraa-map-motion-mode-output`
- `betteraa-map-depth-input-isolation`
- `betteraa-menu-map-camera-trace`
- `betteraa-map-vendor-input-isolation`
- `betteraa-main-menu-zero-jitter`

The installed 0.5.25 production run passed its harness assertions with 120
screenshots and no test warnings or errors. Representative and full-sequence
inspection found no planetary patch flicker in any of the six scene/backend
combinations.
