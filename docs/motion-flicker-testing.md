# Motion cadence capture and candidate testing

This developer-only adapter uses the existing `ReduxTestHarness` extension API.
It observes each normal rendered frame at Better AA's temporal resolve boundary.
It does not use a video clock, synchronous `ReadPixels`, or physics changes.
Nothing from `tests/Motion` is included in the production mod.

## Run on this workspace

Close the game before starting. From `G:\KSP2\ReduxBetterAA`:

```powershell
# Preferred automatic detector: motion/depth only, all three native backends.
.\tools\Run-MotionTests.ps1 -Script .\tests\Motion\detect.lua -Label motion-check

# Reproduce the experimental candidate matrix, including paired scene color.
.\tools\Run-MotionTests.ps1 -Script .\tests\Motion\candidates.lua -Label motion-candidates

# Native-pixel source/output strips to inspect the vector-filter quality tradeoff.
.\tools\Run-MotionTests.ps1 -Script .\tests\Motion\detail.lua -Label motion-detail
```

Requirements: the installed game and Better AA, adjacent `ReduxTestHarness`, the
installed Unity editor's C# compiler, and Python with NumPy/Pillow. Paths for the
game, editor, Python and artifact root are runner parameters. Only video analysis
requires `ffmpeg` and `ffprobe`.
The optional `plot-motion-comparison.py` summary figure also uses Matplotlib.

The runner compiles the optional adapter, installs the harness, backs up any
existing Better AA visual-test adapter and config, runs/terminates the game, then
restores that adapter and exact config. It verifies the production DLL/config
hashes and runs the capture validator. Run folders include script, adapter,
source and fixture hashes. The harness remains installed for future tests.
If the game cannot exit, restoration is deliberately deferred; the error names
the run folder containing the backup. Do not change files in the running game.

The local fixture `ReduxTestHarness/fixtures/local/motion-day-descent-280.json`
was prepared for this investigation. Its SHA-256 is
`06767c42080d491a22ef5f14d79a237092da0232a21879bb1f5381d03d46c411`.
It is private, ignored test data. Each arm reloads it. The preparation script can
reproduce it from the original Fly Safe-2 quicksave and launchpad Fly Safe-15
fixture; source hashes and modifications are in the adjacent manifest. It
creates a separate **initial condition**, refuses to overwrite an existing
destination and never edits the original save. This is not a flight automation
feature or a change to the production simulation.

```powershell
python .\tools\prepare-motion-fixture.py <source-quicksave.json> <new-fixture.json> --scene-source <launchpad-fixture.json>
python -m unittest discover -s tests/Motion -p test_*.py
```

## Capture contract

`ReduxBetterAA.Motion.start(label, count, colorCapture, detailCapture)` records
8–600 consecutive resolve frames. Color defaults to true; native detail defaults
to false and limits the count to 240. `ready()` waits for all pending asynchronous
readbacks and writes the data after the observation window.

- Motion-only: three 64 x 36 RGBA32F readbacks (108 KiB/frame, approximately
  50.6 MiB for 480 frames). No scene output redirection or color copy.
- Paired color: adds pre-AA and post-AA 320 x 180 RGBA16F scene samples. One
  source-sized intermediate ensures the output sample is from the same resolve.
- Native detail: adds central 1024 x 256 source/output strips, preserving native
  pixel scale; approximately 960 MiB of extra readback data for 240 frames.
  Crop origin and dimensions are recorded. These are inspection crops, not a
  supersampled reference image.
- Metadata includes render frame IDs, real timestamps, fixed-step timestamps,
  frame durations, speed/altitude, raster camera pose and matrices, jitter,
  backend identity, resets and vessel viewport position. The camera experiment
  records its actual raster pose before restoration, not the later game pose.

Data are little-endian float32 or float16 RGBA streams, bottom-up in the tested
D3D11 player. A point-sampled sparse vector/depth grid is intended for coherent
terrain regions; it cannot diagnose every tiny object or a terrain seam.
The format names describe exported sample channels, not original GPU formats.
The test adapter allocates readback arrays; it is not a zero-allocation production
render path. Readback queues and frame counts are bounded, and no file writes
occur inside the capture window.

## Automatic diagnosis

`analyze-motion-series.py` runs automatically and emits `motion-analysis.json`
plus per-arm plots and scene contact sheets where color was captured. It checks:

1. Consecutive frame IDs, increasing timestamps, expected backend, complete
   readbacks and finite samples.
2. Actual rendered FPS and how many renders occur between physics steps.
3. Terrain vector magnitude on fixed-step versus inter-step renders, correlation
   with physics steps and actual camera translation.
4. Static-world vector reconstruction using depth and current/previous camera
   matrices. Both Y orientations are audited; the tested D3D11 convention is
   `False`. Backend signs are separately audited (DLAA/FSR2 `-,-`; TAA `+,+`).
5. Reset-frame plus two-following-frame exclusion for steady-state scores.
6. Consumed vector variation, EMA recurrence correctness and focused-vessel
   high-frequency viewport motion.

`physics_stepped_valid_camera_motion` means the cadence was detected **and the
vectors agree with the stepped presentation**. It is not a claim that AA is
receiving corrupt data. Insufficient movement or inter-step sampling produces
an explicit inconclusive classification; a moving camera with absent vectors
does not pass as stationary. A passing Lua test means the capture succeeded,
not that flicker was absent or image quality improved.

For an intended smoothing fix, apply an optional cadence gate to a moving arm:

```powershell
python .\tools\analyze-motion-series.py <run-or-arm-folder> --require-smooth
python .\tools\analyze-motion-detail.py <native-detail-run-folder>
python .\tools\analyze-motion-videos.py G:\KSP2 <video-analysis-folder>
```

The optional gate fails on stepped or inconclusive captures. It does not replace
the vector-coherence, vessel-judder and visual-quality gates. Native-detail
analysis reports approximate motion-compensated changes in static background
regions and consecutive vessel crops. It has no perfect reference and cannot
select a winner from one scalar. Video frequency analysis verifies actual frame
timestamps and rejects variable spacing rather than silently assuming 120 FPS.

The completed investigation and every rejected experiment are recorded in
[decision 0043](decisions/0043-physics-cadence-motion-investigation.md).
