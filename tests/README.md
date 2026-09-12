# Standard release checks

Run these for every release. Keep this checklist and lasting unit tests here;
keep private fixtures, raw captures and one-off investigations outside the repo.

## Automated

- `pwsh -NoProfile -File tools/Test-Release.ps1`: package allowlist, metadata,
  version agreement, path safety, source cleanliness guards and script syntax.
- `pwsh -NoProfile -File tools/Build.ps1`: all EditMode tests, shader/assembly
  compilation, player build and installable ZIP validation.
- For build/dependency changes, repeat the documented first build from a fresh
  checkout with no Library, imported assemblies or game asset cache.

EditMode tests live in `Assets/ReduxBetterAA/Tests/EditMode`. They cover camera
ownership, history resets, jitter/projection alignment, backend policy,
configuration and cleanup. The public package validator rejects unexpected
files, including test adapters and third-party DLLs.

## In-game

Use the built ZIP on an installed Redux player. Run normal mode selection,
with F10 live comparison stopped. Use the same view/settings for comparisons.
The external Redux Test Harness can automate these checks, but is optional.

| Check | Pass condition |
| --- | --- |
| Clean install, natives absent | Loads without errors; unsupported vendor modes are unavailable; TAA/FXAA/SMAA work. |
| Each supported mode, then Off | Correct mode activates; Off restores unfiltered native rendering. |
| Main menu, flight, map, VAB | Scene transitions recover; no repeated exceptions, stale history or missing output. |
| Launchpad terrain | View northwest hills from launchpad 4, zoom out enough to see slopes; compare Off/TAA/DLAA/FSR 2 while paused and launching. No coherent flashing patches. |
| Thin geometry and foliage | Pan around struts/antennas and vegetation; check shimmer, disappearance and trails. |
| Map planet and icons | Rotate and zoom; no flashing planet patches, icon filtering or changed icon stacking. Toggle map AA. |
| Plumes and atmospheric flight | Check trails and transparency during motion, not only when paused. |
| Lifecycle | Repeat mode switches, resize, quickload/revert, vessel switch and time warp; rendering recovers. |
| UI and diagnostics | Text stays crisp; F10 Issue ZIP opens and identifies the correct backend and capture stage. |
| Resource/performance | After warm-up, zero managed allocations in AA callbacks; no accumulating targets/contexts across switches. Record CPU/GPU timings where available. |

Record source commit, Redux/Unity versions, GPU/driver, resolution, tested modes,
scene setup and results in the release notes or linked PR. Mark unsupported or
untested cases explicitly. Keep selected before/after images in release assets,
not the source tree. Capture frame rate is not a performance measurement.
