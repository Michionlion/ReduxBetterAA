# Decision 0043: speed-dependent motion flicker investigation

Date: 2026-09-11. Scope: shared motion inputs for the existing native AA backends.

## Finding

The dominant oscillation in the supplied clips is consistent with the game's
50 Hz physics cadence. In the instrumented, continuously locked following-camera
captures, camera/world translation advances on physics steps and holds between
them. Raw motion is consequently nonzero on one render and almost zero on the
next. The vectors largely describe that stepped presentation correctly. Faster
travel makes each step larger. This is not a claim that all camera motion runs
at 50 FPS; see the subsequent interpolation audit in decision 0044.

This is different from the previously repaired invalid foliage object history.
It is also different from a floating-origin discontinuity: origin resets appear
as isolated large events and must be excluded from steady-state cadence scores.

**Decision: keep production AA unchanged.** None of the tested simple candidates
passed both stability and coherence checks. The accepted changes are the detector,
reproducible test candidates and this evidence record. A quieter vector debug view
alone is not sufficient evidence of a fix.

## Evidence and scope

All generated evidence is under `Artifacts/motion-investigation-20260911`.
Original user videos and saves were preserved. No physics timestep, solver,
gravity, forces or Rigidbody interpolation settings were changed.

- Five supplied 2560 x 1440, 120 FPS videos were decoded frame by frame. The
  magnitude, normalized and fast vendor-input recordings have prominent peaks
  around 50 Hz in fixed terrain regions. Their colored encodings are nonlinear;
  video RGB differences are not vector magnitudes. A recording can skip game
  renders, so video frequency evidence prompted instrumented verification.
  Timestamp-validated decoded frame counts are 191, 341, 600, 577 and 165 in
  filename order. The normalized clip advertises 342 container samples, while
  both ffprobe's decoded-frame count and ffmpeg's displayed frames are 341;
  analysis uses those real timestamps and does not invent a replacement frame.
- `baseline-metadata-20260911-182145` contains the stationary control and natural
  launchpad sequence. The paused control has zero raw motion despite active
  jitter. The final `launch-fast-dlaa` label is misleading: staging deployed a
  parachute, and this interval is slow descent, approximately 8 m/s. Its raw
  motion correlates 0.991 with physics steps and 0.997 with camera translation;
  static-ground reprojection agrees to about 0.00074 pixels at the 95th percentile.
- The original orbital quicksave never entered the atmosphere: its perigee is
  above the atmosphere. That test was cancelled and supplies no fix evidence.
- A separate, hash-identified **synthetic initial condition** retains the source
  vessel/parts, places it 8 km over the launchpad region at 280 m/s radial descent,
  and uses the launchpad fixture's daylight time. Each candidate reloads that same
  fixture and then runs the ordinary game physics. It is not described as an
  original recorded save. The preparation script and fixture manifests record
  the changes and source hashes.
- Per-render capture reads raw motion, consumed motion and depth asynchronously
  at `TemporalRenderHook.Render`, with actual render/fixed clocks and camera
  matrices. Small same-frame pre-AA/post-AA color images provide scene context.
  Capture frame IDs must be consecutive; missing data, nonfinite values,
  backend fallback and unfinished readbacks invalidate the capture.

**Subsequent qualification:** the original harness reapplies its orbit request
in every LateUpdate with `SetGimbalState(state, false)`, which resets the rig's
input smoothing. These runs therefore cannot establish freely running camera
behavior. `Test.camera.release()` now permits setting a pose once and then
resuming native rig control. Live Auto uses smoothing time 0; Cinematic uses
0.35 seconds, and orbit input is integrated each rendered frame in either mode.
None of the unsuccessful motion-only fixes is promoted by this qualification.

The installed Redux camera source updates its view in
`UniverseCameraManager.OnLateUpdate`, but the target/observer presentation still
advances with physics in these runs. Existing `CameraAnchorSmoothing` smooths
camera transitions; it is not continuous fixed-step interpolation. Better AA's
three native backends share projection jitter and motion sanitization. The
sanitizer's rejection/fallback option is disabled in these captures; backend
component-sign conversion remains active.

## Experiment ledger

This ledger includes unsuccessful setup attempts so they cannot be mistaken for
successful tests. Numeric results and acceptance decisions follow the ledger.

