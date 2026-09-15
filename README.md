# Redux Better AA

Better AA adds anti-aliasing options to KSP2 Redux and fixes flicker on terrain,
water and distant coastlines. Choose TAA, NVIDIA DLAA, FSR Native AA, FXAA, SMAA
or supersampling. The flight UI stays at its normal resolution.

Supports Windows x64 with Redux **0.2.9.0** or **0.2.8.5**.
See [what's new in v0.6.2](docs/releases/v0.6.2.md).

## Install

1. Install KSP2 Redux, then close the game.
2. Download the ZIP for your Redux version from
   [Releases](https://github.com/Michionlion/ReduxBetterAA/releases).
3. Extract it into the folder containing `KSP2_x64.exe`. You should have
   `mods/ReduxBetterAA/swinfo.json`.
4. Open **Settings → Mods → Redux Better AA** to choose a mode.

Each ZIP includes the mod and the libraries needed for DLAA and FSR. You only
need one download. Better Clouds is optional.

New installs start with **TAA**. Updates keep your saved selection, including
Off. To update, move the old `ReduxBetterAA` folder outside `mods` before
extracting the ZIP, then restore your configuration and reports.
See [installation and removal](docs/INSTALL.md) for details.

## Modes

| Mode | Description |
| --- | --- |
| TAA | Smooths edges across frames, with adjustable sharpness and stability. |
| NVIDIA DLAA | NVIDIA's native-resolution AA for supported RTX GPUs. Uses preset K by default. |
| FSR Native AA | AMD's native-resolution AA, including FSR 3.1 on compatible GPUs. The menu shows the available FSR version. |
| FXAA Low / High | Fast edge smoothing with no frame history. High smooths more edges. |
| SMAA | Edge smoothing with no frame history; an alternative to FXAA. |
| Supersampling | Renders at 125–200% resolution for finer detail, at a substantial GPU cost. |
| Off | Turns scene anti-aliasing off. |

TAA, DLAA and FSR work at native resolution. Map AA and foliage motion repair
can be toggled separately. Supersampling is available in flight and the VAB;
it switches to Off in the map and main menu.

DLAA starts each main-menu visit on preset K. Changing its preset there lasts
for that visit; your saved flight preset is kept.

## Fixes

- Reduces terrain shimmer and flicker while the camera moves.
- Fixes water and distant coastline flicker in temporal AA modes.
- Keeps map icons clear and prevents planet flicker in the map and main menu.
- Repairs foliage motion that can leave trails in temporal AA modes.

## Known limitations

DLAA requires a supported NVIDIA RTX GPU. Unavailable modes are hidden. If a
mode fails to start, Better AA switches to Off; F10 shows the reason.

Most testing has been on Redux 0.2.9.0 with an RTX 5070 Ti. AMD hardware,
FSR 4, Redux 0.2.8.5 and long sessions need more testing.

F10's live A/B comparison can show terrain differently from normal play and
costs extra performance. Close it when comparing modes or checking flicker.

## Report a problem

With the problem visible, press **F10 → Issue ZIP**. Review the images and
attach the ZIP with reproduction steps to an
[issue](https://github.com/Michionlion/ReduxBetterAA/issues).

Reports are saved in `mods/ReduxBetterAA/diagnostics/reports`. Nothing is
uploaded automatically. Capturing a report can pause the game, and its images
may include vessel names and visible UI.

[Contributing](CONTRIBUTING.md) · [MIT license](LICENSE) ·
[Third-party notices](THIRD-PARTY-NOTICES.md)
