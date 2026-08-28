# Decision 0031: prefer DLAA preset M and capture cloud inputs

## Status

Accepted for 0.5.27. The dotted-ray attribution below is superseded by
Decision 0032; the M default and cloud diagnostics remain accepted.

## Context

Two DLAA-only rendering defects remained after the 0.5.26 release:

- a diagonal ray of dark dots, most visible with preset K; and
- intermittent loss of distant clouds at large flight-camera distances.

History resets did not restore the missing clouds. A controlled TestHarness
matrix replayed the exact dot-ray pose with presets K and M, zero projection
jitter, manual and PPv2 exposure sources, and zero and normal sharpening. A
second matrix exercised fresh DLAA contexts across the cloud renderer's 0.25 to
0.5 resolution-scale transition. A third held the KSP flight-camera controller
at 120 km while alternating three fresh K contexts, Off intervals, and M, and
captured the cloud renderer's private `_finalSceneColor` RGB/alpha and
`_preUpsample` RGB targets.

## Findings

- The dotted ray was present for every K condition and absent or effectively
  absent for every equivalent M condition. Later raw motion-vector captures
  proved that the foliage repair produced the same sparse line before DLAA.
  K amplified that invalid input while M hid it; K was not the producer.
- The cloud renderer remained enabled, completed its dynamic-resolution
  transition, and retained populated private render targets in all controlled
  Off/K/M cases. Presented cloud coverage also matched. This does not disprove
  the reported intermittent failure, but it rules out a deterministic distance
  threshold and the tested stale-history transition.
- Better AA must not reset or alter the cloud renderer's independent temporal
  history without evidence that the fault originates there.

## Decision

- Preset M is the conservative, fresh-install, invalid-value, and legacy
  `Default` migration target. Explicit F, J, K, L, and M selections remain
  valid; existing explicit K selections are not silently migrated.
- The public preset description identifies M as Redux Better AA's balanced
  default. Decision 0032 removes the obsolete K dotted-ray warning after fixing
  its raw foliage-motion source.
- Capability-report schema 22 records the selected camera's
  `VolumeCloudRenderer` mode, transition flags, dimensions, resolution scale,
  and every private `RenderTexture` descriptor through a diagnostics-only
  reflection boundary.
- A plain-F10 screenshot also writes five source images beside the presented
  frame: `cloud-final-rgb`, `cloud-final-alpha`, `cloud-pre-upsample-rgb`,
  `cloud-new-rays-alpha`, and `cloud-previous-rays-alpha`. The latter pair
  distinguishes freshly rendered cloud coverage from cloud-local history.
  Failure to locate a cloud renderer or target remains non-fatal and is
  reported in the capture status.

## Lifecycle and performance

Cloud state reflection occurs only during a report capture. Texture readback,
PNG conversion, and managed allocations occur only during an explicit one-shot
screenshot request; they are not part of the production render path. The
diagnostic owns each temporary `Texture2D`, restores `RenderTexture.active`,
and releases the temporary texture in a `finally` block.

## Verification

Automated coverage verifies the M defaults and preserves explicit preset
values. Runtime verification must confirm that:

1. a new or reset configuration selects M while an explicit K selection stays K;
2. plain F10 writes the presented screenshot plus all available cloud source
   images without leaving the engineering panel hidden;
3. schema 22 identifies `FlightCameraPhysics_Main`, the cloud configuration,
   transition flags, dimensions, and created target descriptors; and
4. when distant clouds disappear, the matching source images establish the
   first stage at which coverage is lost.
