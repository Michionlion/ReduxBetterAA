# DLAA and FSR 2 native libraries

DLAA needs `NVUnityPlugin.dll` and `nvngx_dlss.dll`. FSR 2 needs
`AMDUnityPlugin.dll`. **Put them beside `KSP2_x64.exe`, not in the mod folder.**
TAA and spatial AA need none of these files. DLAA also needs supported RTX hardware.

## Download

These links download Windows support files directly from Unity:

| Installed Redux | Matching Unity | Download |
| --- | --- | --- |
| 0.2.9.0 | 6000.5.8f1 | [Windows support files — 382 MiB](https://download.unity3d.com/download_unity/5cb7df797b7d/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-6000.5.8f1.pkg) |
| 0.2.8.5 | 6000.4.1f1 | [Windows support files — 370 MiB](https://download.unity3d.com/download_unity/336a400b9ea2/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-6000.4.1f1.pkg) |

Check `UnityPlayer.dll` → Properties → Details if you are unsure of your player
version. Keep the plugin version matched to the player when Redux updates.

## Install

1. Close KSP2. Open the downloaded `.pkg` as an archive with [7-Zip](https://www.7-zip.org/).
2. Open `TargetSupport.pkg.tmp`, then open `Payload` as an archive. Inside it,
   open `Variations/win64_player_nondevelopment_mono`.
3. Copy the DLLs listed above beside `KSP2_x64.exe`. Back up any different
   existing copies before replacing them.
4. Start KSP2 and select DLAA or FSR 2 Native AA in **Settings → Mods → Redux Better AA**.

The `.pkg` contains Windows player files despite its Mac installer name; don't
run it as an installer. The archive is larger than the three files you need.
A matching Unity editor has the same DLLs under
`Editor/Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_mono`.

If a mode stays hidden, F10's capability report identifies missing runtimes or
unsupported hardware. Uninstalling Better AA does not require removing these
shared DLLs; only remove them if no other mod uses them.

## Source

The downloads come from Unity's release pages for
[6000.5.8f1](https://unity.com/releases/editor/whats-new/6000.5.8f1) and
[6000.4.1f1](https://unity.com/releases/editor/whats-new/6000.4.1f1).
Both sets were extracted and hash-checked against the working player/editor copies.
Unity's [software terms](https://unity.com/legal/editor-terms-of-service/software)
and applicable third-party terms, including the
[DLSS license](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt), apply.
Better AA's MIT license covers its own code; its release links to the original
Unity archives rather than republishing those binaries.
