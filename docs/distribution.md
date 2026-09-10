# Beta distribution

Use a **versioned Windows x64 ZIP laid out relative to the game root**, with one
full variant per Redux/Unity target: 2.8.5/6000.4.1f1 and 2.9/6000.5.8f1.
Publish it as a GitHub prerelease, with SHA-256, supported Redux/Unity versions,
known limitations and a link to the issue template. No downloader, runtime
auto-updater or custom native bridge is needed. Keep Unity/native versions fixed
for the beta so a report identifies a reproducible configuration.

The full payload is:

```text
mods/ReduxBetterAA/ReduxBetterAA.dll
mods/ReduxBetterAA/swinfo.json
mods/ReduxBetterAA/Addressables/...  (the SDK's catalog, settings, link and bundle)
NVUnityPlugin.dll
nvngx_dlss.dll
AMDUnityPlugin.dll
README.md
INSTALL.txt
LICENSE
THIRD-PARTY-NOTICES.md
licenses/...
runtime-receipt.json
package-manifest.json
```

Redux supplies SpaceWarp2, ReduxLib, Harmony, PPv2, Newtonsoft.Json, Addressables
and Unity's managed modules. Do not duplicate them or ship Assembly-CSharp,
UnityPlayer, a game executable, saves, user configuration, reports, test harness,
editor files or development binaries. Better Clouds is not a dependency.

The three native files are the additional dependency set for the tested Unity
Windows player. Keep them beside `KSP2_x64.exe`; they are loaded during startup.
A portable package omits these and offers the spatial/TAA paths plus any vendor
runtime already available in the installed player. A missing runtime must hide
its public mode and produce a readable capability/fallback report.

## Build

First use the pinned Unity/ThunderKit workflow in
[build-and-package.md](phase-1/build-and-package.md). It produces the authoritative
mod archive. Then assemble the release without rebuilding or publicizing any
game assembly:

```powershell
pwsh -NoProfile -File tools/Package-Beta.ps1 `
  -RuntimeDirectory C:\reviewed-runtime-export `
  -RuntimeReceipt C:\reviewed-runtime-export\runtime-receipt.json
```

The default target is Redux 2.9. Add `-ReduxTarget 2.8.5` for its older runtime
export. Do not mix these native libraries between players. The managed mod
payload is shared; its loader manifest permits the two validated release families.

The packager validates the SDK payload, assembly/manifest versions, one asset
bundle, runtime hashes and supplied redistribution receipt. It requires a project
LICENSE for public output. [runtime-receipt.example.json](runtime-receipt.example.json)
is intentionally unapproved. Use `-LocalValidationOnly` only for an internal
validation artifact; its filename and embedded notice identify it as nonpublic.
Without `-RuntimeDirectory`, the same script assembles a portable variant.

## Runtime provenance and remaining release work

The workstation's Editor/runtime-support copies are sufficient for the local
experiment, but their presence is not evidence of redistribution clearance.
Obtain the matching, licensed player-export files and the exact accompanying
notices from the Redux/Unity distribution owner. Record their provenance and
reviewed hashes in the receipt. Do not substitute a random DLSS DLL or a different
Unity native plugin just because its filename matches.

Unity's runtime grant describes distribution integrated into a project and
notes that some third-party/service-provider uses can need further authorization;
that does not establish this mod publisher's rights to redistribute files taken
from another project's player. See the [Unity Editor Software Terms, sections
2 and 2.2](https://unity.com/legal/editor-terms-of-service/software).

NVIDIA permits specified application-integrated distribution subject to its
requirements, including protective downstream terms and applicable notices;
the SDK is not licensed as an unrestricted standalone redistributable. Review
the [NVIDIA DLSS license](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt),
its attribution supplement and the notices accompanying the exact binary.
The AMD Unity plugin is also part of Unity's runtime distribution; AMD's open
source FSR license alone does not establish rights to Unity's wrapper binary.

No public upload is performed by these tools. Before publication, settle the
original-code license, native-runtime receipt/notices, and outstanding visual
acceptance listed in [beta-review.md](beta-review.md).

## Updates and uninstall

Close the game. Back up the existing mod folder outside `mods` (the loader can
discover nested duplicate manifests), including configuration and diagnostics.
Replace the old mod payload as a unit so stale Addressables bundles are removed,
then restore only user configuration/diagnostics. Extract the full bundle into
the game root. Do not overwrite a different existing native runtime without
checking its owner and exact Unity version. Retain the package manifest with
the install for troubleshooting.

To uninstall, remove `mods/ReduxBetterAA`. Only remove the three root native
files if this package installed them, their hashes still match its manifest,
and no other mod requires them. Redux itself stays installed.
