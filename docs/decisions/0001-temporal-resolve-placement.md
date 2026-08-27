# Decision 0001: Unified final-scene temporal resolve with invalid-motion guardrail

- Status: Accepted for Phase 1 exit; constrained for Phase 2 evaluation
- Date: 2026-08-23
- Phase: 1 exit / Phase 2 entry
- Scope: SPEC Sections 6.2, 6.3, 7.1, 9.2, and 11.1

## Context

Flight composes scaled space first and physics space last. At non-native render
scale both source cameras write the `RenderScalePresenter` shared target, which
is presented later at `CameraEvent.AfterEverything`. At native scale the scene
cameras write the camera target directly. Map and VAB each expose one intended
main camera and `PostProcessLayer`. Runtime captures show native UI over the
diagnostic scene view but not in the selected scene final color.

The flight physics-main diagnostic has coherent combined depth coverage and is
the last scene post-process point before UI. Most motion captures are coherent:
moving orbit p99 values are approximately 1 pixel per frame and stationary
orbit/ascent values are approximately zero. Near the launchpad, however,
intermittent frames contain finite but physically impossible motion across both
depth-covered and no-depth regions. Recorded p99 values reach approximately
1,300-1,780 pixels per frame and maxima reach 1,451-2,031 pixels. The issue is
local to the immediate pad region in the available evidence and disappears by
roughly 687 m.

The runtime capability report for the tested NVIDIA RTX 5070 Ti on D3D11 shows
Unity's managed NVIDIA API but reports that the native Unity NVIDIA plugin did
not load. DLSS/DLAA feature availability is therefore false.

## Decision

Choose Branch B: run exactly one temporal resolve on the final scene camera's
PPv2 post-process layer.

- Flight resolves on `FlightCameraPhysics_Main`, after scaled/physics scene
  composition and before native UI. The scaled main contributor receives the
  same PPv2 Halton jitter sample but does not run a separate temporal resolve.
- Map resolves on the active `MapCamera` main camera.
- VAB resolves on the active `ObjectAssemblyCameraManager.Camera`.
- At non-native render scale, temporal resolution occurs before the presenter
  blit. At native scale it occurs before later UI cameras draw to the target.
- Existing AA state is captured and restored on disable, camera/output change,
  unsupported state, scene teardown, and mod shutdown.

The near-launchpad motion defect is a required guardrail, not accepted input
quality. Phase 2 begins with PPv2's moving-pixel history contribution reduced
from its stock 0.85 to 0.05 and with the backend disabled by default. This does
not repair the motion field; it limits how much corrupted reprojection can
contaminate the result while the prototype is evaluated.

## Alternatives rejected

- Synchronized per-stack resolves would maintain separate histories for
  already-composited contributors and risks double filtering and edge
  disagreement.
- A new composite depth/motion pass is not yet justified because the final
  physics camera normally exposes coherent combined inputs. It becomes the
  next option if conservative PPv2 still ghosts or flashes near the pad.
- DLAA is not a Phase 2 alternative. It requires the Phase 4 gate, valid scene
  inputs, and a working NVIDIA runtime feature.

## Consequences

- Phase 1's resolve-placement gate is satisfied for a reversible PPv2 test.
- PPv2 TAA remains experimental until launchpad, flight/map transitions, VAB,
  discontinuities, and steady-state allocation behavior pass Phase 2 tests.
- A visible flash in the motion diagnostic is not a history reset; before this
  prototype no temporal history existed. After enablement, actual history
  resets are logged as explicit `[ReduxBetterAA/History]` reasons.
- DLAA remains blocked and no vendor binary, native bridge, or NVIDIA-specific
  backend is added.

## Evidence

- Camera reports for Flight, Map3DView, and VehicleAssemblyBuilder.
- Native 100% and non-native 200% render-scale reports.
- Four clean orbit captures, multiple alternating clean/corrupt launchpad
  captures, and a clean stationary ascent capture at roughly 687 m.
- `phase1-latest.json` captured on Unity `6000.4.1f1`, Redux `0.2.8.5.103184`,
  KSP2 `0.2.3.0`, D3D11, and NVIDIA RTX 5070 Ti.
