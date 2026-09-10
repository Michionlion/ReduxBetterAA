# 0042 — Coherent main-menu raster jitter

Phase 3/4 native AA integration, SPEC 1 main-menu requirement and 11/12 input
coherence. Implemented and tested on Redux 0.2.9.0.104521-beta (`d299b906`),
Unity 6000.5.8f1, Direct3D 11, RTX 5070 Ti (driver 610.88), 2560×1440. No vendor library,
shader algorithm, game assembly or render-scale/presenter replacement is added.

## Clean baseline

Before menu changes, the existing integrated TAA quality work, live comparison,
unified AA ownership and retired-experiment cleanup were committed as Better AA
`28c1329`. The related test-harness adapter/camera/investigation work was committed
separately as `7242ebe`. Both trees were clean. The baseline built and passed
103 EditMode tests. Unrelated AlarmClock work was left alone.

## Observed problem

The current menu resolves at `Camera.Scaled`, after the earlier `Skybox` camera
and before `FlowCamera`/UI. Same-frame raw captures contain the whole 3D scene
without menus or text. Custom TAA, DLAA and FSR2 already execute there and change
pixels; the defect is limited temporal edge reconstruction, not a missing hook.
FXAA/SMAA also execute through the enabled PPv2 layer. Neither moving the final
resolve nor filtering UI is needed.

Decision 0029 disabled menu jitter after planetary patches appeared. Re-enabling
projection jitter alone reproduces that failure on the current player. The shared
projection owner sets `useJitteredProjectionMatrixForTransparentRendering=false`.
The menu's planetary/atmospheric contributors consequently use a different raster
projection from opaque passes. The visible planet includes
`CelestialBody_Scaled_Flow` and atmosphere materials. Matching transparent and
opaque projection jitter removes the patches. This is an observed input-coherence
fix; the investigation does not claim to have isolated the precise internal
planet shader depth-test instruction responsible for each triangle.

The original menu restriction must therefore be revised independently of map.
Map rendering has not been requalified for jitter by this work.

## Controlled isolation

One running menu, one frozen vessel pose (`Time.timeScale=0`), identical settings,
sharpness zero, DLAA M: eight consecutive full-resolution, same-frame linear
RGBA16F input/output captures per arm and backend after 120 settling frames.
The three arms are zero jitter, opaque-only jitter, and matching opaque/transparent
jitter. Stock effects can still evolve; time-scale freezing does not freeze every
shader or real-time effect. These are diagnostic frame differences, not FPS data.

Mean absolute consecutive-frame change, in clipped sRGB, in the fixed planet ROI
`x=1700..2499, y=1000..1399` (top-left origin):

| Backend | Zero jitter | Opaque-only jitter | Matching jitter |
|---|---:|---:|---:|
| Custom TAA | 0.000721 | 0.051163 | 0.000645 |
| DLAA M | 0.000307 | 0.051506 | 0.000303 |
| FSR2 Native AA | 0.000295 | 0.050981 | 0.000273 |

Matching jitter reduces the failure by 98.7–99.5%, back near the zero-jitter
control. Difference images show the large planetary patches disappearing.
The earlier separate-launch experiments are supporting evidence only because
the menu can choose a different vessel at startup.

## Implementation and ownership

- Enable full jitter for the recognized active `Skybox` → `Camera.Scaled` pair,
  with matching viewport/target, background rendered first, and depth-only clear
  on the resolve camera. Unrecognized stacks retain the existing zero-jitter path.
- Custom TAA, DLAA and FSR2 cache that graph's transparent-jitter requirement.
  Both participating cameras receive the same existing Halton offset. Transparent
  rendering uses that offset too, and vendor dispatch retains its existing inverse
  jitter convention. There is still exactly one resolve, before UI.
- `CameraProjectionState` records both the original and applied transparency
  setting. Restore only while the current value matches our claim, preserving
  distinguishable external writes. Restore is idempotent and duplicate callbacks
  do not apply jitter twice.
- Flight/KSC/VAB behavior is unchanged. Map retains zero jitter and its existing
  independent AA toggle. Engineering-only PPv2 TAA remains unavailable in the menu:
  its own projection setup forces transparent jitter off. Public TAA is Custom TAA.
