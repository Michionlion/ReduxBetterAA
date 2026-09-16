# Third-party notices

`VegetationMotionVectorRepair.shader` and the diagnostic
`Phase1MotionVectorPassProbe.shader` derive from Unity's Built-in motion-vector
shader. Preserve the Unity Technologies MIT notice in
`licenses/Unity-Built-in-Shaders.txt` with source and binary distributions.

The original Better AA code's license does not replace any third-party terms.
Redux, SpaceWarp2, ReduxLib, Harmony, PPv2, Newtonsoft.Json, Unity managed modules
and Addressables are supplied by the user's Redux installation and are not
redistributed by this package. Unity editor packages are build dependencies,
not player payload.

Complete release ZIPs contain unmodified Unity/NVIDIA player DLLs
and combined vendor notices from `licenses/Native-Runtimes.txt`.

Each complete release ZIP also contains the Better AA native
bridge and the unmodified `amd_fidelityfx_upscaler_dx12.dll` from
[AMD FSR SDK 2.3.0](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tree/60f4ea81909200d8542eca14dccb2628b763a9a3),
commit `60f4ea81909200d8542eca14dccb2628b763a9a3`. The bridge is covered by this
repository's MIT license. The AMD binary is governed by the SDK's separate
[binary license and component terms](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/docs/license.md),
including the components listed under MIT, and the original
[third-party notices](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/60f4ea81909200d8542eca14dccb2628b763a9a3/3rdpartynotice.md).
The complete original license and notices are embedded in
`mods/ReduxBetterAA/native/THIRD-PARTY-NOTICES-FSR.txt` inside the complete release ZIP.
`fsr-runtime-manifest.json` records the SDK revision and file hashes. The source checkout contains no AMD SDK binary.
