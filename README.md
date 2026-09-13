# Redux Better AA

Better anti-aliasing and reconstruction for KSP2 Redux: custom TAA, NVIDIA
DLAA/DLSS, FSR native AA/upscaling, FXAA, SMAA and supersampling. Temporal modes smooth moving edges while keeping
the game's UI at native resolution.

**v0.6.1 is the first public beta.** Windows x64, Redux 0.2.8.5 and 0.2.9.0.
This branch prototypes modern FSR and upscaling on top of the v0.6.2 candidate.
It is not a published release; see the [implementation and limits](docs/architecture.md#upscaling-and-frame-generation-integration).
Most recent testing uses Redux 0.2.9.0, D3D11 and an RTX 5070 Ti at 1440p.

## Install

1. Install Redux, then close the game.
2. Download the mod ZIP from [Releases](https://github.com/Michionlion/ReduxBetterAA/releases)
   and extract it beside `KSP2_x64.exe`. You should have `mods/ReduxBetterAA/swinfo.json`.
3. From v0.6.2, new installs start with custom TAA (shown as **TAA**). Change modes in
   **Settings → Mods → Redux Better AA**. Updates preserve your saved selection, including Off.

DLAA/DLSS need matching Unity NVIDIA native libraries. FSR 4.1/3.1 uses a
separate optional FSR runtime package. [Direct downloads and installation](NATIVES.md).
The mod ZIP contains no game or vendor DLLs. Better Clouds is optional.
For updates, move the old mod folder outside `mods`, install the new one,
then restore your configuration and reports. Keep only one Better AA installation.

## Modes

| Mode | What to expect |
| --- | --- |
| TAA | Custom temporal AA, with sharpness and stability controls. |
| NVIDIA DLAA | Native-resolution reconstruction on supported RTX hardware. Defaults to preset K. |
| FSR 4.1 / FSR 3.1 Native AA | Official SDK selects the supported provider; settings show its actual family. |
| NVIDIA DLSS / FSR Upscaling | Reconstructs supported flight/KSC scenes at native display size with Quality, Balanced or Performance inputs. |
| FXAA Low / High, SMAA | Spatial AA without temporal history. |
| Supersampling | Renders at 125–200% per dimension; substantially more GPU work. |
| Off | Disables scene AA and releases Better AA's rendering resources. |

Native AA renders at 100%; upscaling uses reduced scene resolution. Unsupported
vendor modes are hidden in settings, F10 and hotkey cycling; runtime failures fall back to Off with a reason in diagnostics.
FSR 3.1 is the compatibility provider. There is no separate FSR2 backend;
older saved FSR selections migrate to the supported modern provider.
Map AA and foliage motion repair have separate settings. Physics is unchanged.
When DLAA is selected, each main-menu visit starts with preset K. Preset changes
there apply only to that visit; other controls keep their normal persistence.

## In practice

The launchpad hills exposed a depth/jitter mismatch in the game's terrain pass.
The published v0.6.1 fix reduced the measured flicker signal by **94–97%** across
DLAA, TAA and its then-supported FSR 2 backend in the saved test view, without changing their quality settings.
Its added CPU cost was about **0.003 ms per terrain depth draw**, with no managed
allocations in the measured draws. These measurements cover the terrain fix in that fixture.
[Measurements and rejected fixes](https://github.com/Michionlion/ReduxBetterAA/releases/download/v0.6.1/v0.6.1-terrain-measurements.md).

Before/after images and the beta's testing notes are in the
[release notes](https://github.com/Michionlion/ReduxBetterAA/releases/tag/v0.6.1).

## Known limits and reports

- This is a beta. Non-NVIDIA hardware, long sessions and the full range of flight/VAB scenes need more testing.
- The published v0.6.1 uses zero map jitter. The development build restores coherent map jitter and draws map icons after AA.
- Supersampling falls back to Off in the map and main menu.
- F10's live A/B comparison renders the scene twice. Its terrain-depth behavior differs from normal mode selection; stop it before judging terrain flicker or measuring performance.
- Upscaling is gated to the inspected Redux 0.2.9.0.104521 D3D11 camera graph. Menu/map/VAB use the selected provider's native AA; the active late close-vessel camera is not yet supported by the SR path.
- FSR 3.1 and DLSS have passed a limited RTX 5070 Ti player smoke. FSR 4.1 requires separate AMD hardware validation. This does not establish moving quality or a performance gain.
- Frame generation has an input/lifetime contract but no enabled provider yet. Native close-vessel composition after SR remains future work; it needs complete scene depth/motion before FG. There is no Ray Reconstruction.

For a rendering bug, keep it visible and use **F10 → Issue ZIP**. Review the
images, then attach the ZIP and reproduction steps to an
[issue](https://github.com/Michionlion/ReduxBetterAA/issues).
Reports stay in `mods/ReduxBetterAA/diagnostics/reports`; nothing is uploaded automatically.
Capture can pause the game, and screenshots can contain visible vessel names/UI.

[Build and contribute](CONTRIBUTING.md) · [Architecture and fixes](docs/architecture.md) ·
[MIT license](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)
