# Northwest hillside flicker regression

This test uses the user's local Test quicksave, facing the northwest hills from
launchpad 4. The save is intentionally not committed. Fixture SHA-256:
`C0E289A1F7A413D45C48AD5A9FD7A66118252EF01E8CCFC46E744F5136B5D751`.
Place its unmodified copy at
`../ReduxTestHarness/fixtures/local/terrain-test-launchpad4.json`.

Close KSP2, install the built Better AA DLL, then from this repository run:

```powershell
pwsh -NoProfile -File tools/Run-TerrainTests.ps1 -Label terrain-regression
```

The runner compiles the optional test adapter against the installed player,
loads the fixture, preserves its camera, and captures paired production/bypass
runs for DLAA, TAA and FSR2, plus Off. Bypass disables only the new terrain-depth
projection correction in the same DLL. Saved preset, sharpness and all other AA
settings stay fixed. It then measures cost without readback, pans the camera,
reloads the save and performs normal launches. It never changes physics rates,
interpolation, transforms or solver parameters to improve the image.

The runner restores the exact prior AA config and optional adapter on exit and
records hashes. The tested production DLL remains installed. The original Test
save is not overwritten. The game must be closed for adapter replacement.

Outputs go under `Artifacts/terrain-investigation-YYYYMMDD/<label>-<timestamp>`:

- `run.json`: fixture, DLL, script, adapter sources and restoration evidence.
- `samples/<case>/terrain.json` and `frames.jsonl`: formats, settings, timestamps,
  consecutive Unity frames, matrices, jitter, reset and backend identity.
- Binary native 768×384 input/output RGBA16F and depth R32F; 512×288 context.
  Buffers are bottom-row-first. ROI origin is (768,896) from the bottom left.
- `terrain-analysis.json`, `samples/<case>/review`: native crops, difference
  maps, coherent-block time series and per-case scores.
- `samples/*-profile.json`: two 360-draw windows per condition and mode, with
  CPU elapsed time, actual frame time and managed allocation.
- `terrain-verification.json`: explicit pass/fail checks and paired reductions.

Captures use bounded asynchronous GPU readback and defer file writes until the
window ends. They consume disk space (roughly 1 GB per 128-frame case); keep the
artifact directory outside version control. Generated review videos are derived
afterward and are not used for measurement. A 120 FPS cap is requested, not
assumed: analysis checks real timestamps and uninterrupted render-frame IDs.

The acceptance signal averages 16×16 RGB blocks in the interior hillside, then
measures consecutive changes in clipped sRGB. It rejects the known coherent
flicker while suppressing unrelated independent pixel dither. For this exact
stationary view, corrected output must score below 0.00025 and improve at least
90% against its matching bypass. Every corrected terrain-depth projection must
match the actual raster in the same frame. Off must remain quiet and unfiltered.
Motion and view changes are excluded from the stationary threshold.

To analyze and verify an existing complete regression run:

```powershell
python tools/analyze-terrain-flicker.py <absolute-run-directory>
python tools/verify-terrain-fix.py <absolute-run-directory>
python tools/render-terrain-review.py <absolute-run-directory>
python -m unittest discover -s tests/Motion -p test_terrain_detector.py
```

For an isolated historical candidate, pass `-Script tests/Motion/<script>.lua`;
the automatic production acceptance is applied only to `terrain-test-regression.lua`
or when `-VerifyFix` is explicit. A successful capture of a rejected candidate
is valid evidence, not a successful fix.

The test adapter is inert without its launch environment. It contains historical
motion probes, but the terrain scripts do not enable them. The experiment record,
including failed harness attempts and measurement limits, is
[decision 0044](decisions/0044-northwest-hills-flicker-investigation.md).
