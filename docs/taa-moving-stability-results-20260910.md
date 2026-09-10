# Custom TAA moving thin-geometry results - 2026-09-10

The installed update reduces crawling/shimmer on the measured launchpad grating
by **16.3% during the normal pan** and **6.7% during a faster diagonal reversal**,
using temporal error against matching supersampled frames. Normal-pan grating
spatial error improves 1.1%; the faster test increases it 0.6%. This is a measured
improvement, not elimination of moving aliasing or a claim of overall DLAA parity.

- [Normal-pan comparison clips](../Artifacts/quality-pan-adaptive-final-20260910-113314/review/index.html)
- [Faster diagonal reversal clips](../Artifacts/quality-pan-reversal-adaptive-final-20260910-113440/review/index.html)

Each viewer contains Previous TAA / New TAA / DLAA M / FSR2 Native AA / SSAA 200%,
with native-pixel viewing, slow playback, representative PNGs and lossless RGB
masters. Previous TAA includes the stationary fix that the user already reviewed.
The links above open the local review files.

## Controlled player measurements

KSP2 Redux 0.2.9.0.104521-beta (`d299b906`), Unity 6000.5.8f1, D3D11,
RTX 5070 Ti, Windows driver 32.0.16.1088. Editor: 6000.4.1f1.
Paused `launchpad-fly-safe-15` / Fly Safe-15 fixture, 2560x1440, native AA,
sharpness .24, TAA stability .99, unchanged .75/8-sample jitter, DLAA M.
Orbit distance 35, initial yaw 0/pitch 30, FOV 55, 120 warmup frames.

Each arm captures 128 consecutive frames at a fixed 1/60 delta: 32 still,
64 moving, 32 settling. Normal pan advances yaw .1 degree/frame. The second
path advances yaw .2/pitch .1 degree/frame for 32 frames, reverses for 32,
then stops. These are normal production-renderer captures before UI.
Both versions use the same baseline SSAA reference for their path.

Structure crop: bottom-origin `(1024,512,512,512)`. Terrain:
`(256,896,256,256)`. The grating region was selected from the baseline image,
before final player validation: x=80..431/y=40..229 within the structure crop.
Calculations use native linear RGB. Temporal error is mean adjacent-frame change
in the residual against corresponding SSAA frames, not raw variance during motion.

| Path / region | Measurement | Previous TAA | New TAA | Change |
|---|---|---:|---:|---:|
| Normal / grating | Temporal error | .02204067 | .01844462 | -16.3% |
| Normal / grating | Spatial RMSE | .07640015 | .07556273 | -1.1% |
| Normal / structure | Temporal error | .01940401 | .01781193 | -8.2% |
| Normal / structure | Spatial RMSE | .05599849 | .05467775 | -2.4% |
| Normal / terrain | Temporal error | .01271188 | .01259890 | -0.9% |
| Normal / terrain | Spatial RMSE | .02234655 | .02192626 | -1.9% |
| Reversal / grating | Temporal error | .03223563 | .03007075 | -6.7% |
| Reversal / grating | Spatial RMSE | .07401320 | .07444888 | +0.6% |
| Reversal / structure | Temporal error | .02815229 | .02747349 | -2.4% |
| Reversal / structure | Spatial RMSE | .05585448 | .05522688 | -1.1% |
| Reversal / terrain | Temporal error | .01688071 | .01711492 | +1.4% |
| Reversal / terrain | Spatial RMSE | .02371637 | .02307176 | -2.7% |

Stationary structure variation falls a further **38.7%** on the normal path
(.00103025 to .00063199), with stationary spatial RMSE also improving.
Late-settle structure variation improves 26.5% on the normal path and 34.6%
after reversal. Full stationary/settle measurements, including small spatial
tradeoffs, are in each run's `shimmer.json`.

The faster-terrain temporal increase remains a limitation. The earlier global
four-pixel response candidate increased both faster-terrain temporal error
(3.8%) and spatial error (3.5%); it was rejected. The final curve restores the
original large-motion history response and substantially reduces that tradeoff.

DLAA still reconstructs moving fine detail more cleanly. On normal-pan grating,
its temporal error is .01802440 versus Custom's .01844462, while spatial RMSE
is .06585729 versus .07556273. FSR2 records .01770951 / .07051381. During reversal,
DLAA's temporal error is .02657684 versus .03007075 for Custom. These are fixture
results, not general backend rankings.

## Implementation and rejected approaches

Only the production Custom TAA shader changes:

