# Native runtime downloads

Install the main mod ZIP plus **one runtime ZIP matching your Redux version**.
Each runtime download includes all native components supported by that engine;
there is no GPU-vendor or feature selection when downloading.

| Redux | Unity | Download | Included |
| --- | --- | --- | --- |
| 0.2.9.0 | 6000.5.8f1 | `BetterAA-Runtimes-Redux-0.2.9.0.zip` | NVIDIA DLAA/DLSS, AMD FSR, NVIDIA and AMD frame generation |
| 0.2.8.5 | 6000.4.1f1 | `BetterAA-Runtimes-Redux-0.2.8.5.zip` | NVIDIA DLAA/DLSS and AMD FSR; FG requires the newer engine |

Download from [Releases](https://github.com/Michionlion/ReduxBetterAA/releases/tag/v0.6.2).
Close KSP2 and extract beside `KSP2_x64.exe`, preserving archive paths. Back up
any different existing files first. Updates from the earlier split downloads
use the same DLL paths; no settings changes or additional feature ZIPs are needed.
TAA and spatial AA still work without native downloads. Hardware/runtime probes
control which features are available; bundling a component does not establish
support on untested hardware.

Each combined archive preserves original vendor licenses and component manifests,
plus `BetterAA-Runtimes-manifest.json` with every payload hash and engine version.
The build instructions below create intermediate components for maintainers;
these are combined into the two downloads above before publication.

## Build combined downloads

Build the FSR and both-vendor FG components below, then run:

```powershell
python tools/package-all-runtimes.py --editors 'S:/Development/Unity/6000.5.8f1/Editor' 'C:/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor' --fsr-runtime 'G:/packages/BetterAA-FSR-Runtime-0.6.2-win-x64.zip' --frame-generation 'G:/packages/BetterAA-FrameGeneration-0.6.2-Redux-0.2.9.0-amd-nvidia-win-x64.zip' --mod-version 0.6.2 --output 'G:/packages/combined'
```

Packaging validates the pinned NVIDIA files, FSR manifest and both FG providers,
rejects overlapping component paths, and includes the FG Unity plugin only for
its pinned engine. It preserves payload bytes and refuses to overwrite archives.

## Frame generation companion

The local companion targets Redux **0.2.9.0 / Unity 6000.5.8f1**, Windows x64,
and the D3D11 player. It contains the ABI2 coordinator, selected ABI1 providers,
unmodified pinned vendor runtimes, original notices/licenses, and a hash manifest.
Its name is `BetterAA-FrameGeneration-<mod-version>-Redux-0.2.9.0-<vendors>-win-x64.zip`.
This component is included in the combined runtime download for Redux 0.2.9.0. The local build path
does not publish files or change the main mod package.

Close KSP2 and extract beside `KSP2_x64.exe`, preserving the archive paths and
any different existing files. The matching main mod must be installed separately.

- `KSP2_x64_Data/Plugins/x86_64/GfxPluginReduxBetterAAFrameGeneration.dll`
- `mods/ReduxBetterAA/native/frame-generation/ReduxBetterAA.StreamlineProvider.dll`
- `mods/ReduxBetterAA/native/frame-generation/ReduxBetterAA.AmdFrameGeneration.dll`
- NVIDIA's six FG/Reflex runtime DLLs in `native/frame-generation/streamline/` under the mod.
- AMD's `amd_fidelityfx_framegeneration_dx12.dll` in `native/frame-generation/amd/` under the mod.
- Installation notes, the hash manifest, and vendor/Unity/Better AA licenses alongside those directories.

Each provider is optional. The host asks Unity's native plugin mechanism to load
the coordinator, verifies its actual player-plugin path and initialization, then
queries the running backend. Merely placing a DLL in the directory is not proof
Unity loaded it. Native and package tests establish neither game image quality
nor support on untested hardware. FSR FG4 is selected only through the actual SDK;
the current native GPU validation exercised its FSR3.1.6 compatibility path.

Use local, external SDK/runtime inputs; the script downloads nothing. NVIDIA
headers require Streamline v2.14.1 commit
`2122257e0fce486f91b385aa63b9a09b0a34b363`, and its **full official production
release directory** supplies the six DLLs from `bin/x64` and the original license
files. AMD requires the FidelityFX-SDK 2.3.0 checkout described below, with its
frame-generation headers and signed FG DLL. Unity requires the exact editor's
`Editor/Data/PluginAPI` directory, including `LICENSE.md`.

```powershell
./tools/Build-Native.ps1 -FrameGeneration `
    -BuildDirectory 'G:/build/BetterAA-FG' `
    -UnityPluginApi 'S:/Development/Unity/6000.5.8f1/Editor/Data/PluginAPI' `
    -StreamlineSdk 'G:/dependencies/Streamline-2.14.1' `
    -StreamlineRuntime 'G:/dependencies/Streamline-runtime-2.14.1' `
    -FidelityFxSdk 'G:/dependencies/FidelityFX-SDK' `
    -RunTests -PackageDirectory 'G:/packages/BetterAA-FG'
```

Add `-FrameGenerationVendors nvidia` or `-FrameGenerationVendors amd` to build
only that provider and omit the other vendor's inputs. Omitting
`-PackageDirectory` builds native code without requiring redistributable runtime
files. In this **FG mode**, `-RunTests` runs CPU/Win32 contract and package tests;
it does not run GPU tests or require AMD hardware. The original FSR bridge mode
below retains its existing GPU-test behavior. Run GPU/player validation separately
in a coordinated window, following the native target READMEs.

The companion's verifier checks pinned headers, SDK commits, runtime and license
hashes, exact archive paths, Windows x64 DLL metadata, and required ABI exports.
Repeat packaging of identical inputs is deterministic; an existing archive is
never overwritten. To verify a downloaded/local companion without loading DLLs:

```powershell
python tools/package-frame-generation.py verify 'G:/packages/BetterAA-FG/<archive>.zip' --mod-version <mod-version>
```

The original vendor terms apply to their binaries. The bundle preserves
[NVIDIA's release notices](https://github.com/NVIDIA-RTX/Streamline/releases/tag/v2.14.1),
[AMD's pinned license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/docs/license.md),
and the [Unity Companion License](https://unity.com/legal/licenses/unity-companion-license).
Uninstall with KSP2 closed by removing exactly the companion manifest's files.

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
built separately as an input to the combined runtime downloads.
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

Close KSP2 and extract the ZIP beside `KSP2_x64.exe`. The combined archive includes
`NVUnityPlugin.dll`, `nvngx_dlss.dll`, AMD components and their notices.
Back up any different existing copies before replacing them. Install the
Better AA mod separately, then select NVIDIA DLAA or DLSS in its settings.
Download these companion ZIPs from the matching release; see release preparation in the source checkout's `CONTRIBUTING.md`.
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
