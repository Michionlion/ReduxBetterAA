# Decision 0007: explicit vendor motion direction and PPv2 pre-exposure

## Status

Accepted for the 0.5.4 comparison build. The finite-motion consistency rule is
superseded by [Decision 0013](0013-state-specific-camera-discovery-and-motion-consistency.md).
The sign-agreement view and exposure-source transition still require in-player
validation.

## Context

Unity 6000.4.1f1's exact Core RP adapters classify built-in motion as
`PreviousFrameToCurrentFrame` and multiply both components by `-1` before DLSS
or FSR2. PPv2's TAA shader independently confirms the convention by sampling
history at `currentUv - motion`. Therefore the vendor direction is not a
player-specific guess: both components must be negated for this input.

Unity's NVIDIA execution struct contains `invertXAxis` and `invertYAxis`, but
the corresponding current NVIDIA Streamline options are
`indicatorInvertAxisX` and `indicatorInvertAxisY`. NVIDIA documents them as
orientation controls for its optional on-screen DLSS status indicator. They do
not transform the supplied motion texture and are not a calibration facility.

PPv2 3.2.2 owns a 1x1 `RFloat` texture named `m_CurrentAutoExposure`. It is not
stored on `Camera`, but it is available from the resolve camera's
`PostProcessLayer` renderer after PPv2 has evaluated the active volume. The
selected resolve hook receives color after PPv2 has already applied exposure,
so passing the scalar as vendor pre-exposure matches Unity HDRP's practice more
closely than tagging that same texture as a second exposure operation.

## Decision

- `MotionVectorSanitizer.shader` is the only place where user-selectable X/Y
  motion sign changes occur. Both inversion controls default to enabled.
- DLAA and FSR2 use positive width/height motion scales after sanitization.
- NVIDIA's indicator fields are set only from render-surface orientation: X is
  false and Y follows `SystemInfo.graphicsUVStartsAtTop`.
- The Buffers tab adds `Motion Sign Agreement`. During camera panning over
  static, depth-covered geometry, its left half scores X and right half scores
  Y against depth-based camera reprojection. Green agrees, red is reversed, and
  dark blue lacks enough motion for a decision.
- Invalid or greater-than-256-pixel raw motion first attempts depth-based camera
  reprojection. A finite fallback is accepted only up to 64 pixels/frame;
  otherwise the sanitizer writes zero. Valid raw motion below 256 pixels is not
  blanket-rejected at 64, preserving legitimate object motion.
- Automatic exposure prefers an asynchronous read of PPv2's current 1x1 GPU
  exposure. The scalar is clamped to Unity HDRP's DLSS safety range of 0.2-2.0
  and supplied as pre-exposure. Until a valid sample exists, or when PPv2 auto
  exposure is disabled/unavailable, the context uses vendor auto exposure.
- Manual pre-exposure remains the final explicit override. No synchronous GPU
  readback is introduced.

## Consequences

The old DLAA toggles now have their literal advertised effect. Changing them
can no longer merely rotate an NVIDIA indicator. FSR2 and DLAA also receive the
same transformed texture and use the same camera fallback policy.

The sign-agreement view validates static camera motion, not animated vessels,
particles, or scaled-space/physics-space ownership. Mixed colors on those
objects do not override the source-defined default. Runtime testing should pan
one axis at a time over terrain away from the launchpad corruption.

PPv2 readback is asynchronous and can lag the GPU result by a frame. That is
preferable to a render-thread stall and is consistent with an adapted exposure
signal. Switching between vendor and PPv2 exposure requires one context
recreation and history reset; reports record the effective source and scalar.

## Primary references

- Unity 6000.4.1f1 installed package source:
  `com.unity.render-pipelines.core/Runtime/Upscaling/DLSSIUpscaler.cs` and
  `FSR2IUpscaler.cs`.
- PPv2 3.2.2 pinned package source:
  `Runtime/Effects/AutoExposure.cs` and
  `Shaders/Builtins/TemporalAntialiasing.shader`.
- NVIDIA Streamline DLSS options and indicator semantics:
  <https://github.com/NVIDIA-RTX/Streamline/blob/main/include/sl_dlss.h>.
- NVIDIA DLSS integration checklist and on-screen indicator description:
  <https://developer.nvidia.com/rtx/streamline/get-started>.
