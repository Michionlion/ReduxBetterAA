# Included DLAA and FSR libraries

Download one ZIP matching your Redux version from
[Releases](https://github.com/Michionlion/ReduxBetterAA/releases).
It contains Better AA and the libraries for both DLAA and FSR Native AA.

| Redux | Download |
| --- | --- |
| 0.2.9.0 | `ReduxBetterAA-0.6.2-Redux-0.2.9.0.zip` |
| 0.2.8.5 | `ReduxBetterAA-0.6.2-Redux-0.2.8.5.zip` |

Close the game and extract beside `KSP2_x64.exe`. Back up any different
existing DLLs before replacing them. Use the ZIP for your Redux version;
the NVIDIA libraries differ between versions.

`NVUnityPlugin.dll` and `nvngx_dlss.dll` go beside `KSP2_x64.exe`. The AMD
libraries go in `mods/ReduxBetterAA/native`. The older `AMDUnityPlugin.dll`
is no longer needed by Better AA.

DLAA requires a supported RTX GPU. The FSR mode label shows the version
available on your system. FSR 4 and AMD hardware still need more testing.

The NVIDIA and AMD libraries retain their original licenses, included in the
ZIP. See [third-party notices](THIRD-PARTY-NOTICES.md).
