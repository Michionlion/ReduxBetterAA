# Decision 0000: Phase 1 bootstrap and loader gate

- Status: Accepted for the Phase 1 bootstrap milestone
- Date: 2026-08-22
- Phase: 1 — Render probe and capability discovery
- Scope: SPEC Section 9.2, mod/lifecycle layer (entry point only)

## Context

The repository began as documentation only. Before renderer discovery, hooks, or
diagnostics are introduced, the project needs a package whose manifest and
managed entry point are accepted by the current Redux mod loader.

The target development/runtime versions are pinned to the versions that match
the local Redux test installation:

- Redux template `0.2.8.5`, commit `455b94546430a81a652f97e0917ef3af4cc4a817`
- Redux SDK `beta-6`, commit `3e05f9bd99ff17a09221b8211aa90c2ca9d58af2`
- Unity Editor `6000.4.1f1` (`336a400b9ea2`)
- Installed Redux `0.2.8.5.103184-beta`, commit `ffc94930`
- SpaceWarp manifest dependency `SpaceWarp2 >= 2.0.0`

Primary sources:

- <https://github.com/KSP2Redux/Redux.Template/tree/455b94546430a81a652f97e0917ef3af4cc4a817>
- <https://github.com/KSP2Redux/SDK/tree/3e05f9bd99ff17a09221b8211aa90c2ca9d58af2>
- <https://modding.ksp2redux.org>

## Decision

Adopt the official Redux Unity project template in this repository and define a
single `ReduxBetterAAMod : GeneralMod` entry point. The bootstrap emits
structured lifecycle logs and performs no renderer discovery or mutation.

The canonical Redux package contains exactly:

```text
ReduxBetterAA/
├─ ReduxBetterAA.dll
└─ swinfo.json
```

The SDK/ThunderKit workflow remains the release build path. Until the pinned
Unity editor is installed, `tools/Build-Bootstrap.ps1` provides a deliberately
narrow compiler/package smoke path against the exact managed assemblies in the
target Redux installation. It exists only to prove the loader gate and must not
grow into a parallel asset or release deployment system.

The bootstrap package omits the optional `version_check` member until a
published raw `swinfo.json` URL exists. Supplying the SDK asset's empty default
causes SpaceWarp to emit a one-shot `Malformed URL` diagnostic during startup.

## Consequences

- The loader contract can be verified before renderer work can affect game state.
- No Harmony patch group, event listener, coroutine, camera lookup, resource, or
  per-frame callback exists at this milestone.
- Loader success does not satisfy the Phase 1 exit gate and is not evidence for
  any camera, buffer, composition, or vendor-module claim.
- The temporal resolve placement decision remains reserved for
  `0001-temporal-resolve-placement.md` after runtime captures exist.

## Verification

Run:

```powershell
.\tools\Build-Bootstrap.ps1 -Deploy
```

Launch the target Redux build and require all three lifecycle markers in
`Ksp2.log`, with no loader error for `ReduxBetterAA`. Detailed evidence belongs
in `docs/phase-1/bootstrap-loader-smoke.md`.
