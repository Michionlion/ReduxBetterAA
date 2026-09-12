# Contributing

## Build from a fresh checkout

1. Install [KSP2 Redux](https://github.com/KSP2Redux/Redux) 0.2.9.0 into your KSP2
   installation and close the game.
2. Install Git, PowerShell 7, Python 3 and activate Unity **6000.5.8f1**
   through [Unity Hub](https://unity.com/download).
3. Clone this repository and run:

```powershell
git clone https://github.com/Michionlion/ReduxBetterAA.git
cd ReduxBetterAA
pwsh -NoProfile -File tools/Build.ps1 -Ksp2Root 'C:\Games\Kerbal Space Program 2'
```

Use your actual game directory. If Unity is installed elsewhere, also pass
`-Unity 'D:\Unity\6000.5.8f1\Editor\Unity.exe'`.

The first build resolves the pinned Unity packages and copies compile references
from the installed Redux player into ignored `Packages/KSP2_x64`. No game files
are modified. The build needs no assembly publicizer, test harness, saved
campaign or additional SDK checkout.
Subsequent builds can omit `-Ksp2Root`; pass it again after updating Redux.

The build prepares Addressables, runs the EditMode tests, compiles player scripts
with Unity, then validates and packages the result. Extract
`Deploy/ReduxBetterAA-<version>.zip` beside `KSP2_x64.exe`. The installed
manifest should be `mods/ReduxBetterAA/swinfo.json`. The unversioned ZIP is a
build intermediate. Logs and test results are in `Logs`.

DLAA and FSR 2 require the separate [native libraries](NATIVES.md) when
playing. TAA, spatial AA and supersampling need no additional native files.

## Checks and publishing

Run `pwsh -NoProfile -File tools/Test-Release.ps1` for portable packaging,
source and script checks. No Python packages need installing. Complete the
[in-game release checklist](tests/README.md) before publishing.

Update the version in `Copied/swinfo.json` and
`Code/AssemblyInfo.cs` under `Assets/ReduxBetterAA`. Write
`docs/releases/vX.Y.Z.md`, record the checklist results there, and commit on main.

```powershell
pwsh -NoProfile -File tools/Release.ps1 -Version X.Y.Z -Publish
```

Publishing also needs GitHub CLI authentication (`gh auth login`) and the Windows
Mono player support files for Unity 6000.5.8f1 and 6000.4.1f1. Install those
locally through Unity Hub. The release script uses `-Unity` for the current
editor and the standard Hub path for 6000.4.1f1. For custom locations, invoke
from PowerShell with `-RuntimeEditors @('D:\Unity\6000.5.8f1\Editor',
'D:\Unity\6000.4.1f1\Editor')`. Ordinary builds need only the current editor.

Every release packages both runtime ZIPs from those local files, validates
their pinned hashes, and includes only three DLLs and one notices file per ZIP.
When adding support for a new Unity player, review its vendor terms and update
`tools/runtime-targets.json` from verified official player files.
The script requires clean source, runs the checks and build, verifies that
tracked files and HEAD stayed unchanged, and pushes main and the annotated tag
atomically. It uploads the mod ZIP, separate runtime ZIPs, changelog since the
previous release, build information and checksums to a draft, downloads and
verifies each file, then publishes the beta. Use `-Stable` for a stable release.

Omit `-Publish` to build and prepare release files without publishing.
An existing public release is never overwritten. Failed draft uploads can be
retried from the same commit. No GitHub Actions or license secrets are used.

Every Unity step must exit 0. Packaging also requires a fresh ZIP, a completion
marker and no compiler/shader errors.

## Working on the mod

Read [the architecture and fixes](docs/architecture.md) before changing rendering.
Keep source, build configuration and lasting regression tests here; keep captures,
private saves, decompiled code and investigation scripts outside the checkout.
Record validation in the PR or release, with scene, backend, Redux/Unity version,
GPU and resolution. Use Git history for past decisions; update the architecture
when a design changes. Preserve [third-party notices](THIRD-PARTY-NOTICES.md).

Useful tools, downloaded separately:

- [ILSpy](https://github.com/icsharpcode/ILSpy/releases) for inspecting assemblies.
- [RenderDoc](https://renderdoc.org/) and Unity's Profiler/Frame Debugger for rendering.
- [Redux Test Harness](https://github.com/Michionlion/ReduxTestHarness) for optional in-game automation.
- [NStrip](https://github.com/bbepis/NStrip/releases) for assembly experiments;
  Better AA does not require it. Never replace installed game assemblies.
- [GitHub CLI](https://cli.github.com/) for publishing releases.
