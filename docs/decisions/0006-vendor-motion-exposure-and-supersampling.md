# Decision 0006: vendor motion sanitization, exposure, and DLAA supersampling

## Status

Accepted for the 0.5.3 comparison build. Launchpad and 200% visual acceptance
remain runtime tests.

Motion direction, camera fallback, and PPv2 exposure behavior are superseded by
[Decision 0007](0007-vendor-motion-direction-and-ppv2-exposure.md).

## Context

Phase 1 captures near the launchpad found finite but physically impossible
motion vectors: p99 values were approximately 1,300-1,780 pixels/frame and
maxima were approximately 1,451-2,031 pixels/frame. The corruption was absent
in orbit and in the recorded ascent capture near 687 m. Custom TAA already
rejects history above 64 pixels/frame and tries camera reprojection or zero for
larger samples, but DLAA and FSR2 previously received the raw texture.

The final PPv2 scene hook provides already processed scene color. PPv2 owns an
internal one-pixel auto-exposure texture, but it is not a stable public scalar
at this hook and is not an HDRP pre-exposure contract. Both Unity vendor APIs
provide their own auto-exposure initialization flag.

At Redux render scales above 100%, the scene cameras already render into an
enlarged shared target and the presenter downsamples it later. DLAA contexts
use equal input and output dimensions, so they can run on that enlarged target
without claiming to implement DLSS Super Resolution.

## Decision

- A backend-neutral shader copies the motion texture into a persistent RGHalf
  target and replaces NaN, Inf, and motion above 256 pixels/frame with zero.
- DLAA and FSR2 require the sanitizer and never fall through to raw motion when
  it is unavailable. The safe temporal fallback remains Custom TAA then PPv2.
- The 256-pixel threshold is fixed for this build: it is four times Custom
  TAA's default history-rejection threshold while remaining far below every
  captured launchpad outlier.
- DLAA defaults to preset K. DLAA and FSR2 default to vendor auto exposure;
  manual pre-exposure defaults to 1.0 and remains available as an override.
- DLAA rejects render scales below 100%. Above 100%, it requires the user-facing
  supersampling opt-in, runs equal-size on the enlarged scene target, and lets
  Redux perform the existing downsample before native UI composition.
- FSR2 Native AA continues to require exactly 100% render scale.
- Redux's normal settings expose user-facing modes and important quality,
  exposure, and resolution controls. Diagnostic views, reports, probes, and
  experimental motion-cadence controls remain in the Ctrl+F10 panel.

## Consequences

Zeroing an outlier intentionally favors a locally un-reprojected history sample
over an unrelated sample thousands of pixels away. This prevents the coherent
one-direction launchpad field from driving vendor history, but runtime capture
must still verify that each vendor backend reduces the visible instability.

DLAA at 200% is supersampled AA, not performance upscaling. Its output texture
and vendor internal resources scale with the enlarged pixel count, so GPU time
and memory can rise substantially. Whether a particular scene effect gains
resolution depends on whether its pass follows Redux's shared scene target and
must be confirmed in game.
