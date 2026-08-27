# Phase 1 build and package

## Pinned environment

- Unity `6000.4.1f1`
- Redux template `0.2.8.5` (`455b945`)
- Redux SDK `beta-6` (`3e05f9b`)
- Target KSP2 Redux `0.2.8.5.103184-beta` (`ffc94930`)

Do not import assemblies from a different player or upgrade the SDK as part of
Phase 1.

## First import

On a new checkout, configure the KSP2 executable and game root in ThunderKit.
`tools/Initialize-ThunderKitImport.ps1` can seed the initial ignored
`Packages/KSP2_x64` package when the SDK cannot compile before ThunderKit's
first import. It copies only assemblies from the selected exact player and uses
ThunderKit-compatible metadata; it is not a release builder.

After seeding, run the normal ThunderKit import configuration. The command-line
wrapper below invokes that configuration without replacing it:

```powershell
unity -batchmode -quit `
  -projectPath G:\KSP2\ReduxBetterAA `
  -executeMethod Utilities.Editor.InvokeThunderKitImport.Run `
  -logFile G:\KSP2\ReduxBetterAA\Logs\unity-import.log
```

If Unity stops to compile between configuration executors, run the wrapper
again. Completion is recorded as the final executor index in the log. Then run
the generated `Assets/ImportKsp2ToEditor.asset` ThunderKit pipeline so the
ignored game content, catalog, and StreamingAssets are imported.

## Prepare mod assets

Run the idempotent editor setup after changing mod assets or regenerating SDK
files:

```powershell
unity -batchmode -quit `
  -projectPath G:\KSP2\ReduxBetterAA `
  -executeMethod Utilities.Editor.PrepareReduxBetterAAMod.Run `
  -logFile G:\KSP2\ReduxBetterAA\Logs\unity-prepare-mod.log
```

This uses SDK-owned methods to create Addressables groups and ThunderKit
pipelines, registers the diagnostic and AA-support shaders under their runtime
addresses, and keeps the
curated `Copied/swinfo.json`. The curated manifest intentionally omits the
optional `version_check` field until a published URL exists.

## Compile check

```powershell
unity -batchmode -quit `
  -projectPath G:\KSP2\ReduxBetterAA `
  -logFile G:\KSP2\ReduxBetterAA\Logs\unity-compile.log
```

Require `Exiting batchmode successfully`, with no C# or shader error.

## Package

Run the SDK-generated zip pipeline:

```powershell
unity -batchmode -quit `
  -projectPath G:\KSP2\ReduxBetterAA `
  -executeMethod ThunderKit.Core.Pipelines.Pipeline.BatchModeExecutePipeline `
  '-pipeline=Assets/ReduxBetterAA/Pipelines/Deploy to Zip File.asset' `
  -logFile G:\KSP2\ReduxBetterAA\Logs\unity-deploy-zip.log
```

ThunderKit's batch entry point currently requests editor exit code `1` even
after a successful pipeline, so validate the ThunderKit pipeline log and fresh
artifacts instead of treating that code alone as failure. Require:

- `Finished execution` in the ThunderKit pipeline asset log;
- successful Addressables and managed-assembly builds;
- no `Halted execution`, C# error, or shader error;
- `Deploy/ReduxBetterAA.zip` containing the mod files at archive root;
- DLL, manifest, catalog, settings, link metadata, and the Windows asset bundle.

Extract those files into one `mods/ReduxBetterAA` folder under the target
player. Do not leave a second copy of the same mod ID under `mods/__Testing`.

## Phase 4 workstation-local NVIDIA runtime

The normal `ReduxBetterAA.zip` intentionally excludes NVIDIA/Unity native
binaries. For the maintainer-approved Phase 4 test machine, use the separate
`Deploy/ReduxBetterAA-0.4.2-local-nvidia.zip`. Its root is laid out to merge
directly into the KSP2 game root:

```text
NVUnityPlugin.dll
nvngx_dlss.dll
mods/ReduxBetterAA/...
LOCAL-NVIDIA-RUNTIME-README.md
```

These exact files are copied from the signed `win64_player_nondevelopment_mono`
variation in the locally installed Unity 6000.4.1f1 editor. They are for this
test installation only. Future distributable builds must source and license
the corresponding files through Redux core's matching Unity player export.

