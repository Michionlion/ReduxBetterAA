# Decision 0026: attribute the launchpad raw-motion field to an indirect vegetation draw

## Status

Root cause confirmed in the Unity 6000.4.1f1 player with Redux Test Harness.
No production renderer change or sanitizer change is accepted by this record.

## Reproduction

The harness loads `local/launchpad-fly-safe-15`, selects
`FlightCameraPhysics_Main`, disables AA, and places the flight orbit camera at:

```text
distance = 45 m
yaw      = 0 degrees
pitch    = 35 degrees down
FOV      = 55 degrees
```

This reliably exposes the stationary four-quadrant/radial field in the raw
motion texture. Yaw 90 and 135 degree controls are clean. The following
diagnostic scripts preserve that reproduction:

- `betteraa-launchpad-motion-native-pass.lua`
- `betteraa-launchpad-motion-object-history.lua`
- `betteraa-launchpad-motion-producer-suppression.lua`
- `betteraa-launchpad-motion-vegetation-items.lua`
- `betteraa-launchpad-motion-base-poplar-material.lua`
- `betteraa-launchpad-motion-vegetation-inventory.lua`

The Better AA probe installs a diagnostic copy of Unity 6000.4.1f1's
`Hidden/Internal-MotionVectors` only while a harness test requests it. Normal
backends never select that shader.

## GPU pass evidence

The replacement motion shader first tagged the built-in camera pass separately
from the per-object pass. The corrupt polygon is written exclusively by the
per-object pass:

- zeroing the camera pass removes the ordinary background camera field but
  leaves the polygon unchanged;
- substituting the project-tracked previous camera VP does not change it;
- the private `_PreviousVP` and `_NonJitteredVP` differ only by the small,
  expected camera history for this stationary test;
- an empty culling mask and a newly cloned camera are clean.

The object pass was then split into its current vertex, previous vertex,
current object matrix, and previous object matrix terms:

| Object-history expression | Result |
| --- | --- |
| current vertex + current object matrix | neutral |
| current vertex + Unity `_PreviousM` | corrupt field |
| previous-position stream + current object matrix | neutral |
| native expression | corrupt field |

`_HasLastPositionData` is false over the culprit, so TEXCOORD4 is not involved.
The previous object matrix is exactly identity. The current object matrix is
identity plus translation `(6.645, -36.969, -24.766)`, matching the selected
camera position `(6.646, -36.970, -24.779)` to capture precision.

The observed vector is therefore the projection of one camera-centred current
draw against a previous object located at the coordinate origin. That directly
produces the large radial values even when neither the camera nor the visible
world object moved.

## Owning KSP draw

Harness-only Harmony prefixes suppressed one managed producer at a time after
the camera pose was stable. Atmosphere scattering, volume clouds, PQS terrain,
PQS ocean, light quads, and vegetation billboards all left the artifact intact.
Suppressing this method alone made the motion texture neutral:

```text
AwesomeTechnologies.VegetationSystem.VegetationSystemPro
    .RenderVegetationItemLODIndirect(...)
```

The vegetation inventory then isolated every item submitted by that method.
Only this item reproduced the field:

```text
item:     Base_tree_poplar_large_01_kerbin
item ID:  7821a620-ecf3-4106-909d-0d7ca7cb01a4
mesh:     tree_poplar_01, LOD 0
material: M_Kerbin_Grassland_Branch_01
shader:   NatureManufacture Shaders/Trees/Tree_Leaves_Specular
```

The first leaf submesh/material by itself reproduces the full field. Its bark
submesh and every other submitted plant or tree do not contribute a measurable
field at this pose.

Runtime inventory confirms that the vegetation method uses the direct legacy
`Graphics.DrawMeshInstancedIndirect` path, not a camera command buffer. Its
world bounds are centred exactly on the camera and have 356.286 m extents. The
instance locations exist only in the vegetation GPU buffers; the legacy draw
call supplies a culling `Bounds` and indirect argument buffer, but no previous
per-instance transforms.

## Root cause

The raw artifact is not an inversion error, camera cut, stale target, depth
error, multi-camera handoff, or bad camera VP history. It is an invalid built-in
object-motion draw for a GPU-indirect vegetation leaf batch:

1. Vegetation Studio submits the leaf submesh with
   `Graphics.DrawMeshInstancedIndirect` and camera-centred batch bounds.
2. The normal vegetation shader obtains current instance placement from GPU
   buffers.
3. Unity's built-in replacement motion pass receives no valid previous
   per-instance transform for this legacy indirect draw. In the captured pass,
   `_PreviousM` remains identity while the current draw is camera-centred.
4. The replacement pass rasterizes the poplar leaf geometry with that mismatched
   history, producing the polygonal coverage and origin-to-camera radial values.

This also explains the reproduction envelope. The field appears only where the
poplar leaf triangles cover the view, remains aligned with their world-space KSC
location while the camera turns, and disappears after the vegetation system
culls the batch with altitude or direction.

Unity's issue tracker records that the Built-in Render Pipeline did not extend
motion-vector support to `DrawMeshInstancedIndirect`. The current API marks the
legacy call obsolete in favour of `Graphics.RenderMeshIndirect`; current
`RenderParams` also exposes an explicit `motionVectorMode`. Unity's non-indirect
`Graphics.RenderMesh` accepts an explicit previous object-to-world transform.
Those APIs confirm the missing ownership in the legacy call rather than a
Better AA sign convention problem.

## Clean remediation direction, not implemented

Because the affected vegetation is static, its correct object-motion policy is
camera-only. The preferred renderer-level repair is to migrate this vegetation
submission to a supported `Graphics.RenderMesh*` path and set
`RenderParams.motionVectorMode` to `Camera`, allowing the existing full-screen
camera pass to describe it. If wind-deformed leaves must contribute object
motion, the vegetation renderer instead needs a dedicated motion pass with
current and previous per-instance data from its own GPU buffers.

A material- or draw-specific exclusion from object motion is a smaller upstream
alternative. Any of these repairs belongs in Redux/vegetation rendering, where
the draw and its instance history are owned. Better AA should keep its current
sanitizer until a renderer-level repair is implemented and visually verified,
but this investigation deliberately does not tune or replace that sanitizer.

## Verification records

- Native pass split: run `1bc3b01d23d3`.
- Object-history split: run `a09f1447dc5a`.
- Producer suppression: run `5566090f99fb`.
- Vegetation-item isolation: run `5958ae1579d9`.
- Leaf-material isolation: run `37859583a29e`.
- Direct-draw inventory: run `617df7f68489`.

All six runs passed without new report warnings or errors. The diagnostic
suppression and material isolation are restored at test teardown.
