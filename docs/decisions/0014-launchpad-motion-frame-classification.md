# Decision 0014: same-frame launchpad motion classification and matrix diagnosis

## Status

Accepted for the 0.5.11 diagnostic build. Decision 0026 later confirms the raw
producer as one camera-centred indirect vegetation leaf draw; it rules out the
camera-history categories that remained open in this record.

## Context

The 0.5.10 launchpad run produced paired screenshots, motion statistics, and
capability reports. One stationary sample contained exactly zero motion in all
57,600 sampled pixels. A later sample, with the same camera, backend, and
`invertX=true` / `invertY=true` configuration, contained a finite screen-wide
radial field: p50 was approximately 361 pixels/frame, p99 approximately 1,206,
and the maximum approximately 1,356. Both depth-covered and no-depth samples
were affected. This rules out component inversion as the source and makes an
uncleared local object velocity buffer unlikely.

The flight renderer draws scaled space first and the depth-clearing physics
camera second. Unity's built-in motion pass derives camera motion from internal
`_NonJitteredVP` and `_PreviousVP` matrices. KSP updates both camera stacks from
double-precision simulation coordinates every frame, with independent scaled
and physics-space transforms. The remaining plausible source categories are:

1. a stale or mismatched Unity previous view-projection for the physics camera;
2. cross-camera global state from the scaled/physics handoff;
3. reuse of a motion target whose camera-only background was not coherently
   regenerated.

The captured four-quadrant radial geometry is characteristic of a camera
view/projection mismatch, but screenshots alone cannot distinguish those three
categories.

## Decision

- Reduce the vendor input ceiling from 256 to 64 pixels/frame, matching the
  existing Custom TAA rejection boundary.
- Before the full-resolution sanitizer pass, render a 1x1 corruption flag on
  the GPU from 16 fixed screen anchors. Six or more invalid, over-limit, or
  camera-disagreeing anchors classify a screen-wide corrupt frame.
- On a classified frame, replace every pixel with bounded project-tracked
  camera reprojection when it is no more than 64 pixels/frame, otherwise zero.
  The decision applies to the same frame without CPU readback latency.
- On a healthy frame, preserve ordinary raw object motion. Invalid, over-limit,
  or greater-than-64-pixel camera disagreement still follows the local
  fallback-or-zero rule.
- Permit far-plane camera reprojection for sky/no-depth samples. This prevents
  the corrupt camera-only field from leaking through the region that previously
  had no fallback while retaining deterministic camera rotation.
- Record Unity's `_NonJitteredVP` and `_PreviousVP`, project-tracked current and
  previous matrices, their maximum absolute differences, and resolve-camera
  projection/transform state in every capability report.
- Add separate `Motion: Sanitized Vendor Input` and `Motion: Sanitizer Decision`
  views. Raw diagnostics continue to show Unity's source and do not pretend the
  source itself was repaired.
- Add a one-click six-view capture burst. It closes Ctrl+F10, allows a smooth user
  pan, and captures raw, normalized, validity, sign, sanitized, and decision
  views with matching reports at fixed intervals. The validity report records
  the raw X/Y motion, depth, and over-limit state at the classifier's same 16
  anchors so the field can be fitted and compared to the captured matrices.

## Why not flip an axis

The entire run retained the same enabled X/Y signs across both clean and corrupt
frames. Negating a radial field only reflects it; it cannot turn hundreds of
pixels of motion into the approximately zero motion expected from a stationary
camera. Axis selection remains source-defined: Unity's built-in buffer is
previous-to-current and the vendor inputs require current-to-previous.

## Root-cause exit test

Run DLAA at the launchpad, select the resolve physics camera, and press the
motion-diagnosis burst while smoothly panning horizontally and vertically. The
result is classified as follows:

- If raw motion matches Unity's two internal matrices while `_PreviousVP`
  diverges sharply from the project-tracked previous matrix, Unity/KSP camera
  history state is the source.
- If Unity's matrices match the tracked matrices but raw motion does not, the
  rendered motion target or another writer is the source.
- If Unity's `_PreviousVP` matches the scaled camera rather than physics history,
  the multi-camera handoff is the source and Redux core should supply explicit
  per-camera previous matrices or a project-owned composite motion pass.

Regardless of category, the sanitized view must remain bounded and the decision
view must become orange on a corrupt frame. DLAA and FSR2 must not smear the
scene in the raw field direction. Custom TAA applies the same 64-pixel local
fallback-or-reject boundary. PPv2 remains the untouched comparison backend and
still consumes Unity's raw motion until a renderer-level repair is proven; its
private shader cannot safely receive a replacement texture without either
overriding Unity's global buffer or forking PPv2.

## Resource and performance impact

The classifier adds one 1x1 floating-point render target and one 16-sample
fragment invocation per vendor frame, followed by the existing full-screen
sanitizer pass. It adds no managed per-frame allocation or GPU readback. All
resources share the sanitizer lifecycle and release on backend switch,
resolution change, teardown, and shutdown.
