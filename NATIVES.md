# Native libraries included in the release

Download **one complete ZIP matching your Redux version** from
[Releases](https://github.com/Michionlion/ReduxBetterAA/releases).
Extract beside `KSP2_x64.exe` with the game closed. The mod and all required
native AA libraries are included; no feature-specific download is needed.

| Redux | Unity | Complete download |
| --- | --- | --- |
| 0.2.9.0 | 6000.5.8f1 | `ReduxBetterAA-0.6.3-Redux-0.2.9.0.zip` |
| 0.2.8.5 | 6000.4.1f1 | `ReduxBetterAA-0.6.3-Redux-0.2.8.5.zip` |

Do not mix engine DLLs. Back up different existing copies before replacing them.
`NVUnityPlugin.dll` and `nvngx_dlss.dll` install at the game root. The AMD FSR
bridge and `amd_fidelityfx_upscaler_dx12.dll` install under `mods/ReduxBetterAA/native`.
The old Unity `AMDUnityPlugin.dll` is not needed by this version of Better AA.
DLAA still needs supported RTX hardware. AMD native AA reports the provider the
SDK actually selected; FSR 4 hardware validation remains outstanding.

`BetterAA-release-manifest.json` identifies the Redux/Unity pairing and hashes
all payload files. The nested mod manifest describes the managed component;
`native/fsr-runtime-manifest.json` identifies the pinned AMD SDK and bridge ABI.
The complete ZIP validator checks the combined payload and both component contracts.

NVIDIA DLLs are unmodified Windows player files from the pinned Unity editors;
`BetterAA-Runtime-Notices.txt` carries their original terms. The AMD binary is
unmodified from FSR SDK 2.3.0, commit `60f4ea81909200d8542eca14dccb2628b763a9a3`.
Its complete original terms and notices accompany the native libraries in
`THIRD-PARTY-NOTICES-FSR.txt`. Better AA's MIT license covers its own bridge,
not the vendor binaries. See [third-party notices](THIRD-PARTY-NOTICES.md).

Maintainers build the AMD bridge with [Build-Native.ps1](tools/Build-Native.ps1)
and pass its internal runtime archive to [Release.ps1](tools/Release.ps1).
Internal component ZIPs are build inputs and are not public release downloads.
See [the release workflow](CONTRIBUTING.md#prepare-and-publish-a-release).
