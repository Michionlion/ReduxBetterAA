# Decision 0032: exclude the exact invalid indirect foliage object history

## Status

Accepted for 0.5.28 on Redux 2.8.5 and Unity 6000.4.1f1. Redux 2.9 remains
unverified because its player uses a different Unity patch line.

## Context

The 0.5.24 choice-B repair rerouted direct vegetation draws through
`Graphics.RenderMeshIndirect` with `MotionVectorGenerationMode.Camera`. It
removed the broad radial launchpad corruption, but later captures found a thin
diagonal dotted ray in raw motion. The line was most visible through DLAA preset
K and disappeared when post-process sanitizer E was enabled.

At the exact stationary pose, current and previous camera matrices were equal
and only subpixel Halton jitter advanced. Five sparse pixels carried roughly
200--328 pixels of motion. Subpixel jitter cannot generate that magnitude, and
the values existed before vendor sign conversion or reconstruction.

## Pass and producer isolation

A diagnostic copy of Unity 6000.4.1f1's built-in motion shader tagged the
full-screen camera and object passes separately. The dotted ray consisted of
object-pass overwrites. Discarding all object passes removed it, and isolating
the renderer to one vegetation item and material retained the complete line:

```text
item:     Base_tree_poplar_large_01_kerbin
item ID:  7821a620-ecf3-4106-909d-0d7ca7cb01a4
material: M_Kerbin_Grassland_Branch_01
shader:   NatureManufacture Shaders/Trees/Tree_Leaves_Specular
```

The offending pass still received identity `_PreviousM` and a current
`unity_ObjectToWorld` translation at `_WorldSpaceCameraPos`. Changing the RMI
world bounds, overriding `_PreviousM` through the material property block, and
disabling a named material `MotionVectors` pass did not affect the line. Unity's
Built-in replacement pass supplies those matrices after material properties
and does not depend on the source material's named pass.

## Rejected zero-motion repair

`MotionVectorGenerationMode.ForceNoMotion` removed the stationary ray but wrote
large zero-motion leaf triangles over correct camera motion during a pan. That
creates exactly the kind of history mismatch temporal reconstruction must not
receive. It is not used.

## Decision

Keep the selected B reroute and pair it with an exact production derivative of
Unity 6000.4.1f1's `Hidden/Internal-MotionVectors` shader. Its object vertex
stage identifies only this invalid state:

```text
sum(abs(_PreviousM - identity)) < 0.0001
distance(current object translation, _WorldSpaceCameraPos) < 0.5 metres
```

The object fragment is discarded only when both conditions hold. Unity's
full-screen camera pass remains byte-for-byte equivalent in behavior, so valid
camera reprojection already under the leaves survives. All other object passes
use the native calculation. This is a producer/pass correction, not a motion
magnitude sanitizer and not a zero-motion replacement.

The guarded B patch does not reroute draws until the exact shader is loaded and
installed. Failure, unsupported shader state, another mod owning the global
built-in motion shader slot, or runtime failure restores the original vegetation
renderer. Disable and teardown restore the prior custom shader deterministically.
The TestHarness motion-pass probe temporarily owns the slot through an explicit
lifecycle handshake and restores the production shader afterward.

Sanitizer E remains an independent, disabled-by-default defense for unrelated
bad producers and unsupported future renderers. It is no longer required for
the known poplar-leaf ray on the validated build.

## Verification

- Pass and item isolation: TestHarness run `7ba2426866f4`.
- Rejected ForceNoMotion pan control: run `8f56c4fc7036`.
- Exact predicate, stationary and moving-camera control: run `c70c3b33e8e7`.
- Packaged production path with no diagnostic override: run `a7295b623d9b`.
- The exact-predicate run used B on and E off. The stationary raw buffer had no
  ray; the pan retained coherent camera motion without zero-vector foliage
  holes; DLAA remained clean.
- The production report recorded the repair active and available, sanitizer E
  disabled, and 3,407 rerouted calls. Its 18 stationary/pan/DLAA-K captures had
  no new player-log errors or warnings.
- Runtime packaging and installed production-path verification are recorded in
  `docs/phase-1/build-and-package.md`.

## Cloud issue boundary

The intermittent distant-cloud loss did not reproduce in the latest tests and
is not changed here. Decision 0031's schema-22 report fields and five F10 cloud
source captures remain available. Better AA does not reset, clear, or otherwise
modify the cloud renderer's private history.
