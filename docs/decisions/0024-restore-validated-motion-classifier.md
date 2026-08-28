# Decision 0024: restore the validated motion classifier and truthful diagnostics

## Status

Accepted for the 0.5.23 comparison build. Launchpad visual acceptance remains a
manual runtime gate.

## Evidence

The 0.5.22 adaptive 8-pixel or 10%-of-camera-motion classifier was reported as
far too sensitive and did not isolate the known launchpad field as intended.
The matching captures `phase1-20260828-010842-004` and
`phase1-20260828-010843-005` also exposed a separate diagnostic failure: both
reports selected `Off`, so no sanitizer texture or matrix history existed.
The decision shader silently substituted a black texture. A frame with raw
motion then appeared red while a quiet frame appeared green, even though
neither image represented a sanitizer decision.

The earlier 0.5.21 policy had already separated the known corrupt field from
healthy fast pans in launchpad captures. It used the same 96-pixel disagreement
envelope for the 16-anchor whole-frame classifier and for local replacement,
while allowing coherent camera motion beyond the 256-pixel unverified cap.

## Decision

- Remove the 0.5.22 adaptive classifier threshold and restore the validated
  96-pixel disagreement envelope for all sixteen classifier anchors.
- Keep the independent 256-pixel unverified-motion and bounded-fallback limits.
- Keep six suspicious anchors out of sixteen as the screen-wide corruption
  requirement.
- Retain the 0.5.22 raw sign-agreement confidence display. It affects only the
  diagnostic and cannot modify production motion.
- Mark sanitized-motion and sanitizer-decision views dark blue when no live
  sanitizer texture exists. Their help text explicitly states that Off and
  PPv2 cannot produce the decision view.

## Consequences

The production sanitizer returns to 0.5.21 behavior. A user cannot mistake an
unavailable sanitizer for a red whole-screen rejection, and captures intended
to diagnose the launchpad classifier must keep Custom, DLAA, or FSR2 active.
No resource, readback, or per-frame allocation is added.

## Runtime verification

1. Activate DLAA at the launchpad and capture sanitizer decision while slowly
   orbiting through the known affected direction. Corrupt frames should be
   orange; healthy motion should remain predominantly green.
2. Repeat a deliberate fast pan. Coherent motion should not be broadly rejected.
3. Select Off, reopen sanitizer decision, and confirm the scene is uniformly
   dark blue rather than red or green.
