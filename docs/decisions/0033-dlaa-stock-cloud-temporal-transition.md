# Decision 0033: isolate DLAA from the stock cloud temporal transition

## Status

**Disabled by maintainer request on 2026-09-07.** Live buffer comparisons on both
Redux 2.8.5 and 2.9 proved that persistent stock TUS kept this guard active during
ordinary launchpad flight, silently removing AA. Reinstallation did not change
the behavior. The production backend now constructs an observation-only guard:
dimensions and resizes remain visible, but it cannot suspend DLAA or projection
jitter. The previous policy and its tests are retained for historical analysis;
they are not enabled in production. The original cloud-disappearance defect is
unresolved. See [the investigation](../dlaa-reinstall-investigation-20260907.md).

Accepted as a post-0.5.28 Phase 4 compatibility fix on Redux 2.8.5, KSP2
0.2.3.0, and Unity 6000.4.1f1. Other KSP2 and Unity builds remain unverified.

## Context

The previously intermittent distant-cloud loss was reproduced deterministically
from the reporter's exact camera poses and cloudy sunset save. At the failing
poses, the stock `VolumeCloudRenderer` changed its internal render size from
1280x720 to 640x360 and enabled its temporal upscaler. Its private
`_finalSceneColor` RGB and alpha still contained the clouds, while the presented
DLAA frame contained dark rectangular holes. Disabling DLAA preserved the
clouds.

The reproduction was repeated in three independently reloaded DLAA contexts.
Resetting DLAA history, suppressing projection jitter, changing transparent
jitter, clearing or sanitizing motion, substituting far depth, and biasing
no-depth or every pixel toward current color each failed in three repetitions.
Bypassing only the NVIDIA resolve was clean in all repetitions. This locates the
failure at the second temporal resolve of a stock cloud-temporal transition; it
does not implicate Redux Better Clouds and does not require that mod.

The stock `EnableTUS` flag turns off before the cloud renderer rebuilds its
quarter-resolution targets. Resuming DLAA immediately when that flag falls was
still unstable. The targets can remain at 640x360 indefinitely, so waiting for
their old dimensions would leave DLAA disabled for the rest of the flight.

## Decision

Patch the stock `VolumeCloudRenderer.RenderClouds` method with a minimal
postfix. Harmony binds its two private current-size fields when installing the
patch. The steady-state callback forwards only the cached renderer reference,
two integers, and the public `EnableTUS` flag to `TemporalCoordinator`; it uses
no reflection, searches, or managed allocation per frame.

When NVIDIA DLAA owns the same resolve camera:

- suspend projection jitter and skip only the DLAA execution while stock cloud
  temporal upscaling is active;
- keep the suspension for 120 cloud frames after `EnableTUS` becomes false;
- restart the window if cloud temporal upscaling reactivates;
- clear the window immediately when the renderer is no longer at its
  quarter-resolution state; and
- reset only DLAA's owned history when entering or leaving the suspension.

During suspension, the already-composited scene is copied to the destination.
The cloud renderer's settings, buffers, private history, flags, and lifecycle
are never written or reset. Other Better AA backends are unchanged. Disabling
or switching DLAA drops the cached cloud state, restores camera projection
state through the existing backend lifecycle, and leaves the stock renderer
untouched.

## Verification

The production-path guard was exercised in nine independently reloaded DLAA M
contexts across three runs, including three cycles on the final cleaned build.
Every cycle replayed reports 13 through 16, reversed the transition, and waited
240 frames. The last six cycles also made a small two-way camera rotation after
DLAA resumed.

- During reports 14 through 16, stock TUS was active and DLAA compatibility
  suspension was active; no cloud holes appeared.
- At report 17, stock TUS was off but 70 to 74 settle frames remained; no cloud
  holes appeared.
- At reports 18 through 20, the settle count and suspension were both zero, so
  DLAA was executing again. The final build's nine resumed-DLAA captures
  retained the clouds, including all six moving-camera captures.
- All three three-cycle runs passed with no harness errors or warnings.

The stock renderer retained 640x360 cloud targets after its TUS flag fell. The
result is visibly lower-resolution than its 1280x720 state, but it no longer
causes the rectangular cloud disappearance. That persistent stock buffer choice
is outside Better AA's ownership.

## Known limitations

The 120-frame window is an evidence-based compatibility delay: 64 frames from
the size change and immediate resume after the TUS flag fell both failed, while
120 settled frames passed nine transition cycles. It is not a claim about every
future cloud implementation. If KSP or Redux replaces `RenderClouds` or its
private size fields, Harmony must reject the version-sensitive patch and Better
AA must retain its ordinary DLAA path rather than modifying an unknown renderer.
