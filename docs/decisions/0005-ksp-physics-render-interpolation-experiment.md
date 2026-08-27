# Decision 0005: Opt-in KSP physics render interpolation experiment

- Status: Experimental; disabled by default
- Date: 2026-08-24
- Phase: Shared temporal-input correction during Phase 5 evaluation
- Scope: SPEC Sections 6.3, 7.1, 8.3, 9.2, and 10.2

## Context

The normalized-motion and validity/magnitude videos show a vessel alternating
between quiet motion and coherent motion bursts while the game renders at about
160 FPS. The recordings are approximately 30 FPS, so they undersample the game
and cannot establish exact frame cadence by themselves.

Six full-resolution motion-statistics captures at approximately 198 FPS provide
stronger evidence. All 57,600 samples in every capture are finite. The three
quiet captures have a depth-covered moving ratio of zero, p99 at or below
0.032 pixels, and maxima at or below 0.054 pixels. The three active captures
have depth-covered moving ratios from 0.982 to 0.995, p99 from 12.54 to 16.20
pixels, and maxima from 14.36 to 17.63 pixels. No capture has an invalid sample
or a depth-covered outlier above 64 pixels. Active motion covers nearly the
whole vessel instead of forming random holes.

Inspection of the exact target `Assembly-CSharp.dll` shows that
`KSP.Sim.impl.RigidbodyBehavior.StartPhysX()` creates or returns Unity
Rigidbodies without selecting an interpolation mode. The normal update path
reads the active Rigidbody transform. Unity's default Rigidbody interpolation
mode is therefore left in effect. The alternating whole-vessel quiet/burst
pattern is consistent with fixed-step physics poses being presented at a much
higher render rate. This is an evidence-backed inference, not yet a runtime
proof for every vessel state.

## Decision

Do not average, dilate, or temporally smooth the final motion-vector texture as
the first correction. Such a field would no longer describe the color and depth
actually rendered in that frame and could increase reprojection ghosting.

Add a disabled-by-default experiment under the Ctrl+F10 **Buffers** page. When
enabled, it changes only active KSP `RigidbodyBehavior` bodies whose current
mode is `None` to Unity's `Interpolate` mode. This lets Unity interpolate the
rendered physics pose so color, depth, and motion vectors are produced from the
same smoother transform.

The experiment:

- records every original Rigidbody interpolation mode;
- never overrides an existing `Interpolate` or `Extrapolate` selection;
- patches `StartPhysX` so newly created KSP physics bodies are included;
- restores a body before `StopPhysX` and restores all tracked bodies on disable,
  scene transition, mod shutdown, and game shutdown;
- batches temporal-history resets and invalidates performance profiles when the
  motion input changes;
- records fixed-step timing, enabled state, tracked-body count, and status in
  capability reports and F10 motion-statistics sidecars.

## Consequences and gate

The stock behavior remains the default. The experiment must not be promoted
until runtime comparison covers launch, ascent, orbit, landing, staging,
docking, time warp, quickload/revert, vessel switching, and floating-origin
changes. It must improve the visible cadence without introducing transform lag,
part separation, collisions, camera disagreement, or new temporal smearing.
Unity interpolation normally presents a pose between the two most recent fixed
states, so approximately one fixed step of visual latency is an explicit
tradeoff to evaluate.

Compare with the **Motion Vectors: Normalized** and **Motion Validity /
Magnitude** views at the same camera path and frame-rate conditions. F10
captures now include the fixed-update rate and interpolation state in motion
statistics schema 2. A single stationary screenshot is insufficient evidence.

## Evidence limitations

- The supplied videos capture only about every third to fifth rendered frame.
- The six statistics files are single-frame samples rather than a contiguous
  GPU readback sequence.
- The launchpad-only extreme-motion defect documented in Decision 0001 is a
  separate issue; this experiment is not claimed to repair that defect.
