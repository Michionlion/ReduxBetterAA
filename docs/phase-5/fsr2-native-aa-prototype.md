# FSR2 Native AA prototype

Version 0.5.0 adds a fifth opt-in comparison mode:

```text
Off -> PPv2 TAA -> Custom TAA -> NVIDIA DLAA -> FSR2 Native AA -> Off
```

## Scope

This is a native-resolution temporal AA experiment through Unity 6000.4.1f1's
managed AMD module. It is not FSR2 Super Resolution: render and display size
remain equal, the Redux presenter is untouched, and render scale must be 100%.
The resolve stays on the measured final scene camera before UI composition.

The Ctrl+F10 FSR2 page exposes jitter spread and sequence length, optional RCAS
sharpening and strength, PPv2-preferred automatic exposure with vendor fallback,
manual pre-exposure, and explicit sanitizer motion-vector X/Y inversion. Unity's
built-in buffer is previous-to-current, so both components default to negated;
the managed API then uses positive width/height pixel scales.

## Lifecycle and fallback

`AmdFsr2Api` binds `UnityEngine.AMDModule` by reflection once and creates cached
dynamic delegates for the frame path. `AmdFsr2Backend` owns one camera hook,
command buffer, native FSR2 context, and linear random-write output. It releases
them on backend switch, output change, scene invalidation, runtime failure, and
shutdown. Initialization flags include display-resolution motion vectors, HDR
when applicable, reversed Z, and optional auto exposure.

Every execution first copies current color into the output, then records the
native FSR2 call. If the call is accepted but fails to write, the frame remains
visible. Exceptions or invalid depth/motion dimensions latch one failure and
switch to Custom TAA, then PPv2 if necessary.

FSR2 receives the same moving solid-depth-edge bias mask as DLAA. The mask
reduces stale history at moving vessel/terrain silhouettes without broadly
marking no-depth transparent or volumetric regions, preserving useful temporal
detail accumulation there.

## Workstation-local runtime

The normal mod zip excludes vendor binaries. This test machine's local bundle
uses the following exact Unity player runtime beside `KSP2_x64.exe`:

| File | Bytes | SHA-256 | Signature |
|---|---:|---|---|
| `AMDUnityPlugin.dll` | 8,831,920 | `E070D3BBC31B29246CB4E27378FBE76CB332D5499CF7F7F5B3F85D2131BC381E` | Valid, Unity Technologies SF |

Source: Unity 6000.4.1f1,
`PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_mono`.

## Runtime verification

At 100% render scale:

1. Open Ctrl+F10 and select FSR2. Require `Context active`, equal input/output
   dimensions, and a UAV output. If a fallback appears, capture the panel and
   write a report before changing settings.
2. Capture moving thin vessel geometry with Off, Custom, DLAA, and FSR2. Repeat
   near the launchpad and above the altitude where the known motion corruption
   disappears.
3. Pan across a planet limb and terrain/depth discontinuities. Check shimmer,
   disocclusion trails, and whether stationary detail settles.
4. Use Buffers -> Motion Sign Agreement while panning one axis at a time over
   static terrain. Green on both halves with both inversions enabled is expected;
   record any repeatable contradiction before overriding the default.
5. Test flight/map transitions, VAB, quickload/revert, resolution changes, and
   repeated backend switching. Require visible scene color, crisp native UI,
   and no recurring exception.
6. Record GPU/CPU frame time, output memory, and steady-state managed allocation
   after warm-up before making a performance or quality claim.

The backend remains experimental until these in-player checks pass. XeSS is
intentionally absent from 0.5.0.