| Attempt | Outcome / interpretation |
| --- | --- |
| Initial capture with private nested metadata records | Rejected: Unity serialized incomplete frame metadata. Public serializable records plus JSONL validation fixed the recorder. |
| Natural launch sequence | Valid stationary/slow-motion control; not a high-speed fixture. |
| Wait for natural reentry from the supplied orbital save | Cancelled: the orbit does not reenter. No candidate result. |
| Separate descending initial condition, first placement | Valid motion data but poor nighttime scene context. Retained separately. |
| Daylight descending fixture | Used for repeated candidate comparisons, with immutable source/fixture hashes. |
| Zero projection jitter, DLAA | Physics-step oscillation persists. Removes useful temporal sample coverage without fixing the cause. |
| Half projection jitter, DLAA | Physics-step oscillation persists. No demonstrated reason to reduce sampling quality. |
| Test shader bundle via legacy BuildPipeline, normal and stripped version | Both failed to load in the newer player. Infrastructure failures, not AA results. |
| Player texture-lerp shader lookup at the main menu | Unavailable there. Flight's direct `PostProcessResources` reference works; shader algebra/orientation independently checked against captured vectors. |
| 50% current / 50% previous motion EMA, first complete matrix | Executed on DLAA/TAA/FSR2. An origin-reset frame initially seeded the filter history; fixed before the final comparison. |
| Camera-only render interpolation, first complete matrix | Executed on all three backends. Raster pose was restored before image-effect metadata capture; corrected metadata to record the actual raster pose before interpreting vessel motion and matrix agreement. |
| Final EMA and camera interpolation matrix | See final measurements below. |
| Motion-only detector | Passed across DLAA/TAA/FSR2; correctly separates paused control from physics-stepped motion, without video/color readback. |
| Native-pixel EMA comparison | Rejected EMA: residual frame changes increased in all three backends while source residuals stayed within 1%. |

All candidate patches live in the optional test adapter, and are removed on
teardown. They do not alter the shipped rendering implementation or defaults.

## Final measurements

Environment: Redux `0.2.9.0.104521-beta`, commit `d299b906`; Unity `6000.5.8f1`;
Windows D3D11; RTX 5070 Ti, driver `610.88`; 2560 x 1440, native render scale;
Better AA `0.6.0`, source HEAD `61799dfac7d1d8e19381234a5faf8bf019528d93`.
The candidate adapter fixes DLAA preset K and sharpness 0.24. Original settings,
including the original DLAA preset, are restored after each run.

`candidates-final-20260911-185257` passed all 11 arms, 480 consecutive frames per
arm. They ran at 94–107 measured FPS with a 120 FPS target; the detector uses actual
timestamps and fixed-step membership, so it does not assume a perfect 120 FPS
sampling clock. Moving baseline ground vectors were about 0.55–0.64 pixels per
physics-step render and approximately 0.000007 pixels on inter-step renders.

Variation below is the RMS change in sampled terrain vector magnitude between
adjacent valid frames, in native pixels. Vessel judder is high-pass viewport RMS
across X/Y, in native pixels; it excludes slow camera travel and reset guards.

| Backend / candidate | Raw variation | Consumed variation | Step correlation | Vessel judder |
| --- | ---: | ---: | ---: | ---: |
| DLAA baseline | 0.5756 | 0.5754 | 0.9840 | 0.039 |
| DLAA zero jitter | 0.5467 | 0.5466 | 0.9950 | 0.035 |
| DLAA half jitter | 0.5491 | 0.5489 | 0.9948 | 0.036 |
| DLAA motion EMA | 0.5533 | 0.1942 | 0.9919 | 0.035 |
| DLAA camera interpolation | 0.0864 | 0.0864 | -0.0604 | **16.690** |
| TAA baseline | 0.5482 | 0.5481 | 0.9950 | 0.035 |
| TAA motion EMA | 0.5496 | 0.1937 | 0.9915 | 0.036 |
| TAA camera interpolation | 0.0658 | 0.0657 | -0.1425 | **16.688** |
| FSR2 baseline | 0.5525 | 0.5524 | 0.9949 | 0.034 |
| FSR2 motion EMA | 0.5560 | 0.1941 | 0.9921 | 0.034 |
| FSR2 camera interpolation | 0.0717 | 0.0717 | -0.1172 | **17.191** |

The small absolute differences between separately timed raw baseline/jitter arms
are not an improvement: the near-zero/nonzero cadence persists. EMA's within-arm
consumed-versus-raw variation reduction is approximately 65%. Its consumed
vectors differ from the real frame displacement by about 0.185 pixels on average,
versus about 0.0002 pixels for baseline sign conversion. Its actual 50% recurrence
matches captured vectors to approximately 0.000035 pixels, verifying that this
was an executed GPU experiment, not a mislabeled option.

Camera interpolation reduces raw variation by approximately 85–88% and preserves
static-ground vector/matrix agreement (95th percentile error about 0.0002 pixels).
However, the vessel moves tens of pixels against that steadier background.
This is a demonstrated quality regression and rejects the camera-only approach.

![Measured raw/consumed motion and vessel displacement](../../Artifacts/motion-investigation-20260911/candidates-final-20260911-185257/motion-comparison.png)

