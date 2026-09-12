# Release contents

`ReduxBetterAA-<version>.zip` extracts entirely into `mods/ReduxBetterAA`:

- `ReduxBetterAA.dll` and `swinfo.json`.
- One Windows shader bundle, its Addressables catalog and catalog hash.
- Installation/native instructions, MIT license and Unity shader attribution.
- A file manifest with SHA-256 hashes, source commit and SDK archive hash.

The public packager drops `addressables/settings.json` and
`addressables/AddressablesLink/link.xml`. They configure the editor/player build;
SpaceWarp2 loads the mod's `catalog.json` directly. The release keeps the nine
runtime shaders, including the small diagnostic shaders used by F10 and issue
reports. Test adapters, baseline shaders, debug symbols, source, saved settings,
captures, game assemblies and vendor DLLs are excluded by an explicit allowlist.

Native libraries are separate [direct Unity downloads](../NATIVES.md).
Users extract them and copy the required DLLs beside the game executable.

Original code is MIT. Unity-derived motion-vector shaders retain their MIT
notice. Runtime dependencies supplied by Redux are not bundled. See
[third-party notices](../THIRD-PARTY-NOTICES.md).

The old full/local-only runtime ZIPs were workstation experiments and are not
public release inputs. Their receipt-based packaging path has been removed.
The obsolete phase-1 bootstrap compiler has also been removed; releases use the
complete pinned Unity/SDK build. No build step copies files from the game root
into a public archive.

[Build and publish](building.md) · [Install/update](INSTALL.md)
