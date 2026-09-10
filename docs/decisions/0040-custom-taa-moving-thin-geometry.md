# 0040 - Moving thin geometry in Custom TAA

Phase 3, SPEC 11.3-11.6. The user confirmed the stationary shimmer improvement
but reported traveling pixels on thin geometry while panning. Freeze that
installed shader as the new baseline and target motion without losing the
stationary improvement or masking flicker with blur.

The investigation records consecutive normal-renderer color, depth, sanitized
motion, initial history and jitter. The optional EditMode replay uses these
exact inputs and excludes a 32-pixel border around its artificial crop boundary.
Validate it against captured player output before selecting changes, then run
the selected shader in the real player again. Preserve the shared supersampled
reference and unchanged DLAA M/FSR2 controls.

Initial baseline replay RMS differences from the player were .000755 in the
structure crop and .000544 in terrain (linear RGB). Small floating-point and
subtexel filtering differences remain between cropped editor replay and the
full player. This replay is a candidate-screening tool, not a substitute for
player acceptance or a performance measurement.

Ablations test motion scaling/sign/dilation, current reconstruction and history
interpolation separately. Jitter changes require fresh rasterized inputs and
are tested in the player. Temporal residual change is paired with spatial and
edge reference error, analytic thin geometry and response checks. Do not select
a smoother image solely because its temporal variation is lower.

Artifacts for this investigation: `Artifacts/quality-pan-investigation-20260910`.
Derived rejected replay frames can be regenerated from the recorded inputs and
saved plans; metrics, representative previews and exact experimental shaders
are retained. The production vendor backends and shared motion sanitizer are
outside this change's scope.

## Selected implementation

Reconstruct current color from the existing nine neighborhood reads at raster
centers with a fixed Gaussian footprint (sharpness 3), instead of independently
bilinear-filtering every tap. Use a sharper nine-read cubic history kernel
(coefficient .75; .5 reproduces Catmull-Rom). Existing neighborhood clipping
and output sharpening bounds still limit ringing. A .3-sigma moving sampling
allowance avoids treating small coverage differences as new lighting; uniform
lighting/emission changes have zero variance and retain their reactive response.

The default motion range remains eight pixels. Accelerate response only in its
lower half, using `f + f * saturate(1 - f / .5)` for normalized motion `f`.
This approaches twice the response near zero and rejoins the original curve
at half-range. An earlier global four-pixel range improved grating but worsened
terrain in the faster reversal test; it was rejected. Jitter spread/length,
shared motion generation, vendor backends and resource ownership remain intact.

All algorithm values are named shader properties. The production change is
confined to the Custom TAA shader, with no new texture reads, targets, passes,
or managed work. Captures, replay, sweeps and image analysis are developer tools.

See [moving stability results](../taa-moving-stability-results-20260910.md) for
fresh player comparisons, cost, validation, and remaining quality limits.
