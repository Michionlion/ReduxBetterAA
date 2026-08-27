# Decision 0008: preserve transparent volumetrics and bias moving depth edges

## Status

Accepted for the vendor-backend comparison build and retained after extraction
of renderer-specific quality controls into separate mods.

## Evidence

Vendor reconstruction benefits from a current-frame bias around moving solid
depth discontinuities, where stale history can create bright or dark edge
trails. Broad pixels without finite scene depth represent sky or transparent
volumetric composition and cannot be classified safely from depth alone.

## Decision

- Build a narrow current-frame bias mask only around finite moving depth edges.
- Leave broad no-depth regions unmarked so transparent and volumetric content
  can retain useful temporal accumulation.
- Never reach into another renderer or effect to change its quality, history,
  buffers, or lifecycle from Redux Better AA.
- Reset only the selected AA backend's owned history for AA discontinuities.

## Expected behavior

Moving solid silhouettes receive a conservative current-frame bias without
forcing all transparent or far-plane pixels to reject history. Another mod's
temporal effects remain independently owned and are never reset by AA changes.

## Resource impact

The mask owns one output-sized single-channel render target and one material.
They are released on backend switch, resolution change, teardown, and shutdown.
The hot path uses no managed allocation after warm-up.
