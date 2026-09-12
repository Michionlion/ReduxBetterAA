# 0044 — Northwest hills: align terrain depth with temporal jitter

Date: 2026-09-11. Phase 3/4, shared rendering inputs. The 50 Hz vessel-motion
investigation is closed at the user's request; it is not the explanation for
this defect. Decision 0043 remains historical evidence only.

## Reproduction and cause

The user's **Test** campaign, `quicksave_1`, has Test-1 on launchpad 4 facing the
northwest hills. The unmodified local fixture is
`ReduxTestHarness/fixtures/local/terrain-test-launchpad4.json`, SHA-256
`C0E289A1F7A413D45C48AD5A9FD7A66118252EF01E8CCFC46E744F5136B5D751`.
Saved distance is 728.088, heading 280.2093, pitch 22.5335. Loading and pausing
preserves this view. No launch or camera override is needed to reproduce it.

The flicker is caused by **different projection jitter in the terrain depth
draw and final terrain rasterization**. In the installed Redux assembly,
`PQSRenderer.DrawPlanet` calls `DrawPqsDepthNow` before normal camera rendering.
That immediate draw loads `targetCamera.projectionMatrix` and publishes
`_PQSDepthTexture`. Better AA previously applied jitter only later, at the
camera render callback. Consequently, terrain blending consumed depth rendered
without the current raster offset. Halton sample changes made hillside patches
change from frame to frame, even with the game paused and the camera exactly still.

Evidence supporting this diagnosis:

- Off is quiet; all three selectable temporal modes reproduce the defect.
- Disabling jitter removes it. Disabling shadows, changing transparent jitter,
  or using stable culling does not remove it from the measured hillside.
- Applying the upcoming *existing* jitter just around `DrawPqsDepthNow` makes
  its projection exactly equal to the later terrain raster on every captured
  frame. Correcting the material prepass or decal draw alone does not help.
- The same depth-only correction removes most coherent hillside variation in
  TAA, DLAA and FSR2, paused and unpaused. No clock or vessel interpolation change
  is involved.

Installed `Assembly-CSharp.dll` audited:
`8C3AED3C13270BF1F8FF9BC0560D9FE17B5A40FCBD5F993A064C93ED2795E0FD`.
Relevant decompilations and frame matrices are retained in the local evidence
directory. This conclusion is about this northwest-hills defect; it does not
establish that every shadow or terrain seam elsewhere has the same cause.

## Accepted implementation

`TerrainDepthJitterPatch` brackets only the existing immediate terrain depth
draw. The active backend provides its current sample and raster projection;
`AuxiliaryProjectionScope` restores the original projection in a Harmony
finalizer, including exceptions. It preserves distinguishable external camera
writes and avoids adding jitter when the main camera already owns that projection.

The three normal temporal backends implement the same projection-source
contract. Off, unrelated cameras, unsupported projection paths and suspended
comparison rendering do not acquire this scope. The change does not advance
the sequence, change jitter strength, modify filtering/history/sharpness,
allocate targets, introduce a GPU pass, or write physics state.

The managed DLL installed for validation has SHA-256
`41AD53E9B299BED5DF7EF6CAAA353A18BEAE92AEC9D9FBFB4721CD6CDA13E0F7`.
The original DLL is retained under `pre-fix-production` in the evidence directory.
Only this mod DLL was replaced; the existing player and shader assets are used.
This is a local working-tree build, not a published release or a new commit.

## Experiment ledger

All paths below are under `Artifacts/terrain-investigation-20260911`. Each Test
run records the fixture, installed DLL, adapter sources, script hashes, settings
restoration and capture metadata. Failed and rejected attempts are retained.

| Candidate | Outcome on the correct hills |
| --- | --- |
| Off | Quiet control; roughly 0.000025 coherent-block change |
| Transparent jitter enabled | No useful change in DLAA, TAA or FSR2 |
| Stable nonjittered culling | No useful DLAA improvement |
| Shadows disabled | No useful hillside improvement; rejected |
| Zero jitter | Nearly removes the signal; diagnostic only because it sacrifices temporal sampling |
| PQS material prepass aligned | No useful improvement in any of the three modes |
| PQS decal draw aligned | No useful improvement in any of the three modes |
| PQS depth, decal and material prepass aligned | Large improvement in all three modes |
| PQS depth plus decal aligned | Same useful result as depth alone; unnecessary extra scope |
| **PQS depth alone aligned** | **Accepted: smallest successful change** |

| Run | Purpose and outcome |
| --- | --- |
| `scout-20260911-193543` | Earlier native camera audit; close-pad images were not the user's defect. Fixed relative artifact-root handling. |
| `hills-scout-20260911-194447` | Earlier altitude predicate failed during vessel transition; no candidate evidence. |
| `hills-scout-valid-20260911-194608` | Cancelled while the user prepared Test; closed only the harness-owned player. |
| `Test-scout-20260911-200100` | Confirmed saved framing and all temporal modes; initial ROI was too low for acceptance. |
| `Test-isolation-20260911-200245` | Off, transparency, culling, zero jitter, shadows and repeat-baseline controls, 96 frames each. |
| `Test-prepass-20260911-200910` | Material prepass versus all terrain buffers; isolated an early-buffer mismatch. |
| `Test-buffers-20260911-201203` | Separated depth from decals; depth alone matched the broad correction. |
| `Test-depth-final-20260911-201513` | Depth-only candidate across all modes, paused and live, preserving saved quality settings. |
| `Test-production-20260911-202518` | Rejected validation: intrusive scope probe disturbed restoration; shared Harmony probe state also prevented profiling completion. Not evidence against the depth correction. |
| `Test-production-probe2-20260911-203129` | Corrected probes; production images and restoration passed. Timing arrays were omitted by player JsonUtility, so those timing files are unusable. |
| `Test-regression-20260911-203449` | Passed 49 acceptance checks: fresh paired production/bypass captures, separate no-readback timing, camera sweeps and normal launch. |

