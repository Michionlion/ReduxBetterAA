# 0041 — Unified AA ownership

The maintainer requested a single AA settings surface, Supersampling as a mode,
removal of out-of-scope renderer experiments, and a focused safety/architecture
review. This explicitly authorizes the existing Redux supersampling integration
within the phase-5 presentation boundary; no new upscaler or native bridge is added.

Use Redux's public presenter at 125–200%, and select native resolution for other
AA modes. Preserve the stock saved profile. Stock graphics controls are disabled
navigation hints. Off releases temporal and foliage resources while selecting
an unfiltered native scene; unload restores distinguishable state claims.

Combine shared camera/PPv2 ownership and projection restoration. Keep vendor ABI
adapters separate. Detect external scale changes and stop claiming them instead
of adding a continuous override loop. Dispose diagnostic claims before the normal
renderer during shutdown. Preserve existing custom TAA shader tuning.

See [the implementation and validation review](../aa-ownership-review.md).
