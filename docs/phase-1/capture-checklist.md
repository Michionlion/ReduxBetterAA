# Phase 1 capture checklist

## Test metadata

Record once for the capture set:

- game and Redux build;
- mod build/hash;
- GPU and driver;
- resolution, display mode, and AA setting;
- Redux render scale;
- save/vehicle description without personal or save-file contents.

The probe records most machine fields automatically. Add only the visual test
conditions that the runtime cannot infer.

## Procedure for each row

1. Enter the requested state and wait three seconds for cameras to stabilize.
2. Press `Ctrl+F10`, then click **Write report**.
3. Select `Linear Depth` in the panel. Select every scaled-space,
   physics-space, map, OAB/VAB, and presentation camera in the camera list.
   Click **Capture screenshot** for each relevant contributor and note missing
   or inconsistent coverage. The panel hides for the captured frame and then
   reopens automatically.
4. Select `Motion Validity / Magnitude`, close the panel with `Ctrl+F10`, and press
   `F10` while moving the camera and again after it has been stationary for one
   second. Each PNG has a sibling `-motion-stats.json` report. The categorical
   view uses blue for no-depth/quiet, magenta for no-depth/moving, green for
   depth-covered/quiet, cyan for depth-covered/moving, yellow for covered motion
   above 64 pixels/frame, and red for invalid motion. The report's counts are sampled
   estimates of screen coverage; motion values remain expressed in full-size
   source pixels.
5. Inspect `ContributionMask` for each contributing camera.
6. Inspect `FinalColor` at the apparent final scene/presentation camera and
   report whether native-resolution UI/text is absent from that camera output.
7. Select Off before changing state. `F10` and the panel capture button use the
   same capture path; they can also capture the normal screen while the view is
   Off.

## Required matrix

| State | Required observation | Captured |
| --- | --- | --- |
| KSC | Camera graph and native-resolution UI/scene separation | [ ] |
| VAB/OAB | Camera graph, depth, motion during orbit, outlines/UI separation | [ ] |
| Launchpad flight | Scaled/physics order, vessel and planet depth/MV, final color before UI | [ ] |
| Low-altitude terrain | Terrain and vessel depth/MV continuity during translation | [ ] |
| Orbit/planet limb | Near vessel plus far planet/sky depth and MV coverage | [ ] |
| Map view | Map camera graph, depth/MV behavior, icons/UI exclusion | [ ] |
| Flight to map to flight | Camera-group/order changes and automatic reports | [ ] |
| Native render scale | Source targets restored; presentation camera/target inactive | [ ] |
| Non-native render scale | Shared target, source associations, presentation camera/event | [ ] |

Optional but useful rows are quickload/revert, vessel switch, and a window or
resolution change. They help validate lifecycle invalidation but do not
authorize Phase 2 history behavior.

## Evidence interpretation

For each image, record the overlay's exact camera name and view. Visual success
means coverage aligns with the final scene color; the mere presence of a depth
attachment or `MotionVectors` flag is not sufficient.

Do not write `docs/decisions/0001-temporal-resolve-placement.md` until these
captures distinguish among a unified final resolve, synchronized per-stack
resolves, and construction of an additional composite buffer.

## Evidence received 2026-08-22

The first operator flight observation reports that linear depth covers the
vessel and terrain/planet together. The motion magnitude/angle view shows large
saturated radial regions, triangular discontinuities or stripped gaps, and
unstable residual output after camera motion stops. Native flight UI remains
visible over the diagnostic motion view, while the operator believes the
selected final-color camera itself excludes UI/text.

These are useful findings but remain provisional until the saved images can be
matched to the exact camera selected in the panel. The visible UI over the
diagnostic view is evidence consistent with UI composition occurring after the
scene-camera debug hook.

## Evidence completed 2026-08-23

The saved images and sibling statistics reports identify
`FlightCameraPhysics_Main` as the final flight scene camera. Linear depth covers
the vessel and terrain/planet together, while final color is available before
native UI composition. Map and VAB reports identify their single main cameras
and post-process layers. Native and non-native render-scale reports confirm that
the same final-scene camera is the safe PPv2 resolve owner before either direct
presentation or the later `RenderScalePresenter` blit.

Motion is coherent in orbit and above the near-pad region: representative
moving orbit captures have p99 magnitude near one source pixel per frame, and a
stationary capture at roughly 687 m has p99 zero with a 0.001 pixel maximum. In
contrast, multiple launchpad captures contain periodic, scene-wide finite
vectors with p99 magnitudes of roughly 1,300-1,780 pixels and maxima of
1,451-2,031 pixels. Adjacent stationary launchpad frames can be clean, so this
is an intermittent near-pad input discontinuity rather than normal camera
motion or a temporal-history reset.

Decision 0001 therefore selects the unified final-camera resolve with a known
constraint: Phase 2 may evaluate PPv2 TAA only with a conservative moving-pixel
history weight and immediate Off fallback. DLAA and later temporal backends may
not treat the launchpad motion field as validated input.
