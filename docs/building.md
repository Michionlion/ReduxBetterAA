# Build and publish

Releases are built locally. There are no GitHub Actions jobs, Unity license
secrets or private build-input repositories to maintain.

## Setup

Use Windows, Git, PowerShell 7, Python 3.12 and the GitHub CLI (`gh auth login`).
Install and activate **Unity 6000.4.1f1** through Unity Hub. Keep the SDK and
package versions in `Packages/manifest.json`; the current SDK commit is
`3e05f9bd99ff17a09221b8211aa90c2ca9d58af2`.

Open the project, let packages resolve, and configure ThunderKit for the
matching Redux 0.2.8.5 player. Import its assemblies into the ignored
`Packages/KSP2_x64` package. If the first import cannot compile, use
`tools/Initialize-ThunderKitImport.ps1 -Ksp2Root <game> -UnityEditorRoot <editor>`
after packages have resolved, then complete the normal ThunderKit import.
See the [original import notes](phase-1/build-and-package.md) for troubleshooting.
Do not commit game assemblies. This editor builds the managed mod tested with
both documented Redux families; native DLLs must match the actual player.

```powershell
python -m pip install -r tools/requirements-test.txt
pwsh -NoProfile -File tools/Test-Release.ps1
pwsh -NoProfile -File tools/Build-Beta.ps1
```

The first command installs the analysis-test dependencies. The checks exercise
image analysis, packaging and release guards. The build prepares Addressables,
runs all EditMode tests and builds `Deploy/ReduxBetterAA.zip` with ThunderKit.
Logs and test XML are under `Logs`. The SDK ZIP is an intermediate artifact;
`Package-Beta.ps1` makes the installable public ZIPs.

ThunderKit's batch entry point exits with code 1 even on success. The build
wrapper accepts only 0/1 for that step and also requires a fresh ZIP, a successful
pipeline log and no compiler/shader/pipeline errors. Test steps must exit 0.

## Make a release

Update the version in `Copied/swinfo.json`, `swinfo.asset` and `Code/AssemblyInfo.cs`
under `Assets/ReduxBetterAA`. Write `docs/releases/vX.Y.Z.md`, update the user
docs, review the rendering changes and commit everything on `main`.

```powershell
pwsh -NoProfile -File tools/Release.ps1 -Version 0.6.1 -Publish
```

The command requires a clean checkout, checks the source versions, runs the
portable checks and complete Unity build, validates the package, and checks
that the build did not change tracked files or HEAD. It then pushes `main` and
the annotated version tag atomically. The release stays a draft until every
uploaded attachment has been downloaded and hash-checked.

Release attachments are the mod ZIP, changelog since
the previous published release, build information and SHA-256 checksums.
The first changelog includes the repository's initial history. Beta releases
are marked prerelease; use `-Stable` only when making a stable release.
`-Unity <path>` selects the pinned editor at a different location.

Omit `-Publish` to run the whole build without pushing or publishing. Outputs
are kept under `Deploy/releases`. If Unity rewrites a tracked SDK asset, review
and commit that change, then rerun; the script will not auto-commit or hide it.
A failed upload leaves a draft. Rerunning from the same commit rebuilds and
retries that draft; an already published release or conflicting tag is rejected.

Rendering changes still need in-game checks. Use the [terrain regression](terrain-flicker-testing.md),
[quality suite](taa-quality-testing.md) or [mode/lifecycle suite](visual-testing.md)
as appropriate. The release command does not launch the game or claim broad
visual coverage from unit tests.

`tools/Run-VisualTests.ps1 -Scene Release` captures matching Off, TAA and DLAA K
screenshots for release notes, using the local launchpad fixture. It does not
measure performance. Keep only selected, reviewed images in `docs/images`.
