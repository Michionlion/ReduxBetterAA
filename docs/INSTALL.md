# Install Redux Better AA

Install KSP2 Redux first, then close the game and extract the complete ZIP matching your Redux version into the
folder containing `KSP2_x64.exe`. The installed manifest must be at
`mods/ReduxBetterAA/swinfo.json`.

Open **Settings → Mods → Redux Better AA** and choose a mode. Fresh installs
start with custom TAA (shown as TAA); updates preserve saved selections, including Off.
The ZIP includes the native libraries for DLAA and FSR Native AA. No separate
runtime download is required. See the included `NATIVES.md` for engine matching.

To update, move the previous mod folder outside `mods`, extract the new ZIP,
then restore only your configuration and diagnostic reports. Do not keep a
second copy of the same mod under `mods`, even in a backup subfolder.
To uninstall, remove `mods/ReduxBetterAA`. Root-level NVIDIA DLLs can be shared
with other mods; restore any originals you backed up rather than deleting shared files.
When updating from the withdrawn experimental v0.6.2, do not copy its `native`
folder into the new mod folder. Its FG libraries and settings are not used here.

For a rendering problem, reproduce it and use **F10 → Issue ZIP**. Review the
images before sharing the ZIP; visible UI and vessel names can appear.
Reports are saved under `mods/ReduxBetterAA/diagnostics/reports`.

[Releases, documentation and issues](https://github.com/Michionlion/ReduxBetterAA).