`detail-final-20260911-190003` passed all six baseline/EMA arms with 240 native
1024 x 256 source/output strips each. The static-background residual below warps
the previous image by measured real motion, excludes the vessel/crop borders and
reset guards, and then measures mean absolute linear-RGB change. Lower means less
unexplained temporal change in that region, not universally better image quality.

| Backend | Baseline residual | EMA residual | Change | Source residual change |
| --- | ---: | ---: | ---: | ---: |
| DLAA K | 0.00036246 | 0.00041608 | **+14.8%** | -0.63% |
| Custom TAA | 0.00022251 | 0.00030276 | **+36.1%** | +0.23% |
| FSR2 | 0.00085088 | 0.00093463 | **+9.8%** | -0.70% |

Native vessel crops show baseline AA already stabilizing the jittered input;
the EMA provides no evident vessel-detail advantage in the inspected sequence.
Combined with increased background residuals in all three modes and loss of
vector accuracy, this rejects the filter. These are absolute small residuals,
one fixture and an approximate warp; the percentages are not perceived-quality
ratings or a universal ranking of backends.

`detector-final-20260911-185736` passed the paused paired-color control and 480
motion-only frames per backend. Its classifier reported stationary for the
paused scene and `physics_stepped_valid_camera_motion` for all three moving arms.
Motion-only measured FPS was 103.0 / 116.0 / 115.6 for DLAA / TAA / FSR2; preceding
no-readback windows measured 107.3 / 117.9 / 117.2. These consecutive windows
suggest modest observer cost, but different flight moments and the frame cap
prevent treating them as an isolated performance benchmark. The production cost
of this change is zero because the recorder/candidates are absent from the mod.

Validation: the C# adapter compiled against the installed player and executed in
the above runs; eight analyzer tests passed, covering valid 50 Hz steps, missing
motion, insufficient sampling, malformed captures, nonfinite data and EMA
sign/recurrence checks. Python/PowerShell static checks and `git diff --check`
passed. No production shader/package was rebuilt or changed. The production DLL
SHA-256 remained `6ca72d53b997e0ce7dbf1df754cd93ff6c5f8d906770352cf5113bc51855cb85`;
runner manifests verify exact config restoration. Existing plume/fairing asset
load failures, Discord connection errors, blur-camera warnings and audio/OTA
messages recur in both the baseline and final player logs. No motion-adapter
exceptions were found. These pre-existing content failures limit how broadly
the scene's visual quality should be generalized.

The delivered adapter/runner were rerun successfully as
`detector-delivery-20260911-191137`, including motion-only and paired-color capture.
`delivery-validation.json` verifies the compiled source hash, original visual
adapter, first-run settings and production DLL restoration, and clean harness
repository. The optional smoothness gate rejects the stepped baseline and passes
camera interpolation's cadence, while its independent vessel-judder score rejects
that candidate. This explicitly tests why the cadence gate alone is insufficient.
The final game shutdown emitted an unattributed ComputeBuffer disposal warning;
the recorder owns render textures/materials/commands and creates no ComputeBuffer.

## Candidate costs and alternatives

- Jitter amplitude changes add essentially no rendering work but surrender sample
  coverage. Their lack of a cadence improvement makes that tradeoff unattractive.
- Motion EMA requires one extra fullscreen pass and two RG32F textures (56.25 MiB
  at 2560 x 1440), and introduces vector lag. It must discard history across
  resets. Recorded frame rates include capture overhead and are not an isolated
  GPU cost benchmark.
- Camera-only interpolation performs a small amount of CPU transform work and
  adds up to one 20 ms physics tick of camera latency. Keeping the vessel's visual
  pose coherent with that camera is essential; measuring only terrain misses the
  regression.
- Capping rendering to the physics rate sacrifices frame rate and responsiveness;
  increasing the physics rate changes the simulation configuration and costs CPU
  time. Neither meets this task's requirements and neither is proposed for release.
- The coherent upstream approach is render-pose interpolation shared by camera
  anchors, moving vessel visuals and previous-frame matrices, including origin
  reset handling. Physics integration can remain unchanged, but this needs a
  supported presentation layer in Redux and validation of all participating
  renderers. It is not established by a camera-only patch.

## Limits

This investigation tests a controlled descending vessel and a launchpad control
on one machine. Native-detail strips cover a limited region and short interval;
they do not certify every detail, disocclusion, camera reversal or DLAA preset.
No candidate passed the earlier gates to justify release-level testing. Mean
image change includes legitimate motion and is not used alone to choose a winner. Backend selection
alone is never counted as visual success. Launchpad shadow and terrain-join
flicker was not separately reproduced or fixed here.

See [the capture workflow](../motion-flicker-testing.md) for reproduction and
interpretation. Related primary documentation:
[Unity fixed updates](https://docs.unity3d.com/6000.0/Documentation/Manual/fixed-updates.html)
and [per-renderer motion vector generation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Renderer-motionVectorGenerationMode.html).
