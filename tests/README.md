# Standard release checks

Run these for every release. Keep this checklist and lasting unit tests here;
keep private fixtures, raw captures and one-off investigations outside the repo.

## Automated

- `pwsh -NoProfile -File tools/Test-Release.ps1`: package allowlist, metadata,
  version agreement, path safety, source cleanliness guards, script syntax,
  and runtime ZIP contents, source validation and overwrite protection. It also
  checks optional FG archive pins, ABI exports, notices, deterministic packaging
  and independent vendor selection.
- `pwsh -NoProfile -File tools/Test-Candidate.ps1`: fresh game/mod/harness copies,
  latest Redux beta, complete release packages, and in-game checks with and without vendor
  runtimes. Supply `-FsrRuntimeZip` for modern AMD coverage; without it the
  result explicitly records limited vendor coverage and tests AMD unavailable.
  Configure paths in `.env`; see [setup](../CONTRIBUTING.md#test-a-release-candidate).
- `tools/Release.ps1`: prepare the mod, runtime ZIPs, changelog and checksums
  locally. With `-Publish`, also verify the uploaded assets before publishing.
- `pwsh -NoProfile -File tools/Build.ps1`: all EditMode tests, shader/assembly
  compilation, player build and installable ZIP validation.
- For build/dependency changes, repeat the documented first build from a fresh
  checkout with no Library, imported assemblies or game asset cache.

EditMode tests live in `Assets/ReduxBetterAA/Tests/EditMode`. They cover camera
ownership, history resets, jitter/projection alignment, backend policy,
configuration and cleanup. The public package validator rejects unexpected
files, including test adapters and third-party DLLs.

FG native targets have independent pinned-header CMake builds and tests documented
in [Presentation](../Native/Presentation/README.md),
[Streamline](../Native/Streamline/README.md) and
[AMD frame generation](../Native/FrameGeneration/README.md). The explicit
`tools/Build-Native.ps1 -FrameGeneration` route builds the coordinator and selected
providers; `-RunTests` runs CPU/Win32 contracts, without launching GPU tests or
Unity. Follow [NATIVES.md](../NATIVES.md#frame-generation-companion) for external
SDK/runtime inputs and the separate optional companion ZIP. The main mod ZIP
still rejects native DLLs. Run each provider's documented GPU suites separately
after GPU/resource changes; repeat builds from fresh source after dependency
changes. Packaging success does not certify player FG.

Managed tests cover independent settings/fallback, resolved-frame identity and
reset metadata, success-only publishing, no-consumer behavior, reentrant failure
cleanup, input-surface retirement and native ABI layouts. GPU shader fixtures
check asymmetric input orientation and required FG motion repair against exact
camera/depth/jitter snapshots, including reset and plausible object motion,
without changing the AA diagnostic sanitizer preference. Terrain tests cover
generation/depth/color linkage, adjacent camera history and reset invalidation;
GPU fixtures verify depth-matched planet motion, foreground/object/sky retention,
and that FG cannot apply the repair twice. Native tests cover
owned child lifetime, per-ticket Present ownership, immutable input slots,
provider capability/initialization and retained GPU work. Backpressure tests
cover the nonrefreshable completed-image deadline, one-time fallback and explicit
Off/re-enable, including owner-tick/Present ordering. Keep exact test totals
in the build output or release record rather than fixing them in this checklist.

Limited RTX 5070 Ti player checks cover independent native-AA/SR producers with
DLSS 2x–4x and FSR 3.1 FG 2x, actual settings/Auto selection, provider switches,
Off/re-enable, same-frame input consistency and source Present ownership.
Window/backbuffer extent mismatch suspension and re-enabling after matching
geometry returns have been exercised. That is distinct from the provider's
explicit `WindowChanged` error recovery and from minimize/alt-tab recovery;
those remain unvalidated in the player.

AMD FG 4 and older RTX hardware, broader moving flight/UI quality and input
latency still need coverage. Limited PresentMon traces cover native-AA/FG
combinations and a heavy scene; these do not certify all displayed cadence or
exercise the 250 ms fallback in the player.
Actual Present-call counters detect duplicate ownership and vendor extra calls;
they do not establish when images reached the display. Heavy-load timeout/drop
behavior preserves pending leases and ordinary rendering, without guaranteeing
the requested multiplier's frame rate.

## In-game

Use the built ZIP on an installed Redux player. Run normal mode selection,
with F10 live comparison stopped. Use the same view/settings for comparisons.
The candidate pipeline automates mode selection, camera/AA ownership, fallback,
scene transitions and report integrity through the external Redux Test Harness.
It also retains screenshots for review; these do not prove temporal stability.
Controlled flight camera movement must use the game camera rig and verify both
physics and scaled camera poses at render time. Moving only the physics camera
leaves the sky image stationary while changing its expected motion and creates
artificial temporal trails.

| Check | Pass condition |
| --- | --- |
| Clean install, natives absent | Custom TAA activates before any mode selection; unsupported vendor modes are unavailable; TAA/FXAA/SMAA work. |
| Each supported mode, then Off | Correct mode activates; Off restores unfiltered native rendering. Saved selections, including Off, survive an update. |
| Main menu, flight, map, VAB | Scene transitions recover; no repeated exceptions, stale history or missing output. |
| Launchpad terrain | View northwest hills from launchpad 4, zoom out enough to see slopes; compare Off/TAA/DLAA/FSR while paused and launching. No coherent flashing patches. |
| Thin geometry and foliage | Pan around struts/antennas and vegetation; check shimmer, disappearance and trails. |
| Map planet and icons | Rotate and zoom; no flashing planet patches, icon filtering or changed icon stacking. Toggle map AA. |
| Plumes and atmospheric flight | Check trails and transparency during motion, not only when paused. |
| Ascending coastline and vessel edges | Compare ordinary TAA/DLAA/FSR selection during natural ascent. Verify same-frame terrain depth and planet transforms explain the corrected coastline vectors while nearer vessel motion is preserved; inspect land/water boundaries and clouds separately. |
| Ocean raster alignment | In the recognized flight stack, verify both cameras enable transparent jitter with temporal AA and restore it on Off. Compare paused shoreline input/output sequences and moving ascent across TAA, DLAA and FSR; separate jitter-correlated flicker from water/cloud animation and history resets. |
| Lifecycle | Repeat mode switches, resize, quickload/revert, vessel switch and time warp; rendering recovers. |
| UI and diagnostics | Text stays crisp; F10 Issue ZIP opens and identifies the correct backend and capture stage. |
| Resource/performance | After warm-up, zero managed allocations in AA callbacks; no accumulating targets/contexts across switches. Record CPU/GPU timings where available. |
| Optional FG choices and fallback | Probe each installed provider; DLSS 4x/3x falls back through actual lower capabilities, FSR best/compatibility labels match the SDK, saved requests persist and FG changes leave AA/scale unchanged. Missing companion/providers preserve ordinary AA. |
| FG scene inputs and UI | Exercise TAA, DLAA, FSR Native AA and SR with each available FG provider. Inspect actual resolved color, raw device depth, signed motion, reset/frame IDs and native output extent; test moving HUD, Toolkit/canvas UI, IMGUI/F10 and cursor. |
| FG Present ownership and cadence | Exactly one source-chain Present per real EOF ticket, with no duplicate retry after failure. Measure child/SDK/generated presents separately with presentation-aware tracing; do not multiply Unity FPS by the requested multiplier. |
| FG recovery and lifetime | Repeat provider/multiplier switches, Off/re-enable, resize, minimize/alt-tab, scene changes and runtime restart. Brief backpressure retains the completed child and pending pixels/HWND leases. After 250ms without a newer completion, it hides once, stays inactive and recovers after Off/re-enable; late retirement cannot show the old image again. Hard invalidation still hides immediately. |

Record source commit, Redux/Unity versions, GPU/driver, resolution, tested modes,
scene setup and results in the release notes or linked PR. Mark unsupported or
untested cases explicitly. Keep selected before/after images in release assets,
not the source tree. Capture frame rate is not a performance measurement.
