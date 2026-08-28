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

Version 0.5.20 completes the AA settings and lifecycle audit documented in
Decision 0021. The Unity 6000.4.1f1 EditMode suite passes all 35 tests, the
prepare method succeeds, and ThunderKit records the deploy pipeline as finished
with successful addressable and assembly builds. ThunderKit 9.3.1 deliberately
calls `EditorApplication.Exit(1)` after its batch task, so its process exit code
is not used as the success signal; the pipeline log and inspected archive are.
The normal seven-file `Deploy/ReduxBetterAA.zip` SHA-256 is
`AB97CF69D45B6749C3934AF38DCE83394D14202D2B4C785A74D637575A1B9AE2`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.20-local-vendor-runtimes.zip` SHA-256 is
`B87F6B8E710D3D20FF58E09645415A752E18D56FC0B9531FCF82680BA49C007A`.
The installed 0.5.20 assembly hash is
`02DF42FADAF63648B4ADA299C03B1F817893D0D59679D1664CD570A2ED9A20A6`;
the existing user configuration and diagnostic capture hashes were unchanged
during installation.

Version 0.5.21 fixes the cross-mod Addressables identity collision and the
stock-AA Harmony signature, and changes every backend failure target to Off as
documented in Decision 0022. The Unity 6000.4.1f1 EditMode suite passes all 37
tests. The editor isolation check successfully loads the installed Better
Clouds bundle first and the rebuilt Better AA bundle second in one process.
ThunderKit records `Finished execution`; the inspected package contains the
DLL, manifest, catalog, settings, link metadata, and one Windows bundle. The
normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`2C87CCF0EED6F9C3750B7C206A5146D81991D40A3F95FEE1E5752A389BBB48B8`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.21-local-vendor-runtimes.zip` SHA-256 is
`98EC02BE3658FB799687064A0D6BBDD42411D0C2F36521CC1F9333AD2E35C519`.
The installed assembly SHA-256 is
`79F52A465246CFEC1A5D50338F1F1223D7A48D8E0C32A28A4D5F6FFCED1F15E9`;
its file version and manifest version are `0.5.21.0` and `0.5.21`. The existing
user configuration and all 125 diagnostic files were preserved. A main-menu
runtime smoke with Better Clouds loaded first records successful loading of all
five Better AA shaders and no AssetBundle or Harmony exception.

Version 0.5.22 adds the adaptive whole-frame launchpad classifier and the
zero-crossing-safe raw sign diagnostic documented in Decision 0023. The Unity
6000.4.1f1 EditMode suite passes all 38 tests. ThunderKit records `Finished
execution`; the inspected normal archive contains the DLL, manifest, catalog,
settings, link metadata, and one Windows bundle. The editor isolation check
loads the installed Better Clouds bundle before the rebuilt Better AA bundle.
The normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`F13FEF075CBFA5460298957C108B37AFDC993BDE860F12EB84804B60C6129DA1`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.22-local-vendor-runtimes.zip` SHA-256 is
`B1743DC8D0E79032E6EC12974BD23879D949C251A8E7E7AF8EA38E357201E08F`.
The installed assembly SHA-256 is
`C92F863306BDEA66569928E1D74CF33476B63B174DD1547AE3CBE148BF1F3931`;
its file version and manifest version are `0.5.22.0` and `0.5.22`. The existing
user configuration and all 127 diagnostic files were preserved. A main-menu
runtime smoke selects DLAA on `Camera.Scaled`, creates the 2560x1440 DLAA
context and sanitizer, and loads every Better AA shader without a bundle,
Harmony, or shader exception. Launchpad visual acceptance remains manual.

Version 0.5.23 reverts the adaptive production classifier, makes unavailable
sanitizer views explicit, and latches continuous transform-derived teleport
resets as documented in Decisions 0024 and 0025. The Unity 6000.4.1f1 EditMode
suite passes all 38 tests. ThunderKit log 54 records `Finished execution`; the
normal archive contains the DLL, manifest, catalog, settings, link metadata,
and one Windows bundle. The editor isolation check loads the installed Better
Clouds bundle before the rebuilt Better AA bundle. The normal
`Deploy/ReduxBetterAA.zip` SHA-256 is
`3A15AC15152ADEE86A66B154167A9257F1B9773F552BCC8E12F3020C9E20A65D`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.23-local-vendor-runtimes.zip` SHA-256 is
`5D7F5BC5749BAEC0452CA26F00DAC390523E9F7EBA0BF1D93369808158666DB2`.
The installed assembly SHA-256 is
`563E5DCB12F958F4EB573EF235A224F6DC09E27A873B350EB2525DCA41474B5F`;
its file version and manifest version are `0.5.23.0` and `0.5.23`. All 143
installed diagnostic files were preserved, and the complete 0.5.22 install was
backed up before replacing its one stale bundle. A harness-launched main-menu
smoke reports Redux Better AA 0.5.23 and Redux Better Clouds 0.2.0 active, loads
all Better AA shaders, and records no BetterAA bundle, Harmony, shader, or
runtime error. Launchpad and 100–150 km visual acceptance remain manual.

