# Phase 2 PPv2 TAA prototype

## Status and safety

The PPv2 backend is installed but disabled by default. Use the AA selector in
the `Ctrl+F10` engineering panel. PPv2 is deliberately absent from the normal
settings and `F12` public cycle, where the portable temporal mode is Custom TAA.
Unsupported states retry camera discovery without changing scene output and
show the reason in the panel.

The `Ctrl+F10` panel has one toolbar for every AA mode plus **Buffers**. The PPv2
page exposes all four PPv2 parameters, a conservative-preset button, and a
manual history-reset button. Values are clamped, applied immediately, and
recorded in subsequent JSON reports. Shared sharpness is persisted; other PPv2
engineering values are session-only.

The initial fixed preset is deliberately conservative:

| PPv2 setting | Value | Reason |
| --- | ---: | --- |
| Jitter spread | 0.75 | PPv2's normal eight-sample coverage |
| Sharpness | 0.15 | Shared cross-backend default; avoids aggressive ringing |
| Stationary history | 0.92 | Useful stability with slightly faster recovery |
| Moving history | 0.05 | Limits contamination from near-pad motion spikes |

Slider ranges are jitter spread `0.1-1.0`, sharpness `0-1`, stationary history
`0-0.99`, and moving history `0-0.99`. Treat high moving-history values as
unsafe around the launchpad until the motion-vector defect has a real fix.

This preset is a diagnostic starting point, not a claim that the launchpad
motion defect is fixed.

## Implementation

- One PPv2 TAA resolve runs on the active final scene camera.
- During flight, the scaled main camera receives the exact next PPv2 Halton
  sample before it renders; its projection state is restored immediately after
  rendering. PPv2 owns and advances the sequence on the physics final camera.
- Any AA on the scaled contributor is temporarily disabled so the final scene
  is not filtered twice.
- The coordinator resets history for first frame, backend/scene/camera changes,
  projection or output changes, explicit floating-origin snaps, and teleports.
  Fast continuous rotation is not treated as a camera cut. Reset reasons are
  logged only when they occur.
- Disable and shutdown restore AA modes, any PPv2 settings object created by
  the mod, tuning, contributor-camera depth flags, jitter
  callbacks, projection state, and event subscriptions idempotently.
- The Phase 1 global camera-count polling trigger was removed because Redux
  transient cameras made it write reports repeatedly while otherwise idle.

The exact `FloatingOriginSnappedMessage` camera handler is patched explicitly.
Quickload/revert and vessel-change hooks are still follow-up work where generic
scene/camera discontinuity detection is insufficient.

## First runtime test

Keep every diagnostic buffer view Off while judging image quality.

1. At the launchpad, press `F10` with PPv2 Off, press `F12`, wait two
   seconds, and press `F10` again from the same view.
2. Slowly orbit the camera, stop, and look for a flash, persistent double image,
   geometry trails, or scaled/physics edge separation. Capture one moving and
   one stationary frame.
3. Ascend through 100-500 m and repeat the orbit/stop test.
4. In orbit, repeat on the planet limb and thin vessel geometry.
5. Toggle map view and return to flight; then visit VAB. Confirm the panel names
   the expected final camera and the log contains a reset for each transition.
6. Select Off in the panel (or cycle to Off with `F12`) and confirm both known
   scene PPv2 layers report `None`; switching modes or unloading must restore
   their exact pre-mod state.

For the first pass, report whether each scene is better, worse, or unchanged;
whether artifacts persist after motion stops; and any repeated
`[ReduxBetterAA/History]` lines while the camera is stationary. If the launchpad
still flashes or ghosts, the next change is an automatic near-pad invalid-motion
fallback or construction of a corrected motion field—not DLAA.

## DLAA gate

This file describes the retained Phase 2 comparison backend. Current builds also
contain later-phase Custom TAA, managed DLAA, and FSR2 Native AA paths, but PPv2
remains isolated and does not depend on either vendor module.
