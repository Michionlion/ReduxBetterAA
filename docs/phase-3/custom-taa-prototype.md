# Phase 3 custom TAA prototype

## Status and comparison scope

Phase 3 was started by explicit maintainer request before the Phase 2 visual
acceptance gate was closed. It does not replace Phase 2. The public `F12` cycle
also includes spatial and supported vendor modes; PPv2 remains an engineering
comparison available only in `Ctrl+F10`:

```text
Off -> FXAA Low -> FXAA High -> SMAA -> TAA -> supported vendor modes -> Off
```

The custom backend is experimental and disabled by default. PPv2 remains an
independently selected engineering comparison, but a Custom initialization or
runtime failure falls directly back to Off. A backend switch releases the
previous owner's resources, restores its camera state, and resets the incoming
history.

## Pipeline

The project-owned resolve runs once on the same final scene camera selected in
Decision 0001, after ordinary PPv2 post processing and before native UI cameras.
When Custom TAA is active, PPv2 AA is disabled on the final and shared
contributor layers so there is only one temporal resolve.

The initial pipeline is:

1. Apply one configurable Halton 2/3 jitter sample to every contributing scene
   camera.
2. Select motion from the closest-depth pixel in a 3x3 neighborhood and detect
   local depth discontinuities in the same footprint.
3. Reproject history with Unity/PPv2 current-to-previous UV motion.
4. Reject invalid, out-of-bounds, excessive-motion, and depth-disoccluded
   history.
5. For extreme or invalid motion only, validate a camera-only fallback from the
   current inverse and previous view-projection matrices.
6. Sample history with a 16-tap Catmull-Rom filter.
7. Build 3x3 YCoCg bounds plus luminance variance bounds. At a depth edge,
   exclude samples from other surfaces before clamping history.
8. At a depth edge, compare against exact previous-depth texel centers in a
   one-pixel footprint. This avoids false rejection from bilinear depth values
   between foreground and background and permits stable same-surface history.
9. Reduce history by velocity, depth disagreement, and an inferred luminance
   reactive mask. The configurable edge-stability factor restores history
   weight only after the surface-aware clamp and depth match pass.
10. Blend, write the next color/depth history, and optionally apply mild
   sharpening.

The current coherence-aware policy accepts camera motion above 256 pixels per
frame only when it agrees with project-tracked reprojection. Unverified motion
above 256 pixels and disagreement above 96 pixels use bounded camera fallback
when available and otherwise become zero. This rejects the measured launchpad
radial field while retaining legitimate fast pans.

## Owned resources and memory

At the active scene output size, the backend owns:

- two scene-format color histories;
- two linear-depth histories (`RFloat`, or `RHalf` fallback);
- one scene-format resolve target;
- one material and one final-camera render hook.

All resources are persistent after warm-up, recreated on descriptor changes,
and released on mode switch, scene invalidation, shutdown, or disposal. With a
typical `ARGBHalf` scene target, the allocation is 32 bytes per pixel with
`RFloat` depth or 28 bytes per pixel with `RHalf` depth:

| Output | ARGBHalf + RFloat | ARGBHalf + RHalf |
| --- | ---: | ---: |
| 1920x1080 | 63.3 MiB | 55.4 MiB |
| 2560x1440 | 112.5 MiB | 98.4 MiB |
| 3840x2160 | 253.1 MiB | 221.5 MiB |

`ARGBFloat` output raises the `RFloat`-depth totals to approximately 110.7 MiB,
196.9 MiB, and 443.0 MiB respectively. The actual allocation estimate is shown
in the Custom tab and serialized into capability reports.

## Ctrl+F10 controls and diagnostics

The panel has one mode toolbar plus Buffers:

- **PPv2** retains the Phase 2 parameter controls and conservative preset.
- **Custom** exposes jitter spread/length, stationary and moving history,
  motion response and rejection, surface/depth threshold, depth-edge stability,
  variance clipping, inferred reactive strength, no-depth history cap,
  sharpening, and history reset. Setting depth-edge stability to zero selects
  the legacy edge decisions for direct A/B testing.
- **DLAA** exposes the managed Phase 4 native-resolution backend and its
  diagnostic parameters.
- **Buffers** retains the Phase 1 camera and scene-buffer visualizers.

Sharpening and debug-view selection are presentation-only and do not clear
Custom history. Changes to jitter, accumulation, rejection, or clipping do.

Custom debug output includes current color, history, reprojected history,
motion vectors, depth rejection, detected depth edges, reactive mask, history
weight, clamp extent, and final resolve. `F10` captures the selected output
using the existing screenshot path.

## 0.4.2 depth-edge stability experiment

The first Custom resolve treated every 3x3 color sample as one neighborhood and
read reprojected history depth bilinearly. At a large depth discontinuity this
could mix two surfaces into the clamp and synthesize an in-between history
depth, causing the foreground/background decision to alternate with jitter.

Version 0.4.2 adds an opt-in-strength edge path (default `0.75`) that:

- detects depth/coverage discontinuities while selecting closest-depth motion;
- builds the history clamp from center-surface samples at those pixels;
- searches exact previous-depth texel centers within one pixel;
- uses a soft depth transition instead of a binary edge threshold;
- raises moving history toward stationary history only after the surface and
  depth checks; and
- compares the inferred reactive signal with clipped history at the edge.

The additional previous-depth taps are dynamically restricted to detected edge
pixels. Unity 6000.4.1f1 passed all 12 edit-mode tests, imported the shader with
no shader error, and completed ThunderKit pipeline log 18. Runtime visual and
GPU timing evidence remain required.

## Required visual comparison

For the same camera path, collect Off, PPv2, and Custom captures at:

1. Launchpad, both stationary and while orbiting the camera.
2. Approximately 100-500 m altitude and low terrain flight.
3. Orbit with the planet limb and thin vessel geometry.
4. Map transition and return to flight.
5. VAB camera motion and part outlines.
6. Engine exhaust, atmosphere, and another transparency-heavy scene.

For Custom, also capture History Weight, Depth Rejection, Depth Edges, Reactive
Mask, and Motion Vectors when an artifact appears. Compare depth-edge stability
`0.00` against the conservative `0.75` on the same moving camera path. Record
whether stopping the camera causes a single reset flash, persistent flicker, or
a trail, and note the allocated MiB shown in the panel.

## Known limitations and remaining gate

- The reactive mask is inferred from luminance change. Dedicated material,
  transparency, and composition masks are not yet available.
- Depth-edge matching searches a one-pixel previous-depth footprint. This is a
  stability heuristic, not a replacement for a dedicated disocclusion or
  geometry mask; overly high edge stability can trade shimmer for short trails.
- Camera-only motion is a narrowly gated fallback and cannot represent object
  motion or every scaled-space/origin-rebase case.
- Origin rebases now use KSP's exact floating-origin camera-handler event.
  Generic transform-derived teleport detection resets once and then latches
  until camera translation settles below 250 m/frame, so continuous orbital
  dolly motion cannot clear history on every frame. Quickload/revert and
  vessel-change events still rely on generic scene and camera lifecycle paths.
- GPU pass time and steady-state managed allocation still require an in-player
  profiler capture.
- Phase 3 is not accepted until the required scenes show stability equal to or
  better than PPv2 and demonstrate reduced ghosting or softness in at least one
  documented Phase 2 failure case.