Early candidate runs deliberately used DLAA preset K / sharpness 0.24. Final
candidate and production runs preserve the user's **preset L / sharpness
0.1004950628**. Preset L exposes this defect much more strongly; do not compare
the absolute scores across those settings as if only the fix changed.

With the saved quality settings, the 128-frame depth-only candidate produced:

| Mode | Paused before | Paused corrected | Reduction | Unpaused reduction |
| --- | ---: | ---: | ---: | ---: |
| DLAA L | 0.00315988 | 0.00018042 | 94.29% | 94.17% |
| TAA | 0.00215913 | 0.00009419 | 95.64% | 95.57% |
| FSR2 Native AA | 0.00524149 | 0.00014704 | 97.19% | 97.02% |

Production repeat scores were 0.00017918, 0.00009423 and 0.00014965,
respectively, with exact per-frame depth/raster projection agreement and no
projection drift. These are reductions in the defined signal, not a claim of
zero visible variation or a general image-quality percentage.

The final paired production regression independently measured **94.28% DLAA,
95.50% TAA and 97.07% FSR2** reduction. Every captured production depth
projection matched exactly, including the three slow pans and three native
launches. Launch captures reached roughly 1.0–1.6 km and 228–295 m/s.
The fixed native ROI no longer contains the target hills that high up, so those
launches validate rendering lifecycle and full-scene visual continuity, not the
stationary hillside threshold. Saved-view captures rendered at 67–90 FPS;
slow-sweep capture/control overhead reduced those runs to 43–47 FPS. Performance
claims use the separate windows without capture or orbit control.

## Verification and cost

Supported Unity/SDK build completed with **115 passing EditMode tests**,
including projection ownership, exceptional cleanup, nested application,
inactive/unsupported/unrelated cameras, deterministic sampling and zero-allocation
scope tests. Initial prepare encountered a transient package/GUID import failure;
the failed attempt was not accepted. A subsequent complete pipeline passed.
Build manifest and logs identify the resulting archive and DLL.

The production paired timing run measures 720 existing terrain depth draws per
mode and condition, with all capture readback and reflective frame audits
disabled. Added median CPU time is **0.0022–0.0029 ms per draw**; all measured
draws allocate **zero managed bytes**. Mean overall frame time changes by less
than 0.2% in those paired windows. This is a CPU measurement; GPU timer data was
unavailable. Source inspection confirms no additional GPU draw or resource.
Full profile distributions are in `terrain-verification.json`.

Runtime: Redux 0.2.9.0.104521-beta, Unity 6000.5.8f1, D3D11, RTX 5070 Ti,
driver 610.88, 2560×1440, 100% scene scale. The 6000.4.1f1 pinned editor builds
the managed mod; runtime evidence here is from the installed 6000.5 player.

## Detector and limits

The harness captures every rendered frame directly at the existing temporal
resolve: native linear input/output, depth, lower-resolution context and exact
camera/raster matrices. It does not encode video during a measurement window.
A 120 FPS cap requests headroom; actual render timestamps establish cadence.
The 128-frame windows observe every jitter phase even when the game renders
below 120 FPS.

Per-pixel differences confuse ordinary PPv2 dither with this defect. The accepted
signal is mean absolute consecutive change of **16×16 RGB block means** within
a fixed hillside interior. Spatial averaging suppresses independent pixel noise
while retaining the coherent changing terrain patches. Off and injected-noise
controls validate that distinction. A fresh production run must be below 0.00025
and improve at least 90% over its matching bypass control.

The threshold is specific to Test's saved view, resolution, ROI and stationary
camera. The analyzer checks the view fingerprint and excludes camera motion
from this threshold. Its experimental depth-reprojected moving-image scores
are diagnostic only; visual review is still needed for motion and detail.
Native frames preserve texture detail with the original reconstruction settings;
this fix does not achieve stability by reducing temporal sample coverage.

Inspected native before/after hillside crops, camera-sweep crops and full-scene
launch screenshots showed no obvious added blur. The spatial gradient of the
temporal-mean hillside increased by about 0.9% in DLAA, 12.4% in TAA and 10.2%
in FSR2. This is a descriptive detail check, not an independent ground-truth
quality score. Review media and the detail proxy are under
`Test-regression-20260911-203449/review/index.html`; the three side-by-side clips
hold captured frames according to real timestamps on a 120 Hz playback timeline.
They do not represent 120 unique game renders per second.

Normal mode selection is the validated path. The separate live A/B comparison
renderer suspends the normal coordinator and replays cameras after the game's
auxiliary buffers were made. Its per-arm terrain-depth coherence is not covered
by this fix or these results. PPv2 engineering comparison is also outside the
three user-selectable temporal modes tested here. Other Redux builds, GPUs and
landscapes remain unmeasured.

See [terrain test workflow](../terrain-flicker-testing.md) for reproduction,
automated acceptance, artifacts and settings restoration.
