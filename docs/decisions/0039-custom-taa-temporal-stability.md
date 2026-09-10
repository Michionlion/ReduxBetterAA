# 0039 - Custom TAA temporal stability

Phase 3, SPEC 11.2-11.6. The user identified objectionable temporal shimmer in
the live comparison footage. Measure consecutive stationary, moving and settle
frames using the normal production renderer, retaining DLAA M and FSR2 Native
AA as unchanged controls. Hold sharpness at the user's .24 setting.

Investigate motion-dependent history loss, depth-edge neighborhood clipping,
and inferred reactivity independently before selecting a change. A decrease
in temporal variance alone is insufficient: check detail against a shared
supersampled spatial reference and use analytic moving/disoccluding geometry
to detect blur and ghosting. Retain finite-history, reset, alpha, resource and
sharpening safeguards. Record measured results and limitations before delivery.

## Investigation and implementation

The inferred reactive mask treated subpixel geometric sampling changes as scene
changes, repeatedly throwing away useful history. Removing reactivity reduced
stationary structure variation by about 92%, but blurred moving details. Removing
depth-edge stabilization had little effect. Raw motion-vector direction was
verified against consecutive supersampled frames; its existing sign is correct.

A local-variance allowance fading out at one pixel/frame reduced stationary
variation by about 86%, but increased moving structure reference error. Faster
global motion response recovered structures while increasing terrain noise.
Contrast/fractional-filtering adaptation did not resolve both regressions and
was rejected. These intermediate candidates are retained only as measurements.

The implementation subtracts a local luminance-variance allowance from inferred
reactivity near rest, fading it to zero by 0.25 pixels/frame. It keeps the existing
motion response, history filtering, depth rejection, clipping and sharpening.
Uniform lighting changes have zero local variance and retain full reactivity.
The shader property `_SamplingNoiseMotionLimit` names the motion cutoff; normal
materials use its 0.25 default. No extra texture reads, passes, render targets,
managed work or vendor-backend changes are required.

The current-turn shader is frozen separately from the earlier optimization
baseline. New GPU fixtures compare analytic, box-integrated subpixel fences at
0, 0.125, 0.25, 0.5 and 1 pixel/frame, measuring both reference error and temporal
variance of that error. Existing disocclusion, reset, nonfinite-history, alpha
and sharpening guards remain in the suite.

See the temporal measurement procedure in [taa-quality-testing.md](../taa-quality-testing.md).
Runtime results and the remaining limitations are recorded separately; the
paused launchpad fixture does not establish stability during exhaust, moving
vessels, orbit transitions or origin rebases.

Recorded runtime results, rejected candidates and remaining tradeoffs:
[2026-09-10 temporal stability results](../taa-temporal-stability-results-20260910.md).