## Workstation-local vendor runtime bundle

Version 0.5.3 uses `Deploy/ReduxBetterAA-0.5.3-local-vendor-runtimes.zip` for
this test machine. It keeps the existing NVIDIA files and adds the signed Unity
6000.4.1f1 `AMDUnityPlugin.dll` at archive root. The root is laid out to merge
directly into the KSP2 game root:

```text
AMDUnityPlugin.dll
NVUnityPlugin.dll
nvngx_dlss.dll
mods/ReduxBetterAA/...
LOCAL-VENDOR-RUNTIMES-README.md
```

The AMD file is 8,831,920 bytes with SHA-256
`E070D3BBC31B29246CB4E27378FBE76CB332D5499CF7F7F5B3F85D2131BC381E`
and a valid Unity Technologies SF Authenticode signature. The normal
`ReduxBetterAA.zip` excludes all three native vendor files.

The 0.5.3 local bundle has SHA-256
`745FB3C1DE7DB1E96EC90BE250448A60C3E49CA80C34CA99A2514A96E5B5CCD0`.

Version 0.5.4 uses
`Deploy/ReduxBetterAA-0.5.4-local-vendor-runtimes.zip` with the same three
signed workstation-local runtimes. It adds explicit Unity-to-vendor motion
direction, camera reprojection fallback, sign-agreement diagnostics, and PPv2
GPU pre-exposure. Its SHA-256 is
`A350BCC3CEE874E2FF7FAC0753C374BB4D7A18A11292DC5AB5361B76FBF6E9FF`.

Version 0.5.5 uses
`Deploy/ReduxBetterAA-0.5.5-local-vendor-runtimes.zip` with those same three
signed workstation-local runtimes. It adds live Buffers-tab camera discovery,
moving solid-depth-edge vendor bias masks, and improved discontinuity handling.
Its SHA-256 is
`E6FCC855C5815E879275920DF542BF4C3EE91BC2A180EC37ADA5F41F4CCC7324`.

Version 0.5.6 uses
`Deploy/ReduxBetterAA-0.5.6-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It adds exact floating-origin history handling,
camera-cut hardening, bounded PPv2 exposure readbacks, skipped-post-render
projection recovery, and frame-pacing correlation logs. Its SHA-256 is
`786B86C2F6994B661FA9EABCC597781B43EA186975F4C4B2986B16BE0D373A73`.
The normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`05F638377F8C38001BAC1E567E434DA257B825FA789FA4DA16F7DEAA340B5888`.

Version 0.5.10 uses
`Deploy/ReduxBetterAA-0.5.10-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It adds KSC/main-menu camera discovery, bounded
camera-consistency rejection for the finite launchpad quadrant field,
and same-moment diagnostic reports. Its SHA-256 is
`C77F3692407C5A0A148DE30704B1AC47D187CD8E0B7B4A66FC54A3304D9CAC48`.
The normal 0.5.10 `Deploy/ReduxBetterAA.zip` SHA-256 is
`2BE75144ED27CCCFC5286AB430206E6B2926B853A3C26DDB5E8F3DB093AE1B94`.

Version 0.5.11 uses
`Deploy/ReduxBetterAA-0.5.11-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It adds the same-frame launchpad motion-field
classifier, 64-pixel/frame vendor and Custom TAA safeguards, sanitized/decision
diagnostics, a six-view capture burst, and Unity-versus-project matrix telemetry.
Its SHA-256 is
`1BB3DC50FD5C5A1C50F96A83446ED36704B5C91E2444B239F5896D292F8316BF`.
The normal 0.5.11 `Deploy/ReduxBetterAA.zip` SHA-256 is
`7D19D2E110E9A9B06350B8DC969EB0820E0279AD4E645E068213330432195ED0`.

Version 0.5.12 uses
`Deploy/ReduxBetterAA-0.5.12-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It adds coherence-aware motion validation, shared
Custom/DLAA/FSR2 sanitization, non-jittered Custom camera history, and a
dedicated motion-statistics shader. Its SHA-256 is
`C719BC81B72ABF683AB3A6962CB577D2A444FD5BA10E65EC413773A137796FCC`.
The normal 0.5.12 `Deploy/ReduxBetterAA.zip` SHA-256 is
`052F3A0423506E0E303FDA8EFAB249B5F5A466324DD9F0F4990D059EC0624E44`.

