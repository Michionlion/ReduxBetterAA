# Decision 0028: validate the camera-motion reference before judging vendor signs

## Status

Validated in the 0.5.24 player on Direct3D 11. The 2026-08-28 launchpad
capture set confirms the selected camera reference and both explicit vendor
component inversions.

## Problem

The original Raw Sign Agreement view sampled color, depth, and motion using the
blit source's `_MainTex_TexelSize`, then reconstructed camera motion with a GPU
projection that could independently select render-texture orientation. On a
top-origin graphics API this could make the Y half broadly red even after the
launchpad producer was repaired, without showing whether the error belonged to
the raw buffer, the camera reference, or the configured vendor sign.

That view cannot be authoritative while its reference convention is untested.
Object motion and disocclusion can produce local disagreement, but they do not
justify a screen-wide axis recommendation.

## Decision

The diagnostic now mirrors the relevant Unity 6000.4.1f1 built-in motion-vector
steps:

1. Sample motion and depth with their own texture-orientation metadata.
2. Select the render-texture GPU projection when `targetTexture` is present or
   `forceIntoRenderTexture` is true; otherwise select the screen projection.
3. Convert the motion-texture UV into projection UV before inverse projection.
4. Convert the previous projected UV back into Unity's motion-texture axes,
   including Unity's explicit Y conversion on top-origin APIs.
5. Treat Unity raw motion as current-minus-previous. DLAA and FSR2 vendor input
   is current-to-previous, so Redux Better AA's explicit texture conversion is
   expected to negate each valid component. This does not rely on NVIDIA's
   similarly named indicator fields.

The new `Motion: Sign Reference Orientation Audit` deliberately compares four
reference variants while the operator performs a vertical pan:

| Display region | Projection | Y conversion |
| --- | --- | --- |
| Upper left | Screen | Unity top-origin conversion |
| Upper right | Render texture | Unity top-origin conversion |
| Lower left | Screen | No conversion control |
| Lower right | Render texture | No conversion control |

Green means the raw Unity Y component and camera-only reference have the same
sign; red means a confident reversal; dark blue means the component is too
small to decide. Schema 19 reports which upper quadrant is automatic for the
selected camera and records the camera flags and projection Y scales needed to
interpret the capture. The diagonally opposite variant may also agree because
changing projection orientation and omitting the Y conversion can cancel each
other; the authoritative comparison is between the upper automatic quadrant
and the lower quadrant in the same projection column.

## Verification gate

At a static, depth-covered scene with source repair B enabled:

1. Select AA Off to remove temporal projection jitter from the isolation run.
2. Select the reference-orientation audit, close the panel, pan vertically, and
   capture with F10.
3. Confirm the upper quadrant matching the report's automatic projection is
   coherently green. The lower no-conversion control in that same projection
   column should disagree on a top-origin API.
4. Select Raw Sign Agreement, keep both explicit vendor inversions enabled, pan
   horizontally and vertically, and capture again. Coherent static terrain
   should be green on the X and Y halves; moving objects and disocclusions may
   disagree locally.

Do not change an inversion setting from a stationary frame, a quiet component,
or an audit whose automatic reference quadrant does not pass.

## Player evidence

The `phase1-20260828-125135-001` through `-010` reports used KSP2 0.2.3.0,
Redux 0.2.8.5.103184, Unity 6000.4.1f1, Direct3D 11, and an RTX 5070 Ti at
2560x1440. AA was Off, vegetation source repair B was active, and sanitizer E
was disabled. `FlightCameraPhysics_Main` had no target texture but did have
`forceIntoRenderTexture=true`, so schema 19 selected the upper-right
render-texture-plus-Unity-Y quadrant. Screen and render-texture GPU projection
Y scales were respectively +1.73205078 and -1.73205078.

Across the eight informative vertical-pan frames, the automatic upper-right
quadrant was at least 99.9% green among decisive diagnostic pixels. The
same-column lower-right no-conversion control was 93.1% to 96.2% red. The two
remaining captures were nearly stationary and correctly rendered almost all
pixels as undecidable.

The follow-up `phase1-20260828-125208-011` frame was also nearly stationary.
During the informative `phase1-20260828-125212-012` pan, the Raw Sign Agreement
view was 99.7% green among decisive X pixels and 99.9% green among decisive Y
pixels with both explicit vendor inversions enabled. This validates the fixed
Unity-to-vendor sign conversion; it does not support runtime sign auto-tuning.
