# Decision 0009: floating-origin history continuity and frame-jump diagnostics

## Status

Accepted for the 0.5.6 comparison build. Runtime confirmation is required at
high surface-relative velocity with DLAA, FSR2, Custom TAA, and PPv2 TAA.

## Context

The supplied five-second capture shows occasional repeated presented frames and
a whole-view discontinuity, including the skybox. The corresponding player log
does not show periodic `CameraCut` resets. Its only late DLAA camera-cut entry is
adjacent to a roughly 465 m/s vessel impact, destruction, and focus loss.

Inspection of the exact shipped `Assembly-CSharp.dll` provides a stronger
cadence match. `KSP.Sim.impl.FloatingOrigin` uses a 1,000 metre position
threshold and publishes `FloatingOriginSnappedMessage` after each snap.
`UniverseCameraManager.OnFloatingOriginSnapped` then rewrites the primary
camera view transforms. At 465 m/s, position-driven snaps can occur about every
2.15 seconds, matching the reported one-to-two-second whole-view event far
better than the logged history-reset evidence.

The previous generic tracker also classified any rotation above 25 degrees
between rendered frames as a camera cut. A normal fast pan following a slow or
stalled frame can cross that threshold, so it was not a trustworthy cut signal.
PPv2 exposure was additionally read back asynchronously as quickly as requests
completed; although asynchronous, a one-pixel exposure signal does not justify
unbounded graphics-queue readback traffic.

## Decision

- Patch KSP's exact floating-origin camera handler and queue an
  `OriginRebased` reset for the active temporal backend.
- Keep the current camera graph, render resources, and vendor context alive.
  An origin snap is a history discontinuity, not a renderer replacement.
- Suppress the generic position-teleport heuristic for that snap so it is
  handled once under the precise reason.
- Remove rotation magnitude as an inferred camera cut. Camera object/selection,
  scene, projection, teleport, and explicit game events remain reset sources.
- Ignore repeated `SetPrimaryScreenCamera` and `SetCurrentUnityCamera` calls
  whose selected camera did not actually change.
- If Unity skips `onPostRender`, restore a lingering jittered projection before
  applying jitter on a later frame.
- Limit the asynchronous PPv2 one-pixel exposure readback to 10 Hz and reuse the
  last valid scalar between requests.
- Rate-limit frame-pacing diagnostics for frames above 30 ms and include the
  number of frames since the latest history reset and origin snap.

## Consequences

An origin snap produces a single vendor-supported history invalidation instead
of allowing prior-coordinate history to persist or recreating the native
context. The change does not attempt to smooth or override KSP's actual camera
transform; if a new test
still shows a whole-view step exactly at the origin event, the remaining defect
is in camera-pose presentation and requires a separately gated camera transform
continuity experiment.

The capture is a 30 FPS recording of a much faster-running game and contains
duplicated encoded frames, so it cannot by itself prove the render-thread source
of every stall. The new `[ReduxBetterAA/FramePacing]` and
`[ReduxBetterAA/History] ... OriginRebased` lines make the next runtime test
falsifiable without adding per-frame managed allocation.
