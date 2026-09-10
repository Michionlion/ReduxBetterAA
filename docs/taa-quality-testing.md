# Custom TAA / FSR2 Native AA / DLAA pixel comparison

Run from this checkout with its supported build installed and the local
`launchpad-fly-safe-15` harness fixture available:

```powershell
pwsh -NoProfile -File tools/Build-Beta.ps1
# Install Deploy/ReduxBetterAA.zip into mods/ReduxBetterAA before the next command.
pwsh -NoProfile -File tools/Run-QualityTests.ps1 -Label candidate
```

For moving thin-geometry investigation, `-Shimmer -ShimmerReplay` additionally
records every Custom source frame, raw depth and sanitized motion, initial
history/depth, and the camera's depth conversion parameters. Input/output color
is RGBA16F; replay depth/motion is RGBA32F, all bottom-row-first. This option is
intentionally disk intensive and remains test-only. The fixed 512/256 pixel
crops have a 32-pixel invalid boundary when replayed; do not measure that boundary.

`tools/Run-TaaReplay.ps1 -Plan <plan.json>` runs the optional editor experiment
outside the normal test suite. A plan supplies `input` (the captured Custom
folder), `output`, and an `arms` array containing `name`, shader asset path,
and optional `settings` entries with `name`/numeric `value`. Shader defaults
apply to new properties; the recorded baseline's other material values are
explicit in the replay runner. Both the final moving-detail shader and
`CustomTaaPanBaseline.shader` use `_MotionResponsePixels=8`; the new shader
accelerates response only in the lower half of that range. Before interpreting a
replay sweep, compare the frozen baseline with the actual player output and
verify that residual differences are small relative to the proposed improvement.
`tools/analyze-taa-replay.py <output> <capture-run>` computes interior spatial,
edge and temporal residual errors, plus a fixed launch-grating region.

`-ShimmerPath reverse-diagonal` records a separate validation path: 32 still
frames, 32 frames at .2 degrees yaw/.1 degrees pitch per frame, 32 reversing
frames, then 32 still frames. Capture a baseline and candidate for that same
path; the analyzer rejects mismatched paths. The default remains the original
.1 degree yaw pan. `-ShimmerJitterSweep` is an optional five-arm raster sampling
experiment; changing jitter requires fresh scene rendering and cannot be inferred
by replaying old inputs.

Shader-only changes can leave the managed DLL hash unchanged. The run manifest
also records the installed Addressables catalog SHA-256; retain the package and
bundle hashes with final results to identify the shader that actually executed.

Requires the matching game/vendor runtimes, pinned Unity compiler, sibling
ReduxTestHarness, Python, NumPy and Pillow. Close KSP2 before starting. The runner
does not download runtimes or change native game binaries. At 1440p the full raw
capture is approximately 7.5 GiB; preserve both baseline and candidate folders
when investigating regressions. Generated folders are ignored by git.

The suite loads and pauses the same local vessel fixture. Off at 200% width and
height supplies four frames for a linear 2x2-downsampled spatial reference.
Native Custom TAA (stability .99), FSR2 Native AA and DLAA M use sharpness zero.
Each has 120 warm-up frames, 16 consecutive stationary frames, eight half-degree
pan samples and 16 consecutive settle frames. The pan steps are driven through
the semantic camera API. Raw frame numbers expose their actual cadence.

Quality capture intercepts the exact production render hook before and after
the backend and redirects its destination only for the sampled frames, keeping
UI out. The Off reference uses a temporary image effect on the same physics
camera. RGBA16F files are little-endian and bottom-row-first; calculations use
linear RGB without tone mapping/clipping. PNGs convert to sRGB for viewing.
JSON records effective backend, frame, dimensions, jitter, GPU and Unity.

Analysis validates byte counts, finite pixels, settings, backend, dimensions,
consecutive sequences and timing completeness. It emits SHA-256 hashes,
per-pixel error/variation maps, pairwise MAE/RMSE/percentiles/PSNR, temporal
standard deviation and frame-to-frame MAE. Spatial reference scores and a fixed
central structure crop supplement the full-frame metrics. Run analysis again:

```powershell
python tools/analyze-aa-quality.py Artifacts/quality-candidate-TIMESTAMP
python tools/compare-aa-runs.py Artifacts/quality-baseline-TIMESTAMP Artifacts/quality-candidate-TIMESTAMP Artifacts/quality-review
python -m unittest discover -s Tests/Quality -p test_*.py
```

Three separate 30-warm-up/240-measured-frame windows per backend follow capture.
They record existing frame timing and resolve CPU submission plus test-only
P50/P95/P99 call timing and current-thread allocation counts around rendering.
The reported owned memory excludes the shared sanitizer, game targets and opaque
vendor internals. No GPU readback or file writing occurs in these windows.

Interpretation limits:

- Other AA outputs are comparisons, not ground truth. Low temporal variation
  can also come from blur. Inspect crops alongside scores.
- The supersampled reference is an approximation; render-scale LOD/effects,
  camera settling, cloud time and exposure can differ from the native render.
  It is not a motion ground truth and cannot certify ghosting during real flight.
- Whole-frame CPU timing may be unscaled frame time when Unity timing is absent.
  Resolve CPU submission is not isolated GPU execution. GPU results without
  samples are unavailable, never zero milliseconds.
- Sequential runs include thermal/clock/streaming noise. Compare the unchanged
  FSR/DLAA controls and repetitions before attributing frame changes to TAA.
