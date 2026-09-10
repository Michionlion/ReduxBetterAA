# Custom TAA temporal stability - 2026-09-10

This records the earlier stationary fix. The installed shader has since been
updated by the [moving-geometry investigation](taa-moving-stability-results-20260910.md).

The installed candidate suppresses false reactive rejection near rest. In the
paused launchpad test it reduced stationary vessel/structure variation by 86%
and settled variation by 83%, while retaining essentially the original moving
spatial detail. This is a substantial reduction, not complete elimination of
shimmer: DLAA still reconstructs the moving launchpad grating more cleanly.

[Before/after clips and native crops](../Artifacts/quality-shimmer-narrow-20260910-100222/review/index.html)
compare original TAA, improved TAA, DLAA M, FSR2 Native AA and SSAA 200% at matching
camera poses. Lossless RGB masters are included; MP4 previews can hide fine noise.

## Controlled measurements

Baseline: `Artifacts/quality-shimmer-baseline-20260910-092811`.
Candidate: `Artifacts/quality-shimmer-narrow-20260910-100222`.
Each captured 128 consecutive normal production-renderer frames per arm: 32 still,
64 panning at 0.1 degree/frame, 32 settling, fixed 1/60 delta, 120 warmup frames.
Physics paused, launchpad Fly Safe-15, distance 35/pitch 30/FOV 55, 2560x1440,
sharpness .24, TAA stability .99, DLAA M, native FSR2. The spatial reference is
Off at 200% per axis with base bilinear downsampling. Both runs use the original
run's exact reference frames. These are native linear-RGB crop measurements.

| Region and measurement | Original TAA | Improved TAA | Change |
|---|---:|---:|---:|
| Structure: still temporal std | .00745900 | .00103025 | -86.2% |
| Structure: still reference RMSE | .05558323 | .04916169 | -11.6% |
| Structure: pan residual change | .02091860 | .01939056 | -7.3% |
| Structure: pan reference RMSE | .05574614 | .05601581 | +0.5% |
| Structure: late-settle temporal std | .00690875 | .00116883 | -83.1% |
| Structure: late-settle reference RMSE | .05253319 | .05104170 | -2.8% |
| Terrain: still temporal std | .00076026 | .00023733 | -68.8% |
| Terrain: still reference RMSE | .02052558 | .02094272 | +2.0% |
| Terrain: pan residual change | .01271170 | .01271060 | -0.01% |
| Terrain: pan reference RMSE | .02234414 | .02234197 | -0.01% |
| Terrain: late-settle temporal std | .00100592 | .00034567 | -65.6% |
| Terrain: late-settle reference RMSE | .02020302 | .02051823 | +1.6% |

Late settle uses frames 112-127. Structure early-settle RMSE (96-103) was .0535251
original and .0533346 candidate. Lower pan residual change means fewer changes
in the error against corresponding SSAA frames; raw temporal variance during
motion also includes genuine scene changes and is not a shimmer score.

Unchanged controls were close between runs. Structure still std was .00344684
for DLAA and .00294457 for FSR; pan residual change was .01754957 and .01802192,
with pan reference RMSE .05128274 and .05262293 respectively. Stationary variance
alone cannot rank overall quality: it rewards blur, and small terrain spatial
error increases remain in the candidate. Visual inspection of the moving crop
confirms that DLAA retains cleaner thin grating detail.

## Change selection

The shader discounts luminance differences within local sampling variation only
near rest, with the allowance fading out by 0.25 pixel/frame. Existing motion
response, depth rejection, clipping and sharpening remain active. Flat emission
changes have no local variance and remain reactive. The production addition is
one named shader property and a few arithmetic operations; no texture reads,
passes, render targets or managed work were added.

Rejected alternatives are preserved in the artifact folders:

- No reactive rejection: about 92% less stationary variation, but moving structure
  RMSE rose from .05575 to .06994.
- One-pixel allowance: about 86% less stationary variation, but moving structure
  RMSE rose to .06186; more history softened moving detail.
- Faster global motion response: recovered structures but worsened terrain.
- Contrast/fractional-filtering adaptation: moving structure RMSE .06006 and
  terrain .02466, both worse than the original. Rejected before final deployment.

Raw motion direction was checked against consecutive SSAA frames; the original
`previousUv = uv - motion` direction is correct. Depth-edge removal did not fix
the stationary shimmer. See [decision 0039](decisions/0039-custom-taa-temporal-stability.md).

## Validation and cost

- Supported Unity/ThunderKit prepare, EditMode tests and packaging succeeded:
  **95/95 editor tests**. New analytic fence tests use an independent 8x8 box
  reference at 0, .125, .25, .5 and 1 pixel/frame. At .125 pixel/frame, reference
  MSE fell from .01544772 to .01277848 and residual variance from .01520167 to
  .01162269. At .25, .5 and 1 pixel/frame, results match the original shader.
- Reset/nonfinite history, disocclusion, moving depth-edge response, alpha,
  sharpening halos and uniform emission response guards pass. Four Python
  analysis tests pass; `git diff --check` passes.
- Final in-game capture: **10/10 assertions**, continuous frames, expected
  backends, no Custom history reset, finite raw pixels. Three separate
  240-frame profiles recorded **zero managed bytes** allocated in the resolve
  hook. No new recurring mod errors; the existing single vegetation startup
  bypass also appears in the baseline log.
- Synchronized editor wall cost, same three-blit pipeline, four alternating
  32-dispatch batches: median **.25074 ms original / .25167 ms candidate**.
  This includes CPU submission and the GPU fence; it is not a hardware pass
  timestamp. The difference is smaller than the batch spread (.239-.264 ms).
- In-game resolve CPU averages .0292/.0304/.0308 ms; hook P95 .0363/.0386/.0403 ms.
  Frame averages 6.89/7.21/7.20 ms and frame P95 7.29/8.01/7.89 ms. No GPU timing
  samples were available. Backend-owned targets remain **84.375 MiB**. These are
  steady-state measurements, not lifecycle recreation-spike measurements.

Runtime: Redux 0.2.9.0.104521-beta, Unity 6000.5.8f1, D3D11, RTX 5070 Ti;
editor 6000.4.1f1. The installed DLL SHA-256 is
`992C96809593A6A021B043D6AE56BE90DF314EBDBC0653C4D43D68612239C6BA`;
package SHA-256 is
`A08D2BE6DAF15EE173DC0EC86F0267043C85B669047CC4CEEDE85252FE15C4D4`.
The candidate's `evidence/` includes exact shader/test sources, the shader diff
against the current-turn baseline, editor XML, cost JSON and a 1,183-file hash
manifest. The original installed mod is preserved under the baseline artifacts.

The test adapter was removed, game closed and the user's DLAA M/.24/.99 settings
restored. The comparison server on port 8769 remains stopped. No vendor backend
was modified during this shimmer investigation.

This fixture does not establish universal stability or ghosting behavior for
moving vessels, exhaust, transparency, VAB, orbit transitions or origin rebases.
SSAA remains an approximate spatial reference because LOD/effects can differ.
The inferred reactive mask still lacks dedicated material/transparency data.
