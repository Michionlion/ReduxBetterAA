# Visual acceptance workflow

Run `pwsh -NoProfile -File tools/Run-VisualTests.ps1` with KSP2 closed and the
Better AA beta installed. The runner builds the test adapter and matching
ReduxTestHarness, launches the game, captures the following DLAA M views, and
writes `Artifacts/visual-<timestamp>.zip` ready to share.

| Selection | Captures |
|---|---|
| `-Scene Menu` | 3 native settings-page screenshots: sharpness 0.15, 0, and 1 |
| `-Scene Flight` | 6 settled launchpad views, a 6-second camera-pan MP4, and 1 map view |
| `-Scene All` (default) | All 10 screenshots and the MP4 |

The menu test opens **Settings > Mods > Redux Better AA** and changes the actual
Mode/DLAA preset dropdowns and Sharpness slider. It verifies that the callbacks
reach configuration and that DLAA is selected and active. An unavailable or
failed DLAA backend fails the suite instead of capturing a labelled fallback.
Flight uses DLAA M with sharpness 0.15 and map AA enabled. It loads the local
`launchpad-fly-safe-15` fixture, pauses it, and captures six settled camera angles.
It also records 180 presented frames across a smooth 30-degree camera pan and
encodes them at 30 FPS (six seconds) at native resolution using H.264 CRF 16.
Frames stream directly into the encoder without a PNG sequence on disk. The
encoder uses the medium preset for a practical quality/compression balance.
The runner records final bytes and encoding in `capture.json`; there is no hard
size cap or automatic quality reduction. The bundle also contains lossless screenshots.
This is sampled video,
not a real-time recording or a frame-pacing benchmark.

The compact suite does not exercise Ctrl+F10 developer mode, other AA backends,
issue-report generation, floating-point buffers, or long pan sequences. The ZIP
contains the lossless PNGs, the MP4, `index.html`, harness reports and bounded logs, plus
`capture.json` with the installed assembly hash and checkout state. Extract it
and open `index.html`. Flight requires FFmpeg on PATH (or pass `-Ffmpeg <exe>`);
Menu-only captures do not require FFmpeg. The runner checks before launching.

Pass `-GameRoot`, `-HarnessRoot` and `-UnityRoot` for other layouts; `-KeepOpen`
leaves the game running. Do not start during an unsaved play session. The Lua
cleanup restores persistent Better AA settings after normal completion or an
assertion failure, and the harness restores its camera/settings/pause snapshot.
Process crashes cannot execute that cleanup. The test-only adapter remains
installed for reruns; remove `mods/ReduxBetterAAVisualTests` afterward if desired.
Neither test plugin is a production dependency.

The runner rebuilds against the selected player's managed assemblies and uses
120-second bridge response timeouts. It waits for game shutdown between suites.
A failed suite retains its local evidence but does not publish a success ZIP.
`tools/Build-VisualGallery.ps1 -Directory <run-directory>` regenerates the gallery;
it also supports older pan sequences and optional FFmpeg encoding.

## Manual review

Inspect the native settings layout, numeric slider labels, selected mode/preset,
and UI sharpness. Review geometry, foliage, clouds and map icons in the DLAA
stills. Reports include the game/Redux/Unity, GPU, resolution and backend state.
Visual quality still needs human review; passing assertions do not certify it.
The small suite does not test restart persistence or full temporal behavior.

Before public beta acceptance, use the broader protocol in `SPEC.md` for motion,
VAB, KSC, orbit, quickload/revert, vessel changes, docking, origin rebases, time
warp, resolution changes, resource/performance measurements and other hardware.
Heavy buffer investigations remain separate opt-in scripts, described below.

## Issue ZIP checks

Run `pwsh -NoProfile -File tools/Run-VisualTests.ps1 -Scene Maintenance` for the
broader native-AA lifecycle check. It requires both vendor runtimes and produces
11 screenshots across all public modes and recovered TAA/DLAA/FSR2 outputs. It
also exercises three mode-switch cycles, map-AA overrides and four production
issue reports. ZIP paths are recorded in the harness report; the heavy reports
remain in the mod diagnostics folder and are not duplicated into the gallery.
FFmpeg is not required for this selection. Original settings are restored.

Schema 2 reports capture actual input at the shared scene-resolve hook.
`inputStage` is `before-temporal-resolve` for Custom TAA/DLAA/FSR2, `after-ppv2`
for the other modes, or `unavailable` without a camera. The adapter reports
`temporal_input_captures` and no longer patches DLAA input. Old schema 1 reports
have an ordering limitation: their appended input hook ran after AA, so equal
input/output cannot demonstrate a no-op resolve. The guard-isolation script
also labels any temporary suppression as `TEST-ONLY`; such a capture is not a
production fix or a cloud-quality acceptance test.

Unzip a generated report and inspect `manifest.json`: input/output/presented
frame numbers should match for a normal capture; missing cloud targets in the
menu are expected. Verify file sizes/hashes, view EXR samples for signed motion
and HDR values, and inspect PNG alpha previews. Trigger a capture while Off,
TAA and each vendor backend are active, during a scene change and after a
backend failure. Confirm the panel/cursor and ordinary rendering recover.

EXRs contain sampled RGBA float channels at source dimensions. Motion previews
map zero to grey and signed UV displacement with a gain of 32. Device-depth PNGs
linearize and brighten depth; linear-history-depth/mask PNGs show their first
channel. Cloud alpha previews are separate opaque grayscale PNGs. These
transforms are visual aids; numeric analysis should use the EXRs.

Run `tools/Test-IssueReport.ps1 -Zip <report.zip>` to verify finalized status,
frame consistency, image presence and every recorded file hash. The compact
visual runner does not generate or bundle issue ZIPs. Full-size reports may
exceed 100 MB because they retain floating-point
scene/history/cloud data; use a file link when an issue tracker limits attachments.

Redux 2.9's first-run online-services dialog can appear in menu screenshots until
the user makes that choice. The harness does not accept it or change those
preferences. Flight and raw scene buffers remain available for visual review.

