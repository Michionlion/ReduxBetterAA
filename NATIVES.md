# DLAA and FSR 2 runtimes

Download the ZIP matching your installed Redux version:

| Redux | Unity | Runtime ZIP |
| --- | --- | --- |
| 0.2.9.0 | 6000.5.8f1 | [Download (29 MiB)](https://github.com/Michionlion/ReduxBetterAA/releases/download/v0.6.1/BetterAA-Runtimes-Redux-0.2.9.0.zip) |
| 0.2.8.5 | 6000.4.1f1 | [Download (29 MiB)](https://github.com/Michionlion/ReduxBetterAA/releases/download/v0.6.1/BetterAA-Runtimes-Redux-0.2.8.5.zip) |

Close KSP2 and extract the ZIP beside `KSP2_x64.exe`. It contains only
`AMDUnityPlugin.dll`, `NVUnityPlugin.dll`, `nvngx_dlss.dll` and their notices.
Back up any different existing copies before replacing them. Install the
Better AA mod separately, then select DLAA or FSR 2 in its settings.

TAA and spatial AA need no extra files. DLAA requires supported RTX hardware.
If a mode stays hidden, check F10's capability report. Match the runtime to
Redux's Unity player when updating; do not mix DLL versions.

The DLLs are unmodified Windows player files from Unity
[6000.5.8f1](https://unity.com/releases/editor/whats-new/6000.5.8f1) and
[6000.4.1f1](https://unity.com/releases/editor/whats-new/6000.4.1f1).
Their Unity/NVIDIA/AMD terms are identified in the included notices;
Better AA's MIT license does not cover them.
