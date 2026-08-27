# Phase 1 bootstrap loader smoke

> Historical milestone. The loader gate remains valid, but the current Phase 1
> implementation and verification workflow are documented in
> [`runtime-probe.md`](runtime-probe.md).

## Milestone

Prove that Redux Better AA is accepted by the current Redux mod loader before
adding renderer discovery or diagnostics.

This smoke test addresses only the Redux entry point portion of SPEC Section
9.2. It does not complete Phase 1.

## Target

- Game/Redux build: `0.2.8.5.103184-beta` (`ffc94930`)
- Mod version: `0.1.0`
- Template: `0.2.8.5` (`455b945`)
- SDK: `beta-6` (`3e05f9b`)
- Unity: `6000.4.1f1` (project pin; editor pipeline not yet run)
- Installation: `G:\SteamLibrary\steamapps\common\Kerbal Space Program 2`

## Reproduction

1. Ensure KSP2 is closed.
2. From the repository root, run:

   ```powershell
   .\tools\Build-Bootstrap.ps1 -Deploy
   ```

3. Launch `KSP2_x64.exe`.
4. Wait for the main menu, then close the game normally.
5. Inspect `Ksp2.log` for these exact markers:

   ```text
   [ReduxBetterAA/Bootstrap] Phase 1 bootstrap pre-initialized; render probing is inactive.
   [ReduxBetterAA/Bootstrap] Redux Better AA loaded successfully; no renderer state was changed.
   [ReduxBetterAA/Bootstrap] Phase 1 bootstrap post-initialized.
   ```

6. Confirm there is no loader error, unhandled exception, or recurring warning
   associated with `ReduxBetterAA`.

## Evidence

- Status: Passed on 2026-08-22 at 20:39 EDT
- Built package: `.build/ReduxBetterAA/`
- Archive: `.build/ReduxBetterAA.zip`
- Deployed package: `mods/ReduxBetterAA/`
- Process result: Reached `MainMenu`, then closed normally with exit code `0`
- Mod-correlated warnings/errors/exceptions: `0`

Artifact SHA-256:

```text
5C75195AFD80F7FAB6CEF48A44B8C65C9A7557E62A02C840215FF21DAA63077E  ReduxBetterAA.dll
23889812A693DB5EF7926C4A2F4337017587A7F638F313DCAB58720C78F61970  swinfo.json
0563D0689A98C1658CE160A239EE5A62C493F2CAFD98D989E132DE8A5D47FFEC  ReduxBetterAA.zip
```

Relevant `Ksp2.log` evidence:

```text
[Space Warp] Attempting to register mod: ReduxBetterAA, Redux Better AA
[Space Warp] Registered plugin: ReduxBetterAA
[ReduxBetterAA] [ReduxBetterAA/Bootstrap] Phase 1 bootstrap pre-initialized; render probing is inactive.
[ReduxBetterAA] [ReduxBetterAA/Bootstrap] Redux Better AA loaded successfully; no renderer state was changed.
[ReduxBetterAA] [ReduxBetterAA/Bootstrap] Phase 1 bootstrap post-initialized.
[General] [State] Swapping game state! prev: [WarmUpLoading] --> new: [MainMenu]
```

The smoke build used `tools/Build-Bootstrap.ps1` against the target player's
managed assemblies. The pinned Unity editor installation was still in progress,
so the official SDK/ThunderKit editor pipeline was not run in this milestone.

## Phase status after this smoke

This statement records the status at the time of the smoke. Lifecycle-safe
camera discovery, no-op lifecycle patches, buffer visualizers, and vendor
probing were implemented afterward; see [`runtime-probe.md`](runtime-probe.md).
