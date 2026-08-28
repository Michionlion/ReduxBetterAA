# Decision 0021: AA settings and lifecycle audit

## Status

Accepted for the 0.5.20 comparison build. Automated lifecycle and policy tests
pass; representative in-player visual comparison remains required.

## Context

A review after the cloud renderer moved to Redux Better Clouds found several
cross-backend inconsistencies:

- `Off` stopped Redux temporal work but restored the pre-existing PPv2 mode, so
  it was not necessarily a zero-AA performance or image-quality baseline.
- PPv2's engineering sharpness range was `0-3`, while the shared persisted
  setting silently clamped the same value to `0-1`.
- conservative presets assigned different values to the one shared sharpness
  setting, and FSR2 duplicated it with a separate RCAS enable toggle;
- spatial and PPv2 backends could leave settings objects created by the mod or
  contributor-camera depth flags behind after deactivation;
- sharpness and Custom debug-view changes unnecessarily cleared temporal
  history; and
- epsilon-sized animated FOV changes were treated as projection discontinuities.

The Unity 6000.4 FSR2 and DLSS command data expose sharpness on a `0-1` scale.
NVIDIA's current preset definitions retain F, J, K, L, and M, mark F legacy,
and identify K as the DLAA-oriented quality default.

## Decision

- `Off` is a coordinator-owned backend. It captures the discovered final and
  shared PPv2 modes, forces both to `None`, and restores them exactly when it
  releases ownership. The mod also restores the captured global MSAA sample
  count on unload.
- Public modes are ordered by increasing spatial quality before temporal modes:
  `Off`, `FXAA Low`, `FXAA High`, `SMAA`, `TAA`, then hardware-supported DLAA
  and FSR2. The public FSR label is `FSR 2 Native AA`; the old `FSR 2` value
  migrates without losing a supported selection.
- Sharpness uses one `0-1` contract and a `0.15` conservative default across
  PPv2, Custom, DLAA, and FSR2. FSR2 derives RCAS enablement from sharpness
  greater than zero instead of exposing a second switch.
- PPv2 and spatial modes restore exact pre-mod effect objects, values, layer
  modes, depth flags, projection state, and event subscriptions. All backends
  reset restored PPv2 histories at ownership boundaries.
- Output-only Custom sharpening/debug selection, DLAA sharpening, and FSR2
  RCAS changes preserve temporal history. Jitter, exposure, motion convention,
  rejection, and accumulation changes still reset or recreate history as their
  APIs require.
- Projection reset detection tolerates ordinary animated zoom. It resets on
  projection-mode changes, aspect changes over `0.001`, perspective FOV jumps
  over five degrees per observation, or orthographic-size jumps over ten
  percent (with a small absolute floor).

## Verification

Focused EditMode tests cover:

- public choice order, capability filtering, legacy migration, and K preset
  normalization;
- shared defaults, clamping, and output-only versus temporal reset policy;
- truthful Off ownership and exact spatial-layer restoration, including removal
  of effect settings created by the mod; and
- continuous zoom versus projection-discontinuity reset behavior.

In-player verification must still compare every mode in flight, map, VAB, KSC,
and main menu; confirm the stock control remains disabled; profile Off as a true
baseline; and verify mode switching and shutdown restore the pre-mod renderer
without a recurring warning or black frame.
