# AMD native AA bridge

The bridge runs native-resolution AMD FSR through the pinned SDK. Unity submits
D3D11 color/depth/motion to same-adapter D3D12 interop; owned contexts and shared
textures retire after GPU work completes. The managed adapter preserves exposure,
depth direction, motion signs and reset state. No frame presentation is replaced.

The official SDK checkout and build output must stay outside this repository:

```powershell
./tools/Build-Native.ps1 -FidelityFxSdk 'D:\SDKs\FidelityFX-SDK' `
    -BuildDirectory 'D:\Builds\BetterAA-FSR' -RunTests `
    -PackageDirectory 'D:\Builds\BetterAA-native-input'
```

Use SDK commit `60f4ea81909200d8542eca14dccb2628b763a9a3`, Visual Studio 2022
C++ tools and CMake 3.24+. The script verifies pinned vendor bytes, builds the
bridge, optionally runs synthetic GPU tests, and packages original AMD notices.
Pass the resulting component archive to `tools/Release.ps1 -FsrRuntimeZip`.
Public releases combine it with the mod and engine-specific NVIDIA DLLs.

The low-level SDK accepts reconstruction dimensions, but the public managed
backend requires equal input/output resolution and 100% render scale. Upscaling
and frame-generation integration remain on
[the investigation branch](https://github.com/Michionlion/ReduxBetterAA/tree/investigate/upscaling-frame-generation).
