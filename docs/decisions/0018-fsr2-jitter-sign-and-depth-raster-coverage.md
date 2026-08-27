# Decision 0018: FSR2 jitter sign and depth raster coverage

## Status

Accepted for the 0.5.15 comparison build. Runtime acceptance requires an FSR2
stationary-edge comparison and a F10 schema 17 report.

## Evidence

A stationary launchpad recording compared `Linear Depth (raw jittered)` with
the former `Linear Depth (de-jittered)` view. Across the first 24 captured
frames, hard vessel and horizon silhouettes toggled in a regular one-pixel
pattern, usually changing the same 439 binary edge pixels. Best whole-frame
alignment remained zero for 61 of 62 transitions. This is the fingerprint of
subpixel raster coverage, not random whole-camera motion.

As a control, the same view is stable with AA Off apart from occasional isolated
pixel flicker. Off supplies zero projection jitter, so this observation rules
out a meaningful continuous upstream camera or geometry shake in the tested
scene.

The compensated view merely shifts where a point sample is read from the
already-rasterized single-sample depth texture. It cannot reconstruct the
geometric sample that would have existed at a different projection phase, so
it is not expected to make every binary silhouette stationary.

Source comparison also found a backend-specific contract error. Redux Better
AA creates its jittered Unity projection with PPv2
`RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix`, whose pixel input
increases Unity's projection offsets. AMD's official FSR2 Unity integration
converts a dispatch jitter sample into the opposite Unity projection
translation. The FSR2 dispatch value must therefore negate both components of
the pixel sample passed to the PPv2 projection helper. DLAA already used this
mapping; FSR2 did not.

## Decision

- Negate both projection-helper jitter components before assigning FSR2
  `jitterOffsetX` and `jitterOffsetY`.
- Continue sending raw jittered color and depth to FSR2. Do not independently
  resample or filter depth.
- Do not enable FSR2 motion-vector jitter cancellation without evidence that
  the supplied Unity motion vectors contain projection jitter.
- Record projection-helper and FSR2 dispatch jitter values independently in
  F10 reports. They must have opposite signs and equal magnitudes.
- Relabel the output-aligned depth diagnostic as a jitter-compensated sample
  and state its single-sample raster limitation in the Ctrl+F10 help.
- Use AA Off plus raw depth as the control for future upstream camera/geometry
  shake investigations.

## Expected behavior

FSR2 should no longer reproject history using a sample offset opposite to the
one that produced current color and depth. Stationary edges should become more
stable and less smeared. This directly targets the stronger FSR2 artifact in
the supplied recording; it does not change DLAA, whose dispatch sign was
already correct.

Hard edges in a diagnostic depth view may still toggle under temporal
projection jitter. With AA Off, continuing raw-depth movement would be evidence
for a separate upstream camera or geometry problem.

## Resource and lifecycle impact

The production change adds one vector negation and no resources, allocations,
readbacks, or lifecycle changes. Two float pairs are allocated only while
serializing an explicit capability report. Backend mutual exclusion and cleanup
are unchanged.