1. Reconstruct current color with a steadier Gaussian footprint using the existing
   nine neighborhood reads at raster centers. The previous bilinear sampling
   changed smoothing with sample phase and also filtered every neighborhood tap.
2. Use a slightly sharper nine-read cubic history filter to preserve fine detail
   through repeated reprojection, retaining clipping and sharpening bounds.
3. Discount a small amount of local sampling variance during motion, while
   preserving full reactivity for uniform lighting/emission changes.
4. Refresh small displacements faster, smoothly rejoining the original motion
   response at half of the existing eight-pixel range. Stationary weighting and
   high-speed history limits retain their established configuration.

No texture reads, passes, targets, or managed per-frame work were added.
See [decision 0040](decisions/0040-custom-taa-moving-thin-geometry.md) for named
parameters and the response formula. Vendor backends, motion generation, jitter
and the F10 comparison implementation were not changed in this investigation.

Captured-input ablations rejected changed motion signs/scales, center-only
motion selection, cubic current filtering, overly soft current filters,
over-sharp history, and broad history retention that softened moving structures.
Fresh raster tests of .75/1.0 jitter spread and 8/16/32 samples did not improve
both structure stability and detail. The eight-sample sequence remains intact.

## Validation and cost

- **103/103 EditMode tests pass**, with zero skipped/failed tests. Six analytic
  moving-fence cases cover horizontal, vertical, diagonal and reversed directions.
  An independent 8x8 pixel-box reference
  improves in every case, for both spatial MSE and temporal residual variance.
  Velocities are (.25,0), (.5,0), (1,0), (0,.5), (.5,.5), and (-.5,.25) pixels/frame.
  MSE reductions range from 10.1% to 53.3%; residual-variance reductions range
  from 8.2% to 54.0%.
- A separate thin-object sequence checks startup, motion, reversal and stop,
  asserting that no visible trail persists outside the current filter footprint.
  Uniform emission, disocclusion, invalid/history reset, finite pixels, alpha,
  sharpen halos and the Catmull-Rom reference-kernel guard also pass.
- Both final game suites pass **10/10 assertions**, with continuous expected
  backend frames and no Custom history reset. Reports contain zero errors or
  warnings. Player logs retain the known single vegetation startup bypass,
  also present in the baseline; no new recurring Better AA errors were found.
- Six separate 240-frame profile windows report **zero managed resolve bytes**.
  Resolve CPU averages .0299-.0321 ms, hook P95 .0354-.0419 ms. Whole-frame
  averages are 6.93-7.32 ms, with P95 7.34-8.14 ms. These windows run after
  capture resources/readbacks are gone. Owned targets remain **84.375 MiB**.
- Alternating synchronized editor pipeline cost: median **.25194 ms previous /
  .25928 ms new**, a .00734 ms increase (2.9%). This includes CPU submission,
  driver and a one-pixel fence, not an isolated hardware GPU timestamp. Batches
  span .24267-.26992 ms; do not infer gameplay FPS or a statistically established
  cost difference from this small sample. No player GPU timing samples exist.
- Four Python analysis tests pass. Final videos contain 128 frames at 60 FPS;
  decoded lossless frame 63 matches the uncompressed source byte-for-byte.

## Evidence and remaining limits

Normal baseline: `Artifacts/quality-pan-replay-baseline-20260910-110010`.
Reversal baseline: `Artifacts/quality-pan-reversal-baseline-20260910-112118`.
Final runs are the two linked viewers above. Experimental inputs, plans, scalar
results and previews are retained in `Artifacts/quality-pan-investigation-20260910`;
discarded derived raw replay frames are reproducible from the preserved inputs.
The optional replay runner is outside the ordinary test suite. Cropped replay
was calibrated against the full player before screening candidates.

The installed package SHA-256 is
`3E934225E0C8DBB9BBA96885BDD5F27E8D540A949D74B66F028CEB2C8EED1BD2`.
The final evidence snapshot retains sources, test XML, costs and installed
package/catalog/bundle hashes; shader identity must not rely on DLL hash alone.
The user's configuration is restored byte-for-byte. The temporary adapter was
removed and the game closed after validation.

SSAA is an approximate spatial reference: LOD, stock temporal effects and
rasterization can vary between runs. Lower temporal error can reward blur, so
it is paired with detail metrics and review clips. Tiny differences should not
be overinterpreted. The paused launchpad and analytic objects do not establish
behavior for exhaust, transparencies, moving vessels in flight, VAB, orbit
transitions or origin rebases. Moving aliasing remains visible, particularly
at higher speeds and on details the current raster never sampled.
