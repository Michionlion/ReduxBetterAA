# Phase 1 static renderer map

This note records inspection of the exact managed assemblies imported from the
target `0.2.8.5.103184-beta` player. It identifies what the probe should look
for; runtime observations remain authoritative.

## Flight renderer

`UniverseCameraManager` owns scaled-space and physics-space flight stacks, the
flight `RenderScalePresenter`, map camera, OAB camera manager, and AA state. The
scaled and physics stack implementations expose their main, debug, cubemap, and
ordered render cameras plus their `PostProcessLayer` and render-space identity.

`UniverseFlightCameraView` maintains both stacks and their order. A shared
jitter owner would eventually have to coordinate these contributors, but Phase
1 only records them.

## Map and VAB

`MapCamera` exposes its Unity camera and post-process layer.
`ObjectAssemblyCameraManager` exposes the corresponding VAB/OAB camera and
post-process layer. `PostProcessingSystem` tracks the active camera group,
including flight, map, and OAB categories.

## Render-scale presentation

The target `RenderScalePresenter` implementation stores a source-camera array,
one shared render target, a presentation camera, and a presentation command
buffer. When enabled for non-native scaling, its source cameras target the
shared texture and the presentation camera blits that texture to `CameraTarget`
at `CameraEvent.AfterEverything`. The presentation camera has no scene culling
and is ordered immediately after the last source camera.

At native scale, the implementation restores each source camera's prior target,
disables the presentation camera, and releases the shared target. Runtime
captures at both scale modes are required before treating either path as the
temporal integration point.

## Vendor modules

The player contains Unity's managed NVIDIA and AMD modules. The NVIDIA API
surface includes plugin load state, graphics-device access, a DLSS feature
query, and a DLAA quality enum. Static assembly presence is not capability
proof; the runtime report separately records native plugin load, device, and
feature status without constructing a feature.

No NVIDIA NGX/DLSS or AMD FSR native plugin binary was found in the target game
installation during Phase 1 inspection. The runtime probe is the authoritative
fallback test.