Version 0.5.24 selects Decision 0027 choice B on Redux 2.8.5: direct indirect
vegetation draws use `RenderMeshIndirect` with camera-only motion by default.
Choice E's motion rejection and camera fallback are off by default, and both
options are independently switchable in Ctrl+F10's Buffers tab. The Unity
6000.4.1f1 EditMode suite passes all 39 tests. TestHarness static, compilation,
and CLI-mock checks pass. ThunderKit log 61 records `Finished execution`; the
normal seven-file `Deploy/ReduxBetterAA.zip` SHA-256 is
`AC4133B440B20C1756CA1A26AD69BB811CA1E56339A8B1C6BAC2E886D082BD27`.
The installed assembly SHA-256 is
`6E7CFFB16013D82DBFFF45937D9B50421D09A52AF3EEEAF21F76FE6BAA43441E`;
its assembly and manifest versions are `0.5.24.0` and `0.5.24`. The existing
configuration and all 173 diagnostic files were preserved during installation.
Production comparison run `1c774870b64e` passes 8/8 assertions, captures the
clean B-on/E-off raw input and the restored B-off radial control, records 4,419
rerouted draws, and reports no test warnings or errors. One incomplete
vegetation draw during scene startup is safely delegated to the original path
and logged once; stable flight draws continue through the repair.

Version 0.5.25 makes projection jitter an observed scene capability as
documented in Decision 0029. Map view and the main menu keep their correct
`MapCamera` and `Camera.Scaled` resolve points but use zero projection and
dispatch jitter; Flight, KSC, and VAB retain the shared Halton sequence. The
Unity 6000.4.1f1 EditMode suite passes all 46 tests. ThunderKit log 66 records
`Finished execution`; the inspected normal archive contains seven files and
reports version 0.5.25. The normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`3D42C4837982AF9C630F53D344A4AF6AAC6D47A715B895CF71D083E53DB4C346`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.25-local-vendor-runtimes.zip` SHA-256 is
`964E040794D34A20EDDF6318138933CB59AEBAFD4851817EDECCEB06916C88EF`.
The complete 0.5.24 install was backed up before installing 0.5.25, and the
existing configuration and diagnostics were preserved. The installed assembly
SHA-256 is
`2B3AADD57559DC903913319153ABD11DB634438FC49B4188F678F226419942D2`;
its file and manifest versions are `0.5.25.0` and `0.5.25`. The user
configuration and 267 accumulated diagnostic files remain present. An installed-game
TestHarness run captures 120 consecutive menu/map frames across Custom TAA,
DLAA, and FSR2 with no test errors or warnings; full-sequence temporal
difference analysis confirms the rectangular planet corruption is absent.

Version 0.5.26 restores numeric feedback on the normal Sharpness and TAA
stability sliders, persists the foliage motion source repair, and adds an
independent map-view AA policy. Disabling map AA activates Off only in
`Map3DView` and preserves the selected flight mode. The Unity 6000.4.1f1
EditMode suite passes all 47 tests. ThunderKit log 67 records `Finished
execution`; the normal seven-file archive reports version 0.5.26 and has
SHA-256
`DEC09093A54E8247C55C5170869F4026B41B7A319A0EA38500BBAD135E0CA0E0`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.26-local-vendor-runtimes.zip` SHA-256 is
`7122CB8545DADDF0ED0AE4DC240BD53D9BB87EBBF55394C325BC7C3F0782DDFF`.
The previous complete install was backed up before overwriting the seven mod
payload files and three already-approved local vendor runtimes. The existing
configuration hash and all 277 diagnostic files were preserved. The installed
assembly SHA-256 is
`B55605EFE51D7116F8E2D122622FDC325AED891E4EF19A0FDCA9DC12E9E79EC2`;
its file and manifest versions are `0.5.26.0` and `0.5.26`.
Installed-game TestHarness run `7ee7aa9c8245` passes 2/2 assertions with 120
main-menu/map screenshots, no test warnings or errors, reports the installed
mod as 0.5.26, and logs all six user-facing settings during pre-initialization.

