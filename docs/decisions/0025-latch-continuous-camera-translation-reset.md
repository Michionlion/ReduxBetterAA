# Decision 0025: latch continuous large camera-translation resets

## Status

Accepted for the 0.5.23 comparison build. High-altitude visual verification is
required with DLAA, Custom TAA, and Better Clouds enabled.

## Evidence

The 0.5.22 player log recorded 124 transform-derived `Teleport` history resets.
At high camera distance, DLAA and later Custom TAA reset on consecutive frames.
KSP's orbital camera can move more than the fixed 1 km detector threshold per
rendered frame during ordinary dolly or orbit movement, so world-space camera
translation alone cannot distinguish a one-frame teleport from continuous
large-scale camera motion.

Better Clouds' log showed its separate native cloud-TAA hook active and its
75–300 km volumetric fade override applied. Better AA contains no call into
cloud history. The repeated BetterAA reset nevertheless prevents the selected
scene backend from accumulating the already-composited cloud result and can
make sparsely sampled current cloud frames conspicuous.

## Decision

- Preserve the first transform-derived `Teleport` reset when movement exceeds
  1 km in one frame.
- Latch that discontinuity so continued large translation cannot reset history
  again on every frame.
- Rearm the detector only after a frame moves no more than 250 m.
- Keep explicit scene, camera, render-scale, origin-rebase, quickload/revert,
  vessel, and manual reset paths unchanged.
- Do not inspect, reset, or modify another mod's cloud-local temporal history.

## Consequences

A real isolated transform jump still clears the active AA history once. Normal
continuous high-altitude camera motion can resume temporal accumulation after
that one conservative reset. The change adds one boolean to the reset tracker
and no hot-path allocation or rendering resource.

## Runtime verification

1. With DLAA active, zoom from the surface through 100–150 km and orbit the
   camera. The log may contain one `Teleport` reset at the transition but must
   not contain a consecutive reset stream.
2. Confirm clouds remain present after motion settles and compare against Off.
3. Repeat with Custom TAA and FSR2 Native AA.
4. Trigger a real teleport or large one-frame camera discontinuity, then confirm
   exactly one reset and no persistent double image.
