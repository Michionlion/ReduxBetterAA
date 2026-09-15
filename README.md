# Redux Better AA

Better anti-aliasing and reconstruction for KSP2 Redux: custom TAA, NVIDIA
DLAA/DLSS, FSR native AA/upscaling, FXAA, SMAA and supersampling. Temporal modes smooth moving edges while keeping
the game's UI at native resolution.

**v0.6.2 beta** adds modern FSR, upscaling and optional frame generation.
Windows x64; base AA supports Redux 0.2.8.5 and 0.2.9.0, while upscaling and FG
require the inspected Redux 0.2.9.0 player. See the [implementation and limits](docs/architecture.md#upscaling-and-frame-generation-integration).
Most recent testing uses Redux 0.2.9.0, D3D11 and an RTX 5070 Ti at 1440p.

## Install

1. Install Redux, then close the game.
2. Download the mod ZIP from [Releases](https://github.com/Michionlion/ReduxBetterAA/releases)
   and extract it beside `KSP2_x64.exe`. You should have `mods/ReduxBetterAA/swinfo.json`.
3. From v0.6.2, new installs start with custom TAA (shown as **TAA**). Change modes in
   **Settings → Mods → Redux Better AA**. Updates preserve your saved selection, including Off.

DLAA/DLSS need matching Unity NVIDIA native libraries. FSR 4.1/3.1 uses a
separate optional FSR runtime package. Frame generation uses its own optional
native companion, with NVIDIA and AMD components selected when building it.
[Native packages and installation](NATIVES.md).
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
AA/upscaling vendor modes are hidden in settings, F10 and hotkey cycling; their
runtime failures fall back to Off with a reason in diagnostics.
FSR 3.1 is the compatibility provider. There is no separate FSR2 backend;
older saved FSR selections migrate to the supported modern provider.
Map AA and foliage motion repair have separate settings. Physics is unchanged.
**Frame generation** is one independent setting for provider and multiplier.
It defaults to **Off**. With the optional runtime installed, completed native
capability probes determine the available **Auto**, **DLSS 2x–4x** and
**FSR 4/3.1 2x** choices. An unsupported DLSS multiplier falls back to a lower
supported multiplier, then FSR 2x; FSR 4 falls back to FSR 3.1. The saved request
and selected AA/upscaler are preserved. TAA, DLAA, FSR Native AA and upscaling
publish their resolved scene through the same FG input path.
See the [selection and presentation contract](docs/architecture.md#independent-frame-generation-contract)
and the validation limits below before using this beta feature.
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

This release also repairs missing planet motion in procedural terrain
vectors during ascent. It matches the visible scene against terrain depth,
preserves foreground vessel motion and supplies the correction to temporal AA
and FG without another setting. See the [rendering fixes](docs/architecture.md#game-rendering-fixes).

## Known limits and reports

- This is a beta. Non-NVIDIA hardware, long sessions and the full range of flight/VAB scenes need more testing.
- The published v0.6.1 uses zero map jitter. v0.6.2 restores coherent map jitter and draws map icons after AA.
- Supersampling falls back to Off in the map and main menu.
- F10's live A/B comparison renders the scene twice. Its terrain-depth behavior differs from normal mode selection; stop it before judging terrain flicker or measuring performance.
- Upscaling is gated to the inspected Redux 0.2.9.0.104521 D3D11 camera graph. Menu/map/VAB use the selected provider's native AA; the active late close-vessel camera is not yet supported by the SR path.
- FSR 3.1 and DLSS have passed a limited RTX 5070 Ti player smoke. FSR 4.1 requires separate AMD hardware validation. This does not establish moving quality or a performance gain.
- Limited RTX 5070 Ti player checks exercised the temporal AA/upscaling producers, DLSS 2x–4x and FSR 3.1 FG 2x, including settings, provider switches and Off/re-enable. Captured inputs have consistent color/depth/motion, and source/SDK/native Present counters confirm extra vendor presentations with one original Present per owned real frame. See the [tested scope](docs/architecture.md#validation-requirements); displayed cadence, latency and broad moving-image quality are not established. AMD FG 4 and older RTX hardware remain untested.
- FG currently requires the inspected Redux/Unity D3D11 player, windowed/borderless RGBA8 SDR output, actual Unity HDR disabled and an eligible flight/KSC camera. Window client and native backbuffer dimensions must match; a windowed resize can suspend FG while Redux retains a differently sized backbuffer. Matching geometry can re-enable it. An HDR desktop alone does not disqualify an SDR game surface. Map/menu/VAB and active close-vessel rendering suspend FG. HUD-less scene plus final UI color provides basic vendor UI handling; exact alpha extraction and every overlay are not guaranteed. Native close-vessel composition needs complete scene depth/motion before SR or FG. There is no Ray Reconstruction.
- Heavy GPU load can drop an FG capture. Brief delays retain the last completed image; after 250 ms without a new one, FG returns to ordinary rendering and stays paused. Switch **Frame generation** Off, then select it again to retry. The requested multiplier is not a frame-rate guarantee. Broad displayed-cadence, input-latency, minimize/alt-tab and device-loss validation remain incomplete.

For a rendering bug, keep it visible and use **F10 → Issue ZIP**. Review the
images, then attach the ZIP and reproduction steps to an
[issue](https://github.com/Michionlion/ReduxBetterAA/issues).
Reports stay in `mods/ReduxBetterAA/diagnostics/reports`; nothing is uploaded automatically.
Capture can pause the game, and screenshots can contain visible vessel names/UI.

[Build and contribute](CONTRIBUTING.md) · [Architecture and fixes](docs/architecture.md) ·
[MIT license](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)
