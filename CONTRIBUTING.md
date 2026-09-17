# Contributing

## Build from a fresh checkout

1. Install [KSP2 Redux](https://github.com/KSP2Redux/Redux) 0.2.9.0 into your KSP2
   installation and close the game.
2. Install Git, PowerShell 7.4+, Python 3 and activate Unity **6000.5.8f1**
   with Windows Build Support (Mono) through [Unity Hub](https://unity.com/download).
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

Ordinary builds produce a managed component ZIP. Public releases include the native
libraries; TAA, spatial AA and supersampling can also run without them. Build the AMD
component using [Build-Native.ps1](Native/README.md), then pass its archive as
`-FsrRuntimeZip` to release and candidate commands.

## Test a release candidate

Copy `.env.example` to `.env` and fill in the local paths. Point `KSP2_SOURCE`
at an untouched Steam installation or a copy of one, and `TEST_WORKSPACE` at
a writable directory outside the source and repository. The pipeline creates
it if needed and retains existing runs. Allow roughly 80 GB free for a new run;
Redux needs temporary patching space in addition to the game copy.
Supply the [Redux CLI](https://github.com/KSP2Redux/Updater/releases),
an external [test-harness checkout](https://github.com/Michionlion/ReduxTestHarness),
and a stock-part launchpad save facing the northwest hills. `redux-cli doctor --json`
reports the launcher configuration and game-profile paths. Close KSP2, Unity
and the Redux launcher before running.

Complete release packages include a mod build and runtimes for both supported
Redux versions. Set `UNITY_EDITOR` to Unity 6000.5.8f1 and `UNITY_EDITOR_LEGACY`
to 6000.4.1f1; both need Windows Build Support (Mono). Point `KSP2_LEGACY_ROOT`
at an installed Redux 0.2.8.5 game (Unity 6000.4.1f1 player); it is only read as
compile references for the legacy build. The harness builds against the candidate
game using the current editor; no additional compiler or SDK is needed.

```powershell
pwsh -NoProfile -File tools/Test-Candidate.ps1 -FsrRuntimeZip 'D:\Builds\BetterAA-FSR-Runtime-0.6.2-win-x64.zip'
```

The pipeline requires clean, committed mod and harness source. It copies the
clean game, installs the latest Redux beta, clones both repositories, and builds
a complete local release. It installs the complete ZIP, temporarily withholds its exact native payload to
test missing-runtime fallback, then restores the complete ZIP and tests vendor modes. Each run retains its
release files, versions, hashes, logs, screenshots and validation results in
`TEST_WORKSPACE`.

The launcher configuration and existing game profile are restored afterward.
Tests use a separate profile with only previously accepted legal preferences;
they do not accept new game agreements. The disposable game disables Redux's
optional online services and Discord integration and confirms those offline
preferences, so the first-run services dialog cannot cover the screenshots.
Nothing is tagged, pushed or published.
Moving-image quality and performance still need the checks below.

## Prepare and publish a release

Run `pwsh -NoProfile -File tools/Test-Release.ps1` for portable packaging,
source and script checks. No Python packages need installing. Complete the
[in-game release checklist](tests/README.md) before publishing.

Update the version in `Copied/swinfo.json` and
`Code/AssemblyInfo.cs` under `Assets/ReduxBetterAA`. Write
`docs/releases/vX.Y.Z.md` with player-facing changes and known limitations, then
commit. Put detailed checklist results in the commit or PR.

To build a complete release locally, pass your installed game and editor paths:

```powershell
./tools/Release.ps1 -Version X.Y.Z `
    -Unity 'D:\Unity\6000.5.8f1\Editor\Unity.exe' `
    -Ksp2Root 'C:\Games\Kerbal Space Program 2' `
    -LegacyUnity 'D:\Unity\6000.4.1f1\Editor\Unity.exe' `
    -LegacyKsp2Root 'C:\Games\Kerbal Space Program 2 - Redux 0.2.8.5' `
    -FsrRuntimeZip 'D:\Builds\BetterAA-FSR-Runtime-X.Y.Z-win-x64.zip'
```

Run this from PowerShell 7.4+. Results go under `Deploy/releases`. Unity asset
bundles only load in players of the editor version that built them, so each
Redux version gets its own mod build: the pinned editor builds in this checkout
against `-Ksp2Root`, then [Build-Legacy.ps1](tools/Build-Legacy.ps1) builds the
same commit with the 6000.4.1f1 editor against an installed Redux 0.2.8.5 player
(`-LegacyKsp2Root`) in a clone under `Library/BetterAA` (or `-LegacyWorkspace`;
keep it under 86 characters so the staged bundle path fits `MAX_PATH`). Both
installs are only read. Packaging refuses a bundle whose Unity version differs
from its target engine. Local
preparation works in a detached checkout and needs no GitHub authentication or
remote. It does not fetch, push, create tags or contact GitHub. Its changelog uses the curated release notes. Keep build steps, test counts,
internal implementation details and branch history out of player-facing documents.
Unity's first package resolution
still needs network access. Ordinary builds need only the current editor.

Every release produces one complete mod-and-runtime ZIP per supported Redux
version. Its mod assembly and shader bundle come from that engine's editor; the
NVIDIA libraries come from the pinned editors; AMD libraries come from the
engine-independent native bridge build. Packaging validates every component,
the bundle's engine, all vendor pins, original notices and a complete payload
hash manifest. The internal managed/native component ZIPs are not release downloads.
When adding support for a new Unity player, review its vendor terms and update
`tools/runtime-targets.json` from verified official player files.
The script requires clean source, runs the checks and build, and verifies that
tracked files and HEAD stayed unchanged. To publish, run the same command on
`main` with `-Publish` and GitHub CLI authentication (`gh auth login`). Publishing
requires the project's GitHub `origin`, fetches remote history, and pushes main
and the annotated tag atomically. It uploads the complete ZIPs,
curated changelog, build information and checksums
to a draft, downloads and verifies each file, then publishes the beta. Preparing a local candidate does not publish it. Use
`-Stable` for a stable release. An existing public release is never overwritten;
failed draft uploads can be retried from the same commit. No GitHub Actions or
license secrets are used.

Every Unity step must exit 0. Packaging also requires a fresh ZIP, a completion
marker and no compiler/shader errors.

## Working on the mod

Read [the architecture and fixes](docs/architecture.md) before changing rendering.
Keep source, build configuration and lasting regression tests here; keep captures,
private saves, decompiled code and investigation scripts outside the checkout.
Record validation in the PR or commit, with scene, backend, Redux/Unity version,
GPU and resolution. Use Git history for past decisions; update the architecture
when a design changes. Preserve [third-party notices](THIRD-PARTY-NOTICES.md).

Useful tools, downloaded separately:

- [ILSpy](https://github.com/icsharpcode/ILSpy/releases) for inspecting assemblies.
- [RenderDoc](https://renderdoc.org/) and Unity's Profiler/Frame Debugger for rendering.
- [Redux Test Harness](https://github.com/Michionlion/ReduxTestHarness) for optional in-game automation.
- [NStrip](https://github.com/bbepis/NStrip/releases) for assembly experiments;
  Better AA does not require it. Never replace installed game assemblies.
- [GitHub CLI](https://cli.github.com/) for publishing releases.
