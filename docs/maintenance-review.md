# Redux Better AA maintenance review

Baseline: `a4b3f67f98f5741bcfa5b792e8d92bee400a6064`, committed before refactoring.
It preserves the beta preparation, compatibility work and captured investigations.
Scope is the current experimental native-AA implementation.

## Findings and disposition

| Area | Finding | Disposition |
|---|---|---|
| Camera ownership | Three copies of PPv2 suppression/depth restoration could drift. | One tested `SceneCameraState`, distinct shared/resolve claims, idempotent cleanup. |
| Render hooks | Three identical components repeated forwarding and attachment. | One `TemporalRenderHook`; backend algorithms and vendor boundaries retained. |
| Issue reports | Appended input capture ran after AA despite execution-order attributes. Equal images could falsely suggest no reconstruction. | Observe at the actual resolve entry; schema 2 names the stage. Remove the test-only DLAA input patch. |
| GPU resource loss | Released GPU storage passed size-only checks. Custom TAA checked only its first history. | Validate every history/output/mask; recreate and reset when lost. Reject invalid input surfaces. |
| Output descriptors | Duplicate vendor builders inherited memoryless flags. | Shared persistent, linear, single-sample UAV policy. |
| Setup failure | Exceptions after camera claims could leave partial ownership. | Catch configuration failure and deactivate before retry/fallback. |
| Backend registry | Selection, names, mode mapping and disposal repeated a backend list. | One registry with a mapping regression test. |
| Jitter/reset | Scene-specific behavior has measured compatibility reasons. | Preserve sample math, map/menu zero jitter and explicit game-event resets. |
| Vendor integration | Similar plumbing has different reflected fields/properties and dispatch contracts. | Retain separate cached-delegate adapters; no SDK/dependency change. |
| Foliage | Exact engine gates protect the global motion shader replacement. | Restrict it to active AA and release it in Off. |
| Settings/UI | Persistent settings, session diagnostics and migration already have distinct responsibilities. | Preserve labels/layout/defaults/callbacks; exercise the real settings page. |
| Diagnostics | The large visualizer contains specialized modes and async readback ownership. | Retain its existing functions; avoid a cosmetic split or removing useful investigation tools. |
| Build/distribution | SDK generation rewrites subasset IDs; distribution has existing licensing gates. | Exclude generated ID churn; preserve package gates and validate a local portable archive. |
| Batch tooling | Packaging ran imported assemblies through Burst even though tests suppressed it; the SDK build timed out. | Apply the existing Burst suppression to all build stages and explicitly detect Unity async timeouts. |

## Validation

- Baseline: supported Unity/ThunderKit build; 60/60 EditMode tests passed.
- Baseline visual suite: `Artifacts/visual-20260909-230802`, ten screenshots and a
  six-second DLAA pan; both suites passed. Baseline DLL SHA-256:
  `dce68dd796ad975d6f0fececf0d2f1aba10a2874caf57974ce4d94c6ead347b3`.
  The receipt's dirty flag reflects concurrent refactoring. The installed DLL
  came from the separately preserved baseline package.
- Refactor: `tools/Build-Beta.ps1` passed 67/67 EditMode tests and the supported
  package pipeline. Test receipt: `Logs/beta-editmode.xml` (2026-09-10 03:15 UTC).
- Refactor visual suite: `Artifacts/visual-20260909-231646`; menu/settings and
  flight/map suites passed, with ten screenshots and a 2560x1440, 180-frame,
  six-second DLAA pan. Compared with the baseline, no regression was observed
  in the inspected launchpad, map and settings images. The central settings
  region was pixel-identical. Temporal samples and small UI changes prevent
  whole-frame equality; pixel differences are not a quality score.
- Lifecycle suite: `Artifacts/visual-20260909-231811`; 113/113 assertions passed,
  no report errors/warnings, eleven screenshots. All seven public modes passed
  three switch cycles. Forced loss recovered all seven TAA targets and all four
  DLAA/FSR2 targets, with the selected backend still active and zero lost targets.
  Map AA overrides preserved the flight selection and restored DLAA when enabled.
- Four production issue ZIPs passed `tools/Test-IssueReport.ps1`, including every
  recorded file hash and same-frame consistency. Off captured identical input
  and output at `after-ppv2`; TAA, DLAA and FSR2 captured distinct buffers at
  `before-temporal-resolve`, without a test-only rendering patch.
- Local portable packaging passed with `tools/Package-Beta.ps1
  -LocalValidationOnly -OutputDirectory .build/review-packages`. Public release
  licensing/runtime gates remain in effect. All tool scripts parsed successfully.

### Runtime and reproduction

Redux `0.2.9.0.104521-beta (26w36c, d299b906)`, Unity `6000.5.8f1`, Windows/D3D11,
RTX 5070 Ti, driver `32.0.16.1088`, 2560x1440 native AA. Fixture
`local/launchpad-fly-safe-15`, paused Fly Safe-15; orbit distance 45, yaw 0,
pitch 35, FOV 55, with the All suite also sampling a yaw pan. DLAA preset M,
sharpness 0.15; existing TAA stability 0.99. Scripts restore original settings.
Run `tools/Run-VisualTests.ps1 -Scene All`, then `-Scene Maintenance` with the
documented ReduxTestHarness and local fixture installed. Maintenance requires
both vendor runtimes. Captures identify the tested source as the baseline plus
uncommitted refactor; the exact tested DLL hash below identifies the binary.

The local [comparison gallery](../Artifacts/visual-review-20260909/index.html)
links original before/after images and videos. Its adjacent `comparison.json`
and `issue-report-validation.json` retain the numerical comparison and report
summary. Captures and large ZIPs remain local artifacts.

### Artifact identity

| Artifact | SHA-256 |
|---|---|
| Tested refactor DLL | `d984d1496aacc9584c589fb1d8756953e9fb7aa40dc25073ac36ab1cfa8303db` |
| SDK ZIP | `42d40ab47b0e9ca8151dff1c590446ba04e5e5380afa56445e3a48841f091776` |
| Portable local-only ZIP | `d8de168a1921d92e8ad5e4249568521babe6dd02399fe4c9cdabfaa2a5b7b320` |

Issue archives are under the installed mod's `diagnostics/reports/`:

| Mode | Archive | Frame | Verified files |
|---|---|---:|---:|
| Off | `betteraa-20260910-031836-7f656029.zip` | 4093 | 97 |
| TAA | `betteraa-20260910-031856-52912ecd.zip` | 5120 | 113 |
| DLAA | `betteraa-20260910-031925-3466ced7.zip` | 6215 | 107 |
| FSR2 | `betteraa-20260910-031946-2dfca221.zip` | 6943 | 107 |

An initial visual launch was discarded because the SDK ZIP had been staged one
directory above the mod. Those files were moved out, the package installed in
`mods/ReduxBetterAA`, and the clean baseline run above replaced that attempt.

## Limits

Image comparisons provide evidence for the tested runtime/scenes, not a general
quality improvement or completion of the public-beta matrix. No isolated GPU
speedup or whole-game zero-allocation claim is made. Non-NVIDIA hardware, other
resolutions, VAB/KSC/orbit/docking and long-duration revert/vessel/origin stress
remain separate acceptance work. No shader retuning or cloud-quality fix is
claimed by this refactor.
