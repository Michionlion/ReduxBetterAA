# Phase 4 managed NVIDIA DLAA prototype

## Scope and status

Phase 4 was started by explicit maintainer request. It adds DLAA without
replacing the Phase 2 PPv2 or Phase 3 Custom TAA comparison paths. Current
public selection is:

```text
Off -> FXAA Low -> FXAA High -> SMAA -> TAA -> NVIDIA DLAA -> FSR2 Native AA -> Off
```

The backend is experimental and disabled by default. It implements native
resolution anti-aliasing only; it does not change Redux render scale and does
not implement DLSS Super Resolution, Frame Generation, or Ray Reconstruction.

## Managed API integration

The core assembly does not reference `UnityEngine.NVIDIAModule` statically.
`NvidiaDlaaApi` resolves the managed surface at startup, validates device API
version 6, loads Unity's native NVIDIA plugin, queries the DLSS feature, and
caches allocation-free delegates for the render path. Missing assemblies,
native files, unsupported GPUs/drivers, and API mismatches are reported as
capability failures rather than loader errors.

One context is created for each immutable output descriptor. Initialization
uses:

- `DLSSQuality.DLAA`;
- equal input and output width/height;
- HDR when the scene format is HDR;
- low-resolution motion vectors (equal to the native color dimensions in
  DLAA mode);
- reversed-Z depth when Unity reports it;
- configurable auto exposure and DLAA preset hints.

Each frame supplies final scene color, `_CameraDepthTexture`,
`_CameraMotionVectorsTexture`, a project-generated moving solid-depth-edge bias
mask, the project-owned output, full input subrect, pre-exposure, sharpening,
and an explicit reset bit. Broad no-depth regions are excluded from that mask
so transparent and volumetric effects retain useful temporal detail
accumulation. After the shared sanitizer
transforms Unity's previous-to-current motion into vendor current-to-previous
motion, the managed call uses:

```text
sanitizer motionSign = (-1, -1)
mvScale = (inputWidth, inputHeight)
jitterOffset = -cameraJitterPixels
indicator invertXAxis = false
indicator invertYAxis = SystemInfo.graphicsUVStartsAtTop
```

The X/Y controls now negate texture components in the sanitizer. Unity's
similarly named NVIDIA fields only orient NGX's optional on-screen status
indicator and never serve as motion controls. Both sanitizer inversions default
on, as required by Unity 6000.4.1f1's source convention.

## Ctrl+F10 controls

The Ctrl+F10 panel has one Off/spatial/PPv2/Custom/DLAA/FSR2/Buffers toolbar. Choosing an
AA mode both activates it and opens that mode's settings; choosing Buffers opens
diagnostics without changing the active mode. The DLAA page exposes:

- jitter spread and sequence length;
- DLAA sharpness on the shared `0-1` user scale;
- automatic exposure with PPv2 preference, vendor fallback, and manual override;
- explicit sanitizer motion-vector X/Y inversion;
- F, J, K, L, and M DLAA preset hints, with K as the default;
- conservative preset and manual history reset;
- managed API, context, dimensions, fallback, and output-memory status.

Changing exposure source, preset, or supersampling policy recreates the native
context. Temporal-input changes reset history once. Sharpness is output-only and
applies without discarding accumulated history.

## Resource and fallback lifecycle

The backend owns one command buffer, one scene-format output texture, one
managed DLSS context, one final-camera render hook, and its camera event
subscriptions. Context and output are recreated on output descriptor changes
and released on backend switch, scene invalidation, or shutdown. Cleanup is
idempotent.

DLAA normally requires render scale 100%. An explicit engineering toggle permits
equal-size DLAA on Redux's supersampled scene buffer before Redux downsamples it.
An unsupported selection falls directly back to Off.
Any execution exception or missing/mismatched depth/motion texture passes the
current frame through, latches the failure once, releases DLAA, and activates a
no-AA fallback on the next coordinator tick.

The DLAA output is a linear random-write resource, matching Unity 6000.4.1f1's
own DLSS integration. The execution command buffer first copies scene color to
the output, then records DLAA. This fail-open prefill prevents a silently
rejected native write from replacing the scene with black. See
[`dlaa-blank-output-diagnosis.md`](dlaa-blank-output-diagnosis.md).

## Workstation-local runtime bundle

For the approved test installation only, the native files come from:

```text
C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Data\PlaybackEngines\
windowsstandalonesupport\Variations\win64_player_nondevelopment_mono
```

They are copied beside `KSP2_x64.exe` and also placed in a separate local test
bundle generated under `Deploy/`; they are not part of the normal distributable
mod zip.

| File | Bytes | SHA-256 | Signature |
| --- | ---: | --- | --- |
| `NVUnityPlugin.dll` | 1,490,856 | `B1F82C5E16AF6F886DBF909D1BDD9C064C253078EBF5A55DAC9E89BF81C2B946` | Unity Technologies SF, valid |
| `nvngx_dlss.dll` | 54,779,504 | `8707E53B26C68C606B98BF31C223485FF30D310A261B1A36D48B2EAABC1507EC` | NVIDIA Corporation, valid |

The game player and editor both identify as Unity 6000.4.1f1, although their
build revisions differ. The managed device-version check and safe fallback
remain mandatory. The distributable Redux integration must source these files
from its own matching licensed player export and document redistribution.

## Runtime acceptance checklist

Record the game/Redux/mod version, GPU/driver, D3D API, resolution, render scale,
scene, and preset for every comparison. At minimum verify:

1. Select DLAA at 100% render scale and confirm the panel reports an active
   context with equal input/output dimensions.
2. Capture Off, PPv2, Custom, and DLAA using the same moving camera path at the
   launchpad, low terrain flight, orbit, map, and VAB.
3. Check thin vessel geometry, planet limbs, volumetric effects, exhaust, transparent
   effects, and stopped-camera behavior for shimmer, trails, or flashes.
4. Open Buffers -> Motion: Raw Sign Agreement and pan one axis at a time over
   static terrain. Require green on the matching half with both inversions
   enabled. Dark blue is ambiguous near an axis zero-crossing; document only a
   coherent red result during the deliberate pan before questioning the
   source-defined default.
5. Exercise flight/map/VAB transitions, quickload/revert, vessel switch,
   resolution change, backend cycling, and game shutdown.
6. Set render scale away from 100% and confirm DLAA refuses initialization when
   supersampling is disabled, then verify the opt-in path uses equal input/output
   dimensions on Redux's larger scene buffer.
7. Remove or rename the local native runtime in a disposable test and confirm
   the mod still loads with a logged fallback.
8. Profile GPU time, backend pass time, history recreation spikes, and managed
   allocations after warm-up.

Phase 4 is not accepted until these in-player checks pass. DLAA consumes the
project sanitizer output: a 16-anchor GPU classifier uses the validated 96-pixel
camera-disagreement envelope to replace a screen-wide corrupt field in the same
frame. Unverified >256 px raw samples or >96 px camera disagreement use a
<=256 px camera fallback when available and otherwise become zero before NVIDIA
execution. Continuous high-altitude camera translation can trigger at most one
transform-derived `Teleport` reset until movement settles; BetterAA never resets
or mutates a cloud renderer's independent temporal history.
