# Decision 0017: synchronized main-menu background jitter

## Status

Accepted for the 0.5.14 comparison build. Runtime acceptance requires main-menu
captures of the distant planet and other `Render.Skybox` silhouettes.

## Evidence

The main-menu F10 reports show a two-camera three-dimensional composition before
Flow/UI rendering. `Skybox` renders first at depth -3 with culling mask
`Render.Skybox`; `Camera.Scaled` renders at depth -1 with `ClearFlags.Depth`.
The latter therefore preserves background color while intentionally clearing
the predecessor's depth. Redux Better AA previously selected only
`Camera.Scaled`, so background color entered every temporal backend without the
projection jitter described by the backend's jitter offset.

This is a color/jitter coherence failure rather than evidence that an arbitrary
near depth should be invented for distant objects. After `Camera.Scaled` clears
depth, uncovered pixels correctly read as the far plane. For a sky or very
distant planet that is the safest shared depth representation; copying device
depth from a camera with independent projection and clip planes would be
mathematically invalid and could occlude later scaled-scene geometry.

## Decision

- In `MainMenu`, retain `Camera.Scaled` as the sole temporal resolve camera.
- Select the exact enabled `Skybox` predecessor only when it renders earlier to
  the same target, and apply the active backend's identical pixel jitter to it.
- Continue resolving once on `Camera.Scaled`, before Flow/UI. Never add the menu
  background as a second temporal resolve.
- Keep the final far-plane depth in pixels not covered by `Camera.Scaled`.
  Camera-motion fallback may use that depth, while static background motion
  remains zero as required by the vendor interfaces.
- Record the shared-jitter camera and all camera clip/projection settings in F10
  reports so a changed menu composition fails diagnosably.

## Expected behavior

The distant planet, background silhouettes, and star/sky geometry receive the
same Halton sample as the foreground menu models. Their stationary edges can
therefore accumulate in Custom TAA, DLAA, and FSR2 instead of being shifted by a
resolve that assumed jitter they never received. UI remains outside temporal
history.

## Resource and lifecycle impact

No render texture, depth copy, native context, or additional resolve is added.
Existing backend projection-state ownership stores and restores the predecessor
camera projection and depth-mode flags during activation and teardown. Only one
temporal backend remains active.
