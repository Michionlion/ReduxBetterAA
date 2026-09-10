# DLAA reinstall and buffer verification, 2026-09-07

Phase 4 rendering verification. The requested reinstall was applied to Better AA
0.6.0, its Addressables payload, and the matching NVIDIA/AMD native runtime files
on both existing Redux players. Every copied file was checked against its package
SHA-256 manifest. All ten installed payload/runtime files already matched on each
player before replacement. This was not a base-game/Redux upgrade, and no unrelated
mods, saves, or stock game assemblies were replaced.

## Findings before the requested guard disable

The normal launchpad DLAA path is a no-AA pass-through on both tested targets.
The cloud compatibility guard remains active with 120 settle frames, including
after unpausing and after disabling clouds through the game setting. The NVIDIA
backend and context remain active with no reported fallback or native failure.
`NvidiaDlaaBackend.RenderCore` returns from the guard branch before `_api.Execute`,
copying the incoming scene to its destination.

The guard resets its settle counter to 120 on every observation with stock
`EnableTUS` true. Its existing policy therefore permits indefinite suspension,
not merely a 120-frame transition. The reinstall does not change that policy.
Menu DLAA does produce a distinct processed output when the guard is inactive.

The existing issue-report input hook also had a measurement defect: it is appended
to the camera after the DLAA component. `DefaultExecutionOrder` does not make it
execute before the image effect. [Unity documents image-effect ordering by camera
component order](https://docs.unity.cn/6000.4/Documentation/ScriptReference/MonoBehaviour.OnRenderImage.html).
Consequently, original `scene-input` and `scene-output` equality did not establish
that DLAA was a no-op. The test adapter now uses a one-shot Harmony prefix at the
actual DLAA render entry to capture the source. Its counter is asserted for each
DLAA capture. Output still comes from the later report hook in the same frame.

## Reproduction and evidence

- Primary Steam player: Redux 0.2.9.0.104521, Unity 6000.5.8f1.
- Regression copy at `G:\KSP2\_diagnostics\Redux29`: Redux 2.8.5,
  Unity 6000.4.1f1. Its directory name is historical, not its runtime version.
- Better AA 0.6.0 assembly SHA-256:
  `a2f13304ec9178fa4dc4ce0f2a07a6628f056e255db1448c4328d2d2ef922399`.
- NVIDIA GeForce RTX 5070 Ti, Windows 11, D3D11, 2560 x 1440, native-resolution
  DLAA, preset M, sharpness zero.
- Fixture: local `launchpad-fly-safe-15`, vessel `Fly Safe-15`, distance 45,
  pitch 35, FOV 55; yaw 0 and 30, paused and unpaused controls.
- Real Off controls explicitly select the Off backend; diagnostic-view labels
  are not treated as backend selection.
- Numeric comparisons use sampled linear RGB EXRs, not gamma PNG previews or
  opaque NVIDIA-internal histories. Each input/output pair has matching frame IDs.

The validated evidence root is
`G:\KSP2\ReduxBetterAA\Artifacts\dlaa-reinstall-verified-20260907`:

- [Comparison gallery](../Artifacts/dlaa-reinstall-verified-20260907/index.html)
- [Numeric buffer comparison](../Artifacts/dlaa-reinstall-verified-20260907/buffer-comparison.json)
- Per-target harness reports and archive hash-validation records are in that root.
- Full EXRs/ZIPs remain at the original directories recorded in the numeric data.

The earlier `dlaa-reinstall-20260907` artifact directory is explicitly superseded:
it predates the corrected input capture and cannot support DLAA input/output
conclusions by itself.

## Diagnostic changes and limits

`tests/Visual/VisualTestMod.cs` now captures true DLAA input and exposes an explicitly
labelled test-only cloud-guard suppression. Suppression requires switching Off
first and is reset before restoring user settings. It skips only the Better AA
cloud observer; it does not rewrite stock cloud settings, buffers, or history.
Cloud disabling is a separate, reversible game-setting control in the full suite.

`dlaa-noaa-buffers.lua` covers menu, launchpad, rotated camera, unpaused flight,
clouds-disabled flight, and guard isolation. `dlaa-guard-isolation.lua` provides
a shorter Off / production DLAA / suppressed-guard DLAA comparison. The normal
visual runner now references the installed Harmony assembly for this adapter.

These isolation runs used the original production DLL and native DLLs. Following
this evidence, the maintainer explicitly requested disabling the guard for now.
`NvidiaDlaaBackend` now constructs `CloudTemporalGuard(enableSuspension: false)`:
observations retain dimensions and resize counts, but cannot suspend DLAA, jitter,
or start the settle countdown. The enabled policy remains available only to its
historical unit tests. A new regression covers 1,000 consecutive active-TUS
observations without suspension. The supported Unity build passed all 60 tests.

The updated game-root packages are in `Deploy/guard-disabled-20260907`, separate
from the earlier guarded packages. Production validation uses
`tests/Visual/dlaa-production-enabled.lua`, which explicitly rejects a test guard
override, checks guard/counter state, captures true input and output, and repeats
after an unpaused camera pan. Its artifact root is
`Artifacts/dlaa-guard-disabled-20260907`.

Final production validation passed on both targets: 15/15 runtime assertions per
player, no harness errors/warnings, and six complete ZIPs with verified file hashes
and matching input/output/presented frame IDs. Stock `EnableTUS` was true in all
six captures, but suspension and countdown stayed off. DLAA projection jitter was
nonzero. No test-only guard override was active.

| Redux | Off changed pixels | DLAA changed pixels | DLAA after pan |
| --- | ---: | ---: | ---: |
| 2.8.5 | 0% | 99.996691% | 99.994792% |
| 2.9 | 0% | 99.997152% | 99.994303% |

These compare actual same-frame AA input/output RGB; the percentage is execution
evidence, not an image-quality score. See the [final production gallery](../Artifacts/dlaa-guard-disabled-20260907/index.html)
and [numeric data](../Artifacts/dlaa-guard-disabled-20260907/buffer-comparison.json).
An initial 2.9 test attempt needed its diagnostic camera explicitly selected;
the retained failed attempt is superseded by the passing run in the same target
directory. It was a test setup error before AA verification, not a player failure.

Installed DLL SHA-256 on both players:
`3dfd42db3a358a085a6d406ee6a763016e1bf150f1d0aed31231eada94c00137`.
The current top-level full/portable ZIPs were replaced with the new packages;
prior guarded ZIPs are preserved under `Deploy/guard-enabled-superseded-20260907`.
Test adapters were archived outside both game folders. User settings were verified
restored to DLAA/M, sharpness 0.24, stability 0.99, foliage repair on and map AA off.

Disabling the guard restores AA execution, not the original cloud-disappearance
defect. That cloud regression remains open. A changed buffer
establishes that image processing occurred; it does not certify all temporal
quality, motion, cloud, performance, or long-session acceptance requirements.
