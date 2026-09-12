# Rendering contracts

Better AA provides native-resolution TAA, NVIDIA DLAA, FSR 2 Native AA, FXAA,
SMAA and supersampling. It does not alter physics or implement DLSS upscaling,
frame generation or ray reconstruction. Unsupported or failed modes select Off.

## Ownership and frame inputs

TemporalCoordinator owns backend selection, camera discovery, resets and
scene transitions. Backends own their resources and resolve lifecycle. Rendering
code must use matching color, depth, motion vectors, projection matrices and
jitter for the same frame. Apply a shared jitter sample exactly once across
participating cameras; do not let individual cameras advance it independently.

Scene state is captured before acquisition and restored only while still owned.
Off releases AA resources and selects native unfiltered scene rendering.
Shutdown, partial initialization failure and repeated cleanup must be safe.
Lost GPU storage invalidates history even when texture dimensions still match.
Reset on camera cuts, quickloads, vessel/scene changes and output changes.

Resolve before UI composition. Map icons are world-space SpriteRenderers:
MapIconOverlay suppresses their normal draw and replays visible icons after AA
using unjittered matrices, original materials and camera depth. Restore only
sprites claimed by the overlay, including on disable and scene teardown.

## Projection alignment

Flight uses the discovered scaled/physics camera graph. Recognized menu and
map cameras use matching opaque and transparent jitter. Changing only the
opaque projection produces flickering planet patches. Unknown camera graphs
must not inherit support merely because a camera has a familiar name.

Terrain's immediate DrawPqsDepthNow pass runs before ordinary camera rendering.
TerrainDepthJitterPatch applies the upcoming raster projection for that draw
and restores it afterward, including exceptions. Do not advance jitter, add a
depth pass or change temporal quality settings to do this.

Foliage motion repair excludes invalid object history for indirect vegetation.
Motion sanitization and vendor input preparation retain their coordinate,
depth and exposure conventions. Changes need moving-scene validation.

## Resource and performance requirements

No steady-state managed allocations, repeated reflection, scene searches or
resource creation in frame callbacks. Every texture, material, command buffer,
subscription and native context has an explicit owner and release path.
Recreate resources when their immutable output properties change. Validate
vendor support and input dimensions before execution; log failure once and
fall back safely. Preserve native-resolution UI in every mode.

## Build and diagnostics

Use Unity's player-script compiler and pinned Addressables package. Addressables must have mod-specific
internal bundle identities (GroupGuidProjectIdEntriesHash) so another mod
cannot collide with Better AA's shader bundle. Package only the mod DLL,
manifest, shader bundle/catalog and installation/license files.

F10 diagnostics and issue ZIPs are supported product features. Capture failure
must leave normal rendering operational. Reports identify the actual capture
stage and are never uploaded automatically. F10 live comparison renders twice;
use ordinary mode selection for terrain and performance validation.

See [the release checklist](tests/README.md) for required regression coverage.
Historical investigations and the original Git history are kept in a separate
maintainer archive.
