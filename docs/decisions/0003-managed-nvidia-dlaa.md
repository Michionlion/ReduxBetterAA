# Decision 0003: managed Unity NVIDIA DLAA backend

## Status

Accepted for an experimental Phase 4 implementation. Runtime visual acceptance
is pending.

Motion-axis and exposure details are superseded by
[Decision 0007](0007-vendor-motion-direction-and-ppv2-exposure.md).

## Context

Phase 1 established a final scene-color hook before native UI and demonstrated
depth and motion-vector access. Phase 2 and Phase 3 established mutually
exclusive temporal ownership, synchronized camera jitter, explicit resets, and
fallback backends. The target player contains `UnityEngine.NVIDIAModule.dll`,
but the original installation did not contain Unity's native NVIDIA plugin.

The player module exposes Unity's managed `GraphicsDevice`, `DLSSContext`,
`DLSSCommandInitializationData`, `DLSSCommandExecutionData`, and
`DLSSTextureTable` API. The initialization, execution, and texture records are
value types and must be passed by reference.

## Decision

Add a fourth, mutually exclusive `NvidiaDlaa` backend using the managed Unity
NVIDIA API through a cached reflection boundary. The core assembly has no
static reference to the optional NVIDIA module. Reflection is performed during
initialization and context creation; cached dynamic delegates are used for the
per-frame command path.

The Phase 4 backend:

- requires NVIDIA hardware, D3D11 or D3D12, motion vectors, and at least 100%
  render scale; scales above 100% require an explicit supersampling opt-in;
- creates equal-sized input and output resources with the `DLAA` quality enum;
- owns one persistent context, command buffer, output texture, and camera hook;
- passes color, depth, motion vectors, jitter, pre-exposure, full subrect, and
  explicit reset state;
- replaces invalid motion and finite motion above 256 pixels/frame with zero
  before vendor execution;
- defaults to preset K and NVIDIA auto exposure because the selected PPv2 hook
  does not expose a stable HDRP-style pre-exposure scalar;
- uses Unity/HDRP conventions of negative pixel motion scale, negative jitter
  offset, inverted Y motion, low-resolution motion, and reversed-Z depth;
- disables PPv2 AA while active and restores all camera/layer state on exit;
- falls back first to Custom TAA, then PPv2 TAA, if probing, context creation,
  or execution fails.

No DLSS Super Resolution render-scale control and no custom native bridge are
introduced in Phase 4. The supersampling option is still an equal-input/output
DLAA resolve on Redux's already enlarged scene buffer, followed by Redux's
existing downsample.

## Local runtime exception

The maintainer approved copying Unity's signed Windows x64 player NVIDIA
binaries from the locally installed Unity 6000.4.1f1 editor into this one test
installation. They live beside `KSP2_x64.exe`, where a Unity Windows player
expects native player plugins. The normal mod zip does not claim redistribution
rights. A separate local-development bundle records the exact files and hashes.

The eventual distributable must obtain the correct native files from Redux's
licensed Unity player export/core packaging and review redistribution terms.
It must not reuse this workstation-specific bundle blindly.

## Consequences

DLAA remains selectable even when unsupported, but failure is visible in the
Ctrl+F10 panel and machine-readable report while the renderer continues through a
known temporal fallback. The implementation can be tested now without coupling
camera discovery or the coordinator to NVIDIA types. Runtime validation must
still prove texture conventions, UI exclusion, transition safety, and no
steady-state managed allocations.