Version 0.5.27 changes the conservative DLAA preset to M and adds one-shot
cloud-source capture as documented in Decision 0031. The Unity 6000.4.1f1
EditMode suite passes all 47 tests. ThunderKit log 86 records `Finished
execution`; the inspected normal archive contains seven files and reports
version 0.5.27. The normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`8AB1A0209F37B4D0C477C769D1DF0857DA81B3C6D4CA36EF59B6AEDF6D35C67F`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.27-local-vendor-runtimes.zip` SHA-256 is
`A016DC621836207B05F00BFAD35A9C3CF2497C7F8E7116E9B03D99499B85DD45`.
The complete 0.5.26 installation was backed up before replacing the mod payload;
the six user setting values and accumulated diagnostics were retained. Five
unreferenced experimental Addressables bundles were moved into the recoverable
backup, leaving only the bundle named by the installed catalog. The installed
assembly SHA-256 is
`2F044B65B7EC860F5627FB163C4500875BC3E53A8C98020FEB5C1BB670E099C0`;
its file and manifest versions are `0.5.27.0` and `0.5.27`.
Installed-game TestHarness run `291ee074078a` passes with no new log errors,
records schema 22 and 22 live cloud render-target descriptors, and writes the
presented screenshot plus all five cloud-source images. The automated launch
closed cleanly after the test.

Version 0.5.28 pairs the selected indirect-vegetation reroute with the exact
invalid object-history exclusion documented in Decision 0032. The Unity
6000.4.1f1 EditMode suite passes all 47 tests. ThunderKit log 91 records
`Finished execution`; the inspected normal archive contains seven files and
reports version 0.5.28. The normal `Deploy/ReduxBetterAA.zip` SHA-256 is
`BCE95E37830997012CE8573B08E62015661677FF5DAFC64E305A24BB9ABD1B8B`.
The workstation-only
`Deploy/ReduxBetterAA-0.5.28-local-vendor-runtimes.zip` SHA-256 is
`83A6FDA452B74F4643E2F9E330B925338A462175D9971FA77019B3F065CF2941`.
The installed 0.5.27 package was backed up before installation; its old and
accidentally nested Addressables trees were moved into that recoverable backup.
User configuration and 145 diagnostic files were retained. The installed
assembly SHA-256 is
`43A14C6E7A6EFBCB2E953E084343683EBD30699F99B1B532BD3A206B9F46D536`;
its file and manifest versions are `0.5.28.0` and `0.5.28`.
Installed-game TestHarness run `a7295b623d9b` passes 2/2 assertions with 18
stationary, moving-camera, and DLAA-K captures and no new log errors or warnings.
The capability report records B active and available, sanitizer E disabled,
and 3,407 rerouted draws; no diagnostic motion-pass override was used. Cloud
behavior remains shelved, while schema-22 and five-image F10 capture diagnostics
stay installed for a future reproduction.
