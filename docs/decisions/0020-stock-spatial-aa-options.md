# Decision 0020: expose stock spatial anti-aliasing modes

## Status

Accepted for the 0.5.17 comparison build.

## Evidence

The exact KSP2/Redux graphics handler maps its anti-aliasing enum as follows:

- `Off` selects PPv2 `None`.
- `Low` selects PPv2 `FastApproximateAntialiasing` with `fastMode = true`.
- `High` selects PPv2 `FastApproximateAntialiasing` with `fastMode = false`.

The handler never selects PPv2 `SubpixelMorphologicalAntialiasing` (SMAA), even
though that effect is present in the embedded Unity Post Processing package.
Calling the stock High setting SMAA would therefore make the selector label
misleading and would not preserve the exact base-game behavior.

## Decision

- Add `FXAA Low` and `FXAA High` as explicit Better AA choices matching KSP's
  two stock spatial variants.
- Add `SMAA` as a separate choice using PPv2's existing High-quality SMAA
  implementation.
- Run each spatial mode through a coordinator-owned backend. It sets the final
  scene `PostProcessLayer`, disables AA on the shared contributing layer, and
  restores both layers and effect settings on backend switch, scene teardown,
  or mod shutdown.
- Spatial modes do not advance jitter, allocate history, or consume motion
  vectors. They remain mutually exclusive with PPv2 TAA, custom TAA, DLAA, and
  FSR2.
- The normal settings page, Ctrl+F10 panel, F12 cycle, performance profiler, and
  capability report use the same `BackendSelection` values.

## Verification

EditMode tests cover the public list and cycle order. In-game verification
should compare each mode against the stock graphics setting in flight, map,
VAB, KSC, and main-menu scenes, and confirm that switching away restores the
previous PPv2 layer configuration.