- The analytic GPU fixtures establish narrow behavior guarantees. VAB, orbit,
  high-speed flight, exhaust, camera cuts and resource-loss matrices remain
  necessary for broad release acceptance.

The comparison command uses one fixed baseline reference for both runs, checks
the capture-script hash and runtime environment, and creates matched crops and
a combined gallery. See [recorded results](taa-quality-results-20260910.md).

`CustomTaaCostTests` supplies an additional isolated editor benchmark: alternate
the frozen baseline and production shader on native-size persistent textures,
run 32 complete pipelines per batch and fence once with a one-pixel readback.
This measures synchronized CPU+GPU wall cost, including submission and fence
overhead; it is not an isolated hardware GPU timestamp. The fixture is a cost
comparison, not a replacement for in-game quality or whole-frame measurements.

## Continuous split-screen videos

With FFmpeg on PATH, record TAA/DLAA, TAA/None, TAA/supersampling and
DLAA/supersampling, then make labelled MP4 review copies:

```powershell
pwsh -NoProfile -File tools/Run-QualityTests.ps1 -Label videos -VideoComparison
python tools/prepare-comparison-videos.py Artifacts/quality-videos-TIMESTAMP
```

Open the resulting `videos/index.html`. The recorder captures 840 consecutive
pre-UI comparison frames per clip, with independent AA histories and a visible
divider. All clips follow the same 14-second camera path: two seconds still,
five seconds panning, five seconds reversing and two seconds settling. Render
delta is fixed at 1/60 second and vessel physics is paused. Encoder backpressure
can slow capture wall time; playback rate does not measure game performance.

These visual clips retain the user's sharpness, TAA stability and DLAA preset;
the report records them. Supersampling is 200% on each axis, or four times the
native pixels. Settings are restored and the test adapter is removed afterward.
The capture component is test-only and is not included in the shipped mod.

The preparation script checks frame continuity, the camera path, history-reset
counts, divider pixels, 60-FPS cadence and duration. Three decoded RGB frames
must exactly match their source PNGs. It preserves the lossless RGB MKV masters
and produces CRF-10 H.264 MP4s with a label band above the unscaled scene. Use
the masters at native pixel size for fine-detail judgment: MP4 chroma subsampling,
compression and browser scaling can hide small differences. Budget about 10 GiB
for a four-clip 1440p run. Stock cloud/exposure effects can share state between
comparison passes; this camera test does not cover moving vessels or exhaust.

## Consecutive shimmer measurements

```powershell
pwsh -NoProfile -File tools/Run-QualityTests.ps1 -Label shimmer-baseline -Shimmer
# Install the candidate, then use the identical fixture:
pwsh -NoProfile -File tools/Run-QualityTests.ps1 -Label shimmer-candidate -Shimmer
python tools/analyze-shimmer.py Artifacts/quality-shimmer-candidate-TIMESTAMP --reference Artifacts/quality-shimmer-baseline-TIMESTAMP
```

`-Shimmer` captures Off at 200% per axis, Custom TAA, FSR2 Native AA and DLAA M.
Add `-ShimmerSweep` for the history, depth-edge, reactivity and response ablations.
Each arm warms up for 120 frames, then captures 128 consecutive normal-renderer
frames at a fixed 1/60-second simulation delta: 32 still, 64 panning by 0.1 degree
per frame, and 32 settling. Vessel physics is paused. Camera distance/pitch/FOV
are 35/30/55, sharpness .24, TAA stability .99. This does not use comparison replay.

Native linear RGBA16F crops preserve individual pixels: 512x512 vessel/structure
at bottom-origin (1024,512), and 256x256 terrain at (256,896), in a 2560x1440
output. The SSAA reference uses the existing base bilinear 2x downsample. Sparse
same-frame input, raw UV motion, depth rejection, reactivity, history weight and
depth-edge maps help explain changes. Camera poses, selected backend, jitter and
history validity are recorded per frame. Missing frames, fallback or a Custom
history reset fail the capture. Analysis also rejects nonfinite pixels and
mismatched runtime environments. Budget about 1.5 GiB for four arms.

Stationary temporal standard deviation measures shimmer. During a pan, actual
scene movement also changes pixels: use adjacent changes in the residual against
the same baseline SSAA frames, together with spatial RMSE and native-size crops.
The last 16 settle frames exclude the initial convergence period; early settle
RMSE is reported separately. The fixed initial edge mask applies only to the
initial stationary phase. Neither lower variance nor lower residual change alone
establishes better quality: blur can improve both. Unchanged FSR/DLAA controls
expose scene drift between runs.

Three separate 240-frame Custom TAA profiles follow capture after test targets
are destroyed. They assert zero managed allocation in the resolve hook after
warm-up and record submission timing, frame timing and owned memory. GPU frame
time is unavailable when there are no GPU timing samples. `shimmer-cost.json`
from `CustomTaaCostTests` compares the current-turn frozen shader and candidate
with the same three-blit pipeline; it includes CPU submission and a GPU fence.

Create synchronized native-crop review clips (FFmpeg required):

```powershell
python tools/prepare-shimmer-review.py Artifacts/quality-shimmer-baseline-TIMESTAMP Artifacts/quality-shimmer-candidate-TIMESTAMP
```

This writes `review/index.html`, lossless RGB masters, MP4 previews and selected
native PNGs. Frame counts/cadence and a lossless decoded frame are verified.
See [measured temporal stability results](taa-temporal-stability-results-20260910.md).
