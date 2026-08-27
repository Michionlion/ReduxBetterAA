# Decision 0013: state-specific camera discovery and motion consistency

## Status

Accepted for the 0.5.10 comparison build. KSC, main-menu, and launchpad
behavior require in-player validation before these paths are considered stable.

## Context

The temporal coordinator previously supported FlightView, Map3DView, and the
VAB only. Runtime captures show that `KerbalSpaceCenter` keeps enabled
`FlightCameraScaled_Main` and `FlightCameraPhysics_Main` stacks with usable
buffers. The main menu instead presents its three-dimensional scene through
`Camera.Scaled`, followed by separate sky and Flow/UI cameras.

Launchpad captures also refine the earlier motion guardrail. The visible
four-quadrant/radial field exists in Unity's raw motion texture before Redux
applies either component sign. It remains anchored to a world/spherical
direction as the view moves. Negating X or Y changes its direction but cannot
create or repair its magnitude. Some affected samples remain below the existing
256-pixel cutoff, so a magnitude-only test is insufficient.

KSP game-state changes do not always coincide with a Unity scene load. A camera
graph that refreshes on scene events alone can therefore retain the previous
state's unsupported result.

## Decision

- Treat `KerbalSpaceCenter` as a supported state and discover its resolve and
  shared-jitter cameras from the same live scaled/physics stack contracts used
  in flight.
- Treat `MainMenu` as a supported state. Prefer the exact `Camera.Scaled`
  scene camera and reject Flow, UI, overlay, skybox, flare, debug, and render-
  scale presentation cameras. This is deliberately state-specific rather than
  a general fallback for unknown game states.
- Permit Custom TAA, DLAA, and FSR2 to attach to a supported final camera even
  when it has no PPv2 `PostProcessLayer`. If a layer exists, preserve and
  temporarily disable its AA as before. PPv2 TAA still requires a valid PPv2
  layer and must report unavailable rather than emulate one.
- Poll the allocation-free game-state enum at 4 Hz. A state transition marks
  the temporal camera graph dirty with `SceneChanged`, causing normal backend
  teardown, rediscovery, and history reset.
- Continue applying X/Y signs explicitly in the sanitizer. For depth-covered
  pixels, also compare finite raw motion with bounded camera reprojection. If
  disagreement exceeds 64 pixels and the camera fallback itself is no more
  than 64 pixels/frame, use the fallback. Existing invalid or greater-than-256
  pixel samples follow the same fallback-or-zero path.
- Make every F10 screenshot request write a same-moment capability report
  before queuing the image. This closes the prior evidence gap where F10 images
  had no matching JSON report.

## Safety and limitations

The 64-pixel disagreement envelope intentionally preserves ordinary object
motion and small camera/model differences. An independently moving object more
than 64 pixels away from camera reprojection may be replaced by camera motion;
this is preferable to feeding the captured screen-scale launchpad field into a
vendor temporal history, but must be revisited if fast-object captures show a
regression.

Main-menu camera ordering is based on the observed target build. F10 now records
the exact camera graph so a different Redux/KSP build can be diagnosed without
silently selecting a UI camera. UI must remain outside the selected scene
resolve. The menu path must fall back safely if its color, depth, or motion
inputs prove incomplete at runtime.

## Runtime verification

1. At a stationary launchpad, capture raw motion, normalized motion, motion
   validity, and sign agreement while using DLAA and FSR2. Confirm that the raw
   diagnostic may still show KSP's defect while the reconstructed scene no
   longer jitters or smears in that fixed direction.
2. Pan on the launchpad and away from KSC. Confirm both inversion defaults
   remain enabled and that changing either sign no longer appears to repair or
   reintroduce the magnitude defect.
3. Enter KSC without loading a new Unity scene. Select Custom, DLAA, and FSR2
   and confirm the status identifies `FlightCameraPhysics_Main`; capture F10 for
   each tested backend. Test PPv2 separately.
4. Return to the main menu. Confirm the selected resolve camera is
   `Camera.Scaled`, AA affects the three-dimensional scene, and Flow/UI text
   remains crisp and unghosted. Record any backend that lacks depth or motion.
5. Exercise Flight -> KSC -> MainMenu -> Flight transitions and confirm each
   state rebuilds once without recurring warnings, duplicate hooks, or leaked
   histories.
