# Decision 0037: measured Custom TAA history, edge and bandwidth improvements

Phase 3; SPEC 11.2–11.6. This maintains the existing Phase 4 native-AA comparison
paths and introduces no upscaling backend or vendor runtime dependency.

## Findings and changes

- The 16-read separable Catmull-Rom history filter can combine its two positive
  center weights per axis into a bilinear read. Nine reads retain the negative
  outer lobes and clamped footprint. Hardware interpolation precision means the
  outputs are not bit-identical: the HDR GPU stress fixture permits 0.008 absolute
  error on values up to 3.5. Integer-phase output is identical. This uses the
  linear-fetch grouping principle described in [GPU Gems 2, chapter 20](https://developer.nvidia.com/gpugems/gpugems2/part-iii-high-quality-rendering/chapter-20-fast-third-order-texture-filtering),
  with the Catmull-Rom center pair grouped independently of its negative lobes.
- Resolving into a third color target and copying it into the write history was
  redundant. Resolve directly into the opposite history, then present/sharpen
  from it. History read/write never alias and sharpening never enters history.
  Remove one allocation and one full-screen copy; preserve reset, resource-loss,
  recreation and teardown behavior. ARGBHalf plus RFloat histories now cost
  24 bytes/pixel: 47.46 MiB at 1080p, 84.38 MiB at 1440p, 189.84 MiB at 4K.
  This excludes the shared sanitizer and all game targets.
- Zero history weight did not make uninitialized history safe: IEEE NaN times
  zero remains NaN. Reset frames now return current color before history reads;
  nonfinite reprojected history also falls back to current color.
- The depth-edge boost restored stationary history even at maximum motion
  response, defeating the moving-history control. Fade that boost out with the
  existing motion response. Retain its stationary behavior.
- The prior depth search used an asymmetric 3x3 area around floor(previousUV),
  permitting a surface outside the bilinear center footprint to accept stale
  history. Restrict validation to its four central depth texels. Catmull-Rom's
  outer color lobes remain controlled by neighborhood clipping; depth matching
  is still a heuristic and does not replace an object-aware disocclusion mask.
- Preserve current alpha coverage instead of accumulating alpha. Bound optional
  sharpening by its five-sample color extrema to avoid bright/dark overshoot.

## Reproducible evidence

`CustomTaaPixelTests` executes the production shader on the GPU. The frozen
`Tests/EditMode/Fixtures/CustomTaaBaseline.shader` is the pre-change shader,
renamed only for test isolation (original SHA-256
`d0c7bd1ad441bca9c16f22a8737f449adb176a9b6f1d4ae059285e8859598d8e`).
It is not registered as a production Addressable. Tests cover fractional and
integer filtering, borders/HDR, invalid history/reset, moving edge retention,
depth-footprint rejection, alpha, sharpen halos, and a 32-frame analytic edge
against an independently integrated 32x32-per-pixel spatial reference.

`tools/Run-QualityTests.ps1` compiles a separate test adapter, launches the saved
fixture, records raw same-frame input/output and three timing windows per mode,
restores user settings and removes/restores the adapter. This is separate from
the small `Run-VisualTests.ps1` suite. Its allocations/readbacks never ship.
See [quality comparison workflow](../taa-quality-testing.md) for interpretation
and the recorded run findings. This is scoped evidence, not broad Phase 3 or
public-beta acceptance across all scenes, motion and discontinuities.
