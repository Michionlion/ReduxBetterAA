# Contributing

Start with [building](docs/building.md). A normal build needs Windows,
Git, PowerShell 7, Python 3 and the pinned Unity editor, plus an installed
Redux game. It does not need NStrip, a test harness, a saved campaign or native
DLAA/FSR libraries. Python uses only its standard library.

The repository contains mod source, Unity project/build configuration, unit
tests, package checks and current documentation. Download dependencies through
the pinned Unity package manifest; do not vendor tools or game files.

Read [SPEC.md](SPEC.md) for rendering contracts. Keep changes focused and include
the checks you ran in the PR. For rendering changes, describe the scene,
backend, game/Redux version, GPU, resolution and observed result. Follow the
[release checklist](tests/README.md); do not infer motion quality from stills.

## Optional tools

Download these separately and keep them outside the checkout:

- [ILSpy](https://github.com/icsharpcode/ILSpy/releases) for inspecting game assemblies.
- [RenderDoc](https://renderdoc.org/) and Unity's Profiler/Frame Debugger for render inspection.
- [Redux Test Harness](https://github.com/Michionlion/ReduxTestHarness) for in-game automation.
  Follow that repository's setup instructions; it is not a build dependency.
- [NStrip](https://github.com/bbepis/NStrip/releases) for assembly experiments.
  Better AA does not require publicized assemblies. Never replace installed game DLLs.
- [GitHub CLI](https://cli.github.com/) for publishing releases.

Keep captures, private saves, decompiled code and temporary test adapters in an
external investigation directory. Promote useful checks into small, repeatable
regression tests instead of retaining an entire debugging experiment. Link
review evidence from the PR or release; do not commit recordings or screenshots.
Pre-cleanup decisions and experimental scripts are kept in a separate maintainer
archive, together with the original Git history and a commit mapping.

## Releases

Releases are built and published locally with [tools/Release.ps1](tools/Release.ps1).
There is no CI service or license-secret setup. Public downloads contain only
the mod and installation/license files. Native-library installation remains
manual; see [NATIVES.md](NATIVES.md).
