# Install Redux Better AA

Install KSP2 Redux first, then close the game and extract the mod ZIP into the
folder containing `KSP2_x64.exe`. The installed manifest must be at
`mods/ReduxBetterAA/swinfo.json`.

Open **Settings → Mods → Redux Better AA** and choose a mode. Fresh installs
start with custom TAA (shown as TAA); updates preserve saved selections, including Off.
TAA, FXAA, SMAA and supersampling need no extra native files.
See the included `NATIVES.md` for NVIDIA DLAA/DLSS libraries
and the separate FSR 4.1/3.1 runtime. Settings show the actual
supported FSR provider; unsupported NVIDIA modes are hidden. Existing FSR
selections migrate to the current provider's name.
FSR 3.1 handles older supported GPUs. The Unity FSR2 plugin is no longer used;
if the modern FSR runtime or its graphics bridge is unavailable, AMD choices
are hidden and portable TAA remains available.

This branch's upscaling is experimental and gated to Redux 0.2.9.0.104521,
Unity 6000.5.8f1 and D3D11. Native AA is used in scenes without an eligible
upscaling graph.

Frame generation uses a separate optional companion described in `NATIVES.md`.
Its single **Frame generation** setting is independent of the AA mode and defaults
to **Off**; choices appear after the installed providers confirm GPU support.
FG currently requires windowed/borderless SDR output and an eligible flight/KSC
camera. Menus, map, VAB and active close-vessel rendering suspend FG. Native
close-vessel composition remains unavailable; installing the companion does not
enable it or guarantee a requested frame-rate multiplier.

To update, move the previous mod folder outside `mods`, extract the new ZIP,
then restore your configuration and diagnostic reports and reinstall the matching
optional native packages you use. The main ZIP does not include those runtimes.
Do not keep a second copy of the same mod under `mods`, even in a backup subfolder.
To uninstall, first remove the FG companion's manifest-listed files as described
in `NATIVES.md`, if installed, then remove `mods/ReduxBetterAA`.

For a rendering problem, reproduce it and use **F10 → Issue ZIP**. Review the
images before sharing the ZIP; visible UI and vessel names can appear.
Reports are saved under `mods/ReduxBetterAA/diagnostics/reports`.

[Releases, documentation and issues](https://github.com/Michionlion/ReduxBetterAA).
