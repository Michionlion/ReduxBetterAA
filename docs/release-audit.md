# v0.6.1 release audit

The release is built from the pinned local Unity/SDK project. The loader needs
five runtime files: the mod DLL, manifest, catalog, catalog hash and one shader
bundle. The public packager adds installation notes and licenses under the same
mod folder, plus a hash manifest.

| Item | Decision |
| --- | --- |
| `settings.json`, `AddressablesLink/link.xml` | Remove from public ZIP. SpaceWarp2 calls `LoadContentCatalogAsync` on the mod catalog directly. |
| Nine runtime shaders | Keep. They implement AA, foliage repair and the user-invoked diagnostic tools. |
| Issue ZIP and F10 diagnostics | Keep for beta reports. Buffer capture and profiling remain opt-in. |
| Tests, baseline shaders, PDBs, saves, configs, captures | Exclude. Packager rejects unexpected entries. |
| Game/Unity/vendor dependencies | Exclude. Redux supplies managed dependencies; native downloads link directly to Unity. |
| Old bootstrap compiler and runtime-receipt packaging | Remove. They are not the full build or the public distribution path. |
| Original code and derived Unity shaders | MIT, with the original Unity attribution retained. |
| Build credentials and hosted jobs | None. Releases are built and tested locally. |

Package tests reject extra binaries, unsafe paths, symlinks, duplicate Windows
paths, absent/wrong bundles, extra catalog shaders, mismatched versions, omitted
licenses and tampered content. Packaging is repeatable for identical inputs.
The release script rejects dirty/staged/untracked changes and changed HEAD,
and checks downloaded release attachments before publishing the draft.

The 0.6.1 package, installed without the two build-metadata files, passed
**268 in-game assertions** with no harness errors or warnings: all public modes,
three switching cycles, lost-texture recovery for TAA/DLAA/FSR 2, supersampling
scales, map overrides and four issue ZIPs. The local run is
`Artifacts/visual-20260911-213220`. The pinned Unity build passed all
**121 EditMode tests**; portable checks include 15 image-analysis tests,
10 package tests and Git source-state guards. The broader terrain evidence
and its limits remain in decision 0044.
All four issue ZIPs passed file-hash and same-frame checks. Release screenshots
passed a separate six-assertion run. Original settings and the prior test
adapter were restored after validation.

The SDK preparation used to recreate every pipeline with random subasset IDs.
It now retains checked-in pipelines and creates them only when absent, so a
normal build can leave source unchanged. It explicitly registers all nine
runtime shaders. Build-only paths, imported game references and local captures
are never copied into the mod ZIP.

Both official Unity native archives were downloaded for inspection. The three
DLLs in the 6000.5.8f1 archive match the installed Redux 2.9 runtime byte for byte;
the 6000.4.1f1 files match the pinned editor's Windows player files. These are
provenance checks, not permission to mirror the DLLs. Users get direct Unity
links and manual copy instructions in [NATIVES.md](../NATIVES.md).

| Official archive | SHA-256 |
| --- | --- |
| Unity 6000.5.8f1 Windows Mono support | `72abb13e5f5afc16aa03c5efba801217719aaba723d26a13937edb9033a6af6d` |
| Unity 6000.4.1f1 Windows Mono support | `e1ba47f25eae5504b23cd4cdfe6874395dbe24dcf0f4b9a5674d70ede24afd4e` |
