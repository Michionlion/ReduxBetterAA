# Optional native runtimes

## AMD FSR 4.1 / FSR 3.1

The new AMD bridge uses the original AMD FSR SDK 2.3.0 upscaler runtime at
commit `60f4ea81909200d8542eca14dccb2628b763a9a3`. It creates an actual FSR
context on the running GPU and queries its selected provider before setting the
menu label. The pinned implementations are FSR 4.1.1 and the FSR 3.1.5 fallback;
the menu shows **FSR 4.1** or **FSR 3.1** accordingly. If this runtime or its
required graphics features are unavailable, the AMD modes are hidden. The
legacy Unity FSR 2 runtime is not used.

This bridge requires Windows x64, Direct3D 11 shared-fence support, and a
same-adapter Direct3D 12 device at feature level 12.0. AMD's selected provider
determines ML support; Better AA does not infer it from the GPU name. Standalone
native-AA and upscaling tests pass with FSR 3.1.5 on the development RTX 5070 Ti.
Redux 0.2.9.0 D3D11 player checks also pass for native AA, upscaling,
window resize and map/reload transitions on that GPU. FSR 4.1 hardware
validation remains outstanding.
Frame generation is not provided by this runtime bridge.

The optional archive is `BetterAA-FSR-Runtime-<mod-version>-win-x64.zip` and is
built separately; no public download for this development package is published.
Close KSP2 and extract it beside `KSP2_x64.exe`. Its four files belong under
`mods/ReduxBetterAA/native/`:

- `ReduxBetterAA.FsrBridge.dll`
- `amd_fidelityfx_upscaler_dx12.dll`
- `THIRD-PARTY-NOTICES-FSR.txt`
- `fsr-runtime-manifest.json`

The AMD DLL is unmodified and verified against SHA256
`d0dcccc74a43c44ba435b7a369b456e0970d8a4464e4bd683119b374f2c9fb46`
before loading. The archive includes the complete original AMD binary license
and SDK third-party notices. These files are separate from Unity's player
runtime DLLs below and are not included in the main mod ZIP.

To build locally, use an external SDK checkout at the pinned revision, with its
API/upscaler headers, signed upscaler DLL, `docs/license.md`, and
`3rdpartynotice.md` present. With Visual Studio 2022 C++ tools, a Windows SDK,
and CMake 3.24+ installed, run from the Better AA checkout:

```powershell
./tools/Build-Native.ps1 -FidelityFxSdk 'G:/path/to/FidelityFX-SDK' `
    -BuildDirectory 'G:/path/to/native-build' -RunTests `
    -PackageDirectory 'G:/path/to/optional-packages'
```

SDK, build, and package directories must stay outside the source checkout.
`-RunTests` executes a standalone synthetic GPU test without starting KSP2.
See the bridge contract in the source checkout at `Native/README.md` and
[the pinned AMD license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/docs/license.md).

## NVIDIA DLAA and DLSS

Use the NVIDIA runtime ZIP matching your installed Redux version:

| Redux | Unity | Runtime ZIP |
| --- | --- | --- |
| 0.2.9.0 | 6000.5.8f1 | `BetterAA-Runtimes-Redux-0.2.9.0.zip` |
| 0.2.8.5 | 6000.4.1f1 | `BetterAA-Runtimes-Redux-0.2.8.5.zip` |

Close KSP2 and extract the ZIP beside `KSP2_x64.exe`. It contains only
`NVUnityPlugin.dll`, `nvngx_dlss.dll` and their notices.
Back up any different existing copies before replacing them. Install the
Better AA mod separately, then select NVIDIA DLAA or DLSS in its settings.
These revised companion ZIPs are built locally until a matching release is
published; see release preparation in the source checkout's `CONTRIBUTING.md`.
AMD FSR uses the separate modern runtime archive described above.

TAA, spatial AA and supersampling need no extra files. NVIDIA modes require
support from the installed runtime and running GPU.
If a mode stays hidden, check F10's capability report. Match the runtime to
Redux's Unity player when updating; do not mix DLL versions.

The DLLs are unmodified Windows player files from Unity
[6000.5.8f1](https://unity.com/releases/editor/whats-new/6000.5.8f1) and
[6000.4.1f1](https://unity.com/releases/editor/whats-new/6000.4.1f1).
Their Unity/NVIDIA terms are identified in the included notices;
Better AA's MIT license does not cover them.