- Add `temporal.jitterTransparentRendering` to capability diagnostics. It describes
  the graph requirement, not a promise that an Off backend applies jitter.
- No additional render target, command buffer, pass, native context or production
  per-frame allocation is introduced. FSR2 remains native-resolution AA, not SR.

## Production verification

`tools/Build-Beta.ps1` passed **108 EditMode tests** and produced the SDK archive.
New tests cover both original/applied transparency values, external ownership,
duplicate callbacks, idempotent restore, mismatched targets, disabled/misidentified
menu cameras and unchanged flight/map policy.

Installed DLL and built DLL SHA-256 both:
`6CA72D53B997E0CE7DBF1DF754CD93FF6C5F8D906770352CF5113BC51855CB85`.
SDK ZIP SHA-256:
`D615CCA8780FFE2649D6F3324490AF08999FD4CD4D385EAC2DE19F168472D437`.

The production menu suite runs without the test-only jitter override patches.
It checks all requested native modes, captures inputs/outputs, exercises three
switch cycles and Off resource release, changes resolution to 1920×1080 and back,
loads the paused `launchpad-fly-safe-15` fixture, exercises all three temporal
backends in flight, checks map's zero-jitter policy, and uses the normal
`GameManager.ShutdownGame()` flow to return to the menu. All three backends then
reacquire the menu policy and render again. **39/39 assertions**, zero harness
errors/warnings. This does not certify unrelated raw player shutdown messages.

A separate production menu run passed 24/24 assertions. All captured output
pixels were finite. A 240-frame resolve window per temporal backend recorded
zero managed allocated bytes after warm-up; median CPU submission was about
0.02–0.03 ms. This is not isolated GPU pass timing or a gameplay performance claim.

Same frozen menu pose, 2×-per-dimension spatial reference, native-sized output,
fixed vessel ROI `x=950..2149, y=200..1119`, clipped-sRGB RMSE:

| Mode | Reference error |
|---|---:|
| Off | 0.02075 |
| FXAA High | 0.01883 |
| SMAA | 0.01818 |
| Custom TAA | 0.01602 |
| DLAA M | 0.01528 |
| FSR2 Native AA | 0.01644 |

The temporal modes reduce this diagnostic error by 21–26% versus Off. Native
close-ups show improved edge coverage. The 2× reference is not ground truth:
lighting, filtering and temporal sampling can differ. Do not treat these numbers
as a universal backend ranking. Presented captures retain separate native UI;
an online-services dialog in some captures was not accepted or reconfigured.

## Reproduction and artifacts

Install the built `Deploy/ReduxBetterAA` contents, retain matching vendor runtimes,
and run from the checkout:

```powershell
pwsh -NoProfile -File tools/Run-QualityTests.ps1 -Menu -Label menu -ArtifactRoot S:\KSP2-menu-AA
python tools/analyze-menu-aa.py <run-directory>
```

`-MenuIsolation` instead runs the explicit zero/opaque/matching-jitter ablation.
Its projection patches exist only in the test adapter and are installed only
when that option is selected. The normal menu suite never installs them.
The runner restores the exact saved AA config after the game exits and removes
its two test-adapter files. `ArtifactRoot` supports a drive with enough free space
for full-resolution float captures. No automatic old-artifact deletion is used.

Local evidence under `S:\KSP2-menu-AA`:

- `quality-menu-baseline-20260910-162030`: original zero-jitter execution proof.
- `quality-menu-jitter-20260910-162308`: opaque-only failure reproduction.
- `quality-menu-transparent-jitter-20260910-162506`: first successful candidate.
- `quality-menu-controlled-20260910-162715`: same-scene isolation table above.
- `quality-menu-production-20260910-163301`: reference, allocation and image review.
- `quality-menu-lifecycle-20260910-163544`: final production transition suite.
- `quality-menu-final-isolation-20260910-163845`: ablation repeated against the
  production DLL using the explicit isolation-only test patches.

The analyzer writes `menu-review/index.html`, numerical results, enlarged detail
images, difference images and a size/SHA-256 manifest of raw inputs/outputs.
The bounded validation does not cover every downloadable menu vessel, every GPU,
Redux 2.8.5 runtime, or long-duration animation. No spatial fallback is necessary
for the validated stock menu stack.
