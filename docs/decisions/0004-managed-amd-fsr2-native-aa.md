# Decision 0004: managed Unity AMD FSR2 Native AA experiment

## Status

Accepted for an opt-in native-resolution experiment by explicit maintainer
request. Runtime visual acceptance is pending.

Motion-axis and exposure details are superseded by
[Decision 0007](0007-vendor-motion-direction-and-ppv2-exposure.md).

## Context

The Phase 1 through Phase 4 work established a final scene-color hook before
native UI, coherent depth and normally coherent motion vectors, one temporal
owner, shared jitter, reset propagation, and safe temporal fallbacks. Unity
6000.4.1f1 and the target player contain `UnityEngine.AMDModule.dll`. Unity's
module exposes FSR2 context creation and execution for custom integrations, but
the target game does not contain `AMDUnityPlugin.dll` and does not use Unity's
render-pipeline upscaler framework.

The maintainer requested FSR and XeSS modes. XeSS is deferred: it is not a
built-in Unity player module and would require a separate Intel package and
native plugin set. FSR2 can reuse the exact-version Unity module already
represented in the player.

## Decision

Add a fifth mutually exclusive backend, `AmdFsr2`, using a cached reflection
boundary around `UnityEngine.AMDModule`. Despite the module name, the backend
does not impose a GPU-vendor check; runtime support is determined by the Unity
plugin and graphics device.

The first implementation is deliberately **FSR2 Native AA**, not an upscaler:

- input and output dimensions are equal and Redux render scale must be 100%;
- the existing final pre-UI scene hook, depth, motion, jitter, and reset state
  are reused;
- invalid motion and finite motion above 256 pixels/frame are replaced with
  zero before FSR2 execution;
- the output is a project-owned linear random-write texture;
- Unity's documented current-to-previous motion convention starts with positive
  width/height scale, with diagnostic X/Y inversion controls available;
- sharpening, exposure, and jitter controls live in the Ctrl+F10 FSR2 page, with
  important user options mirrored in Redux settings;
- FSR2 auto exposure is the default because the selected PPv2 integration
  point does not expose a reliable pre-exposure scalar;
- a failed probe, context creation, or execution falls back to Custom TAA and
  then PPv2 TAA;
- no renderer presenter or internal render scale is changed.

Reflection and boxing occur only during module binding and context creation.
Cached dynamic delegates update command structures and invoke FSR2 each frame.
All native contexts, command buffers, output textures, hooks, subscriptions,
and renderer state have deterministic idempotent cleanup.

## Local runtime exception

The maintainer previously approved workstation-local Unity player runtimes for
this test installation. The local package may therefore add the signed
`AMDUnityPlugin.dll` from Unity 6000.4.1f1's
`win64_player_nondevelopment_mono` export next to `KSP2_x64.exe`. The normal mod
zip excludes it. Redux core must supply and license the matching runtime in a
future distributable.

## Consequences

This experiment answers native-resolution quality and integration questions
without prematurely modifying Redux's presentation path. It is not evidence
that lower-resolution FSR2 reconstruction is correct or ready. Phase 5 render
scale ownership, LOD bias, input sizing, and presentation changes remain a
separate upstream-oriented task.

Primary Unity API reference:
<https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AMD.AMDUnityPlugin.html>.
