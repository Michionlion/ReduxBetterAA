# Decision 0016: jitter-aware depth alignment

## Status

Accepted for the 0.5.13 comparison build, with the diagnostic interpretation
amended by Decision 0018 after the 0.5.15 stationary launchpad capture.

## Evidence

Custom, DLAA, and FSR2 deliberately jitter the scaled-space and physics-space
camera projections with the shared Halton sequence. A raw depth silhouette must
therefore move by a fraction of a pixel even when the camera and scene are
stationary. Nearby ground geometry makes this movement easier to see; it does
not by itself prove unstable depth precision.

Source inspection found two actual coordinate mismatches. Custom TAA already
de-jittered current color with `outputUv - jitter`, but sampled current depth,
depth neighborhoods, motion dilation, and stored history depth at unshifted
output UVs. It consequently compared color and depth from different raster
locations at silhouettes. The shared motion sanitizer reconstructs fallback
camera motion using non-jittered matrices, but treated a raw jittered depth
sample's raster UV as its non-jittered UV. This is a subpixel error in healthy
frames and becomes screen-wide when the launchpad corruption classifier selects
camera fallback.

The shipped KSP assembly updates the flight camera mounts and shot in
`UniverseCameraManager.OnLateUpdate`; the physics camera requests a normal Unity
depth texture. No separate fixed-step depth-copy implementation or evidence of
depth-buffer precision failure was found in the inspected path.

## Decision

- Keep vendor inputs as raw jittered color/depth/motion plus the vendor jitter
  offset. DLAA and FSR2 define that contract and must not receive a filtered or
  independently shifted depth texture.
- Align all Custom current-depth samples, motion dilation, and history-depth
  storage to the same de-jittered current-source coordinate as its color.
- Convert sanitizer raw raster UV to non-jittered current UV by adding the
  current normalized projection jitter before matrix reprojection.
- Expose separate raw-jittered and jitter-compensated point-sample depth
  diagnostics. Report the exact current jitter with the sanitizer matrix
  snapshot. Decision 0018 limits what can be inferred from the second view.
- Do not temporally smooth, blur, or otherwise alter depth. Such filtering would
  hide discontinuities and produce invalid surface correspondences.

## Expected behavior

With Custom, DLAA, or FSR2 active, the raw depth view should visibly follow the
subpixel sequence at hard stationary edges. The jitter-compensated view can
still toggle because it point-samples a single-sample raster and cannot recover
coverage absent from that frame. Custom depth rejection should nevertheless use
color, depth, and motion at coherent source coordinates. On a classified
launchpad corruption frame, DLAA, FSR2, and Custom receive camera fallback
computed from the correct current coordinate.

Use AA Off plus raw depth—not residual motion in the compensated sample—as the
control for an upstream camera or geometry stability investigation.

## Resource and lifecycle impact

The production changes add two vector uniforms and no textures, readbacks, or
managed allocations. The extra depth view is diagnostic-only and reuses the
existing visualizer material and command buffer. Resource ownership and backend
mutual exclusion are unchanged.
