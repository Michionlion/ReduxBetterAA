# Redux Better AA

Better anti-aliasing for KSP2 Redux: custom TAA, NVIDIA DLAA, FSR 2 Native AA,
FXAA, SMAA and supersampling. Temporal modes smooth moving edges while keeping
the game's UI at native resolution.

**v0.6.1 is the first public beta.** Windows x64, Redux 0.2.8.5 and 0.2.9.0.
Most recent testing uses Redux 0.2.9.0, D3D11 and an RTX 5070 Ti at 1440p.

## Install

1. Install Redux, then close the game.
2. Download `ReduxBetterAA-0.6.1.zip` from [Releases](https://github.com/Michionlion/ReduxBetterAA/releases)
   and extract it beside `KSP2_x64.exe`. You should have `mods/ReduxBetterAA/swinfo.json`.
3. Open **Settings → Mods → Redux Better AA** and choose a mode. New installs start at Off.

DLAA and FSR 2 need matching Unity native libraries. [Direct downloads and installation](NATIVES.md).
The mod ZIP contains no game or vendor DLLs. Better Clouds is optional.
For updates, move the old mod folder outside `mods`, install the new one,
then restore your configuration and reports. Keep only one Better AA installation.

## Modes

| Mode | What to expect |
| --- | --- |
| TAA | Custom temporal AA, with sharpness and stability controls. |
| NVIDIA DLAA | Native-resolution reconstruction on supported RTX hardware. Defaults to preset K. |
| FSR 2 Native AA | Native-resolution reconstruction using Unity's FSR 2 plugin. |
| FXAA Low / High, SMAA | Spatial AA without temporal history. |
| Supersampling | Renders at 125–200% per dimension; substantially more GPU work. |
| Off | Disables scene AA and releases Better AA's rendering resources. |

All modes except Supersampling render at 100%. Unsupported vendor modes are
hidden; runtime failures fall back to Off with a reason in diagnostics.
Map AA and foliage motion repair have separate settings. Physics is unchanged.
The main menu starts with DLAA K; changes there apply to that menu visit and
leave the saved gameplay preset alone.

## In practice

The launchpad hills exposed a depth/jitter mismatch in the game's terrain pass.
The fix reduced the measured flicker signal by **94–97%** across DLAA, TAA and
FSR 2 in the saved test view, without changing their quality settings.
Its added CPU cost was about **0.003 ms per terrain depth draw**, with no managed
allocations in the measured draws. These measurements cover the terrain fix in that fixture.
[Measurements and rejected fixes](docs/decisions/0044-northwest-hills-flicker-investigation.md).

Before/after images and the beta's testing notes are in the
[release notes](https://github.com/Michionlion/ReduxBetterAA/releases/tag/v0.6.1).

## Known limits and reports

- This is a beta. Non-NVIDIA hardware, long sessions and the full range of flight/VAB scenes need more testing.
- The published v0.6.1 uses zero map jitter. The development build restores coherent map jitter and draws map icons after AA; see [the investigation](docs/decisions/0045-map-jitter-and-icons.md).
- Supersampling falls back to Off in the map and main menu.
- F10's live A/B comparison renders the scene twice. Its terrain-depth behavior differs from normal mode selection; stop it before judging terrain flicker or measuring performance.
- There is no DLSS upscaling, Frame Generation or Ray Reconstruction.

For a rendering bug, keep it visible and use **F10 → Issue ZIP**. Review the
images, then attach the ZIP and reproduction steps to an
[issue](https://github.com/Michionlion/ReduxBetterAA/issues).
Reports stay in `mods/ReduxBetterAA/diagnostics/reports`; nothing is uploaded automatically.
Capture can pause the game, and screenshots can contain visible vessel names/UI.

[Build and release](docs/building.md) · [Developer docs](docs/README.md) ·
[MIT license](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)
