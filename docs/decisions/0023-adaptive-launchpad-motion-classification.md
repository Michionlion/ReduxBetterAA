# Decision 0023: adaptive launchpad motion classification

## Status

Reverted in 0.5.23 by Decision 0024. Runtime testing found the adaptive
classifier too sensitive to ordinary motion. The sign-diagnostic clarification
is retained, but the production classifier is not.

## Evidence

The 0.5.21 capture `phase1-20260827-213909-001` retained `invertX=true` and
`invertY=true`. Its sanitized motion was an unsaturated, craft-centered flow
captured while the project-tracked current and previous view-projection matrices
also differed. Samples on distant geometry followed the tracked camera
reprojection. This is consistent with real orbit-camera motion around the craft,
not a view-dependent component convention.

Unity Built-in motion remains previous-to-current for every view angle. DLAA and
FSR2 require current-to-previous, so both sanitizer component signs remain
negative. The old sign-agreement view could nevertheless show red close to a
component zero-crossing because it made a decision at only 0.1 pixel of axis
motion. A local red patch at such a crossing was too weak to infer a global sign.

The capture also exposed a real remaining safety gap. The whole-frame 16-anchor
classifier reused the 96-pixel local camera-disagreement envelope. That local
envelope is intentionally broad to preserve independently moving objects, but it
can allow the low-magnitude region around the known upstream radial field's pole
to pass through when the affected spherical direction is near the view.

## Decision

- Keep the full-resolution local disagreement envelope at 96 pixels and the
  unverified/fallback motion limits at 256 pixels.
- Give the 16-anchor whole-frame classifier a separate adaptive disagreement
  envelope. It is 8 pixels during slow camera motion and increases to 10% of
  project-tracked camera motion during a fast pan.
- Continue requiring six suspicious anchors out of sixteen. This retains the
  screen-coherence requirement and avoids treating one moving object as a
  corrupt frame.
- On a classified frame, continue replacing the entire field with bounded
  project-tracked camera reprojection in the same GPU frame.
- Rename the diagnostic to `Motion: Raw Sign Agreement`. Require at least 0.75
  pixel of motion on the tested component before showing a green/red sign result;
  ambiguous axis zero-crossings remain dark blue.

## Consequences

The tighter classifier targets only a screen-wide mismatch. It does not lower
the local object-motion tolerance and does not reject coherent fast camera
motion merely because it exceeds 256 pixels. A legitimate orbit or dolly can
still produce a radial sanitized field centered on the tracked craft; the field's
shape alone is not corruption. The sanitizer decision view remains the
authoritative distinction: orange is a classified upstream field, green is
preserved raw motion, and yellow/red are local replacement/rejection.

No new texture, readback, allocation, or lifecycle owner is introduced. The
existing 1x1 classifier pass performs the additional scalar threshold math.

## Runtime verification

1. At the launchpad with DLAA active, slowly orbit the camera through the view
   angles that previously showed the circular field. Capture sanitized input and
   sanitizer decision at the same angle.
2. Confirm the known corrupt field makes the decision view orange and the
   sanitized input becomes bounded tracked camera motion.
3. Deliberately orbit and dolly the camera around the craft. Confirm legitimate
   craft-centered motion remains green and DLAA does not freeze or smear it.
4. Pan quickly in flight and orbit. Confirm coherent motion remains usable and
   Custom, DLAA, and FSR2 do not introduce new history trails.
5. In raw sign agreement, pan one axis at a time over static terrain. Confirm a
   meaningful wrong sign is coherent red, while local zero-crossings are dark
   blue rather than being misread as a view-dependent inversion.