Version 0.5.13 uses
`Deploy/ReduxBetterAA-0.5.13-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It adds jitter-aware Custom depth alignment,
jitter-aware camera-motion fallback, paired raw/output-aligned depth diagnostics,
and report schema 15 jitter telemetry. Its SHA-256 is
`493FBC86A3DE78F8493B7CB1B1A2FEEE627250102EF2C05D7A25431F46868AF7`.
The normal 0.5.13 `Deploy/ReduxBetterAA.zip` SHA-256 is
`00EC24E3E8C1EFD3CA277E361D18F761458F2D7DF0EEB0CA284B82D5025B8F58`.

Version 0.5.14 uses
`Deploy/ReduxBetterAA-0.5.14-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It synchronizes the main-menu `Skybox` predecessor
and `Camera.Scaled` projection jitter, retains one pre-UI resolve, and adds
schema 16 camera-composition telemetry. Its SHA-256 is
`8EFC64F090B4BFB79E90CD57060F4332CB60D9C3BF66FE4A88D87A87B5A317A9`.
The normal 0.5.14 `Deploy/ReduxBetterAA.zip` SHA-256 is
`75F6A42E843DB3CB81D80C1D0FBC93045DA0F166CC2FA00ACC7C9B6829C19D87`.

Version 0.5.15 uses
`Deploy/ReduxBetterAA-0.5.15-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It corrects the FSR2 dispatch-jitter sign, adds
schema 17 projection/dispatch jitter telemetry, and clarifies the
single-sample limitation of the jitter-compensated depth diagnostic. Its
SHA-256 is
`513505DC2B25261AD5D18536741D9878425A50BB4EF768592A77C07808C3A097`.
The normal 0.5.15 `Deploy/ReduxBetterAA.zip` SHA-256 is
`D618A6D50F8B8BFA259BEE9EBF068C899E5E90C7779F4B842AF379D5A190B508`.

Version 0.5.16 uses
`Deploy/ReduxBetterAA-0.5.16-local-vendor-runtimes.zip` with the same signed
workstation-local runtimes. It consolidates the normal settings page, disables
KSP's conflicting MSAA selector, filters vendor modes by startup capability,
and keeps advanced controls in Ctrl+F10. Its SHA-256 is
`719870EA0D8160FF1C018D8B7C2CE4DB862F3D64E907A93237854759C1E2F9CE`.
The normal 0.5.16 `Deploy/ReduxBetterAA.zip` SHA-256 is
`3509B73630B914DDF616ECB786E432F74E5A9D66D2BCE13AC2EAF26BEBB4F359`.

Version 0.5.17 adds coordinator-owned stock FXAA Low/High and PPv2 SMAA
backends. The normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`310532C1AF2413BD6A8507D7D690B73F8E99FF0FEE6FFB3D0A2AEAFC6E472A07`.

Version 0.5.18 removes unrelated renderer overrides, resources, settings, and
diagnostics from Redux Better AA. Plain `F11` is no longer consumed;
`F10` retains the AA report-and-screenshot shortcut. The normal
`Deploy/ReduxBetterAA.zip` SHA-256 is
`B0E74D38E6F77E85ABC624D9C61DC4CC6BB4E7377B0FBB693C033CCCBDA73742`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.18-local-vendor-runtimes.zip` SHA-256 is
`F1C206838B292121DAE6EB3E729FBAC9B9C3A4EB6D5FBBB06E714C9151126D4A`.

Version 0.5.19 assigns the engineering panel to `Ctrl+F10` and the combined
same-moment report and screenshot to plain `F10`. The normal
`Deploy/ReduxBetterAA.zip` SHA-256 is
`71DB54708E8795062BF715E6150A3065C57CF9C0C391AC2F57CA70D3448D212A`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.19-local-vendor-runtimes.zip` SHA-256 is
`5CA57020FFFEFC2E7D5D3938BE5F703EE4ED9F6B9538CDACA3CA29576DDFBBC0`.
