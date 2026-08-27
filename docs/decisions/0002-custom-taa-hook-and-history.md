# Decision 0002: Custom TAA final-camera hook and project-owned history

- Status: Accepted for experimental Phase 3 comparison
- Date: 2026-08-23
- Phase: 3 prototype
- Scope: SPEC Sections 8.2-8.4 and 11.2-11.6

## Context

Decision 0001 established the final physics/map/VAB scene camera as the only
measured scene resolve point before UI. Phase 2 uses PPv2 at that point. Phase 3
needs persistent project-owned history and multiple shader passes while keeping
PPv2 available for identical-path comparisons.

The current Redux API exposes the camera and its `PostProcessLayer`, but it does
not yet expose a backend-neutral command-buffer frame-input contract. Adding a
native bridge or replacing the presenter would exceed Phase 3.

## Decision

Attach one late-execution `OnRenderImage` hook to the discovered final scene
camera only while Custom TAA is selected. Disable PPv2 AA on both the final and
shared contributor layers, apply one shared jitter sample, and own two color
histories, two depth histories, and one resolve target.

Use the observed PPv2 convention `previous UV = current UV - motion`. Pass the
current inverse and previous view-projection matrices to the resolve only as a
validated camera-motion fallback for extreme/invalid vectors. Never run PPv2
and Custom TAA simultaneously.

## Consequences

- PPv2 remains an independent selectable backend and a safe fallback.
- UI stays outside history because it is composed by later cameras.
- Resolution/format changes recreate history instead of resampling it.
- The hook and every render texture are destroyed deterministically on switch,
  invalidation, teardown, and shutdown.
- A later backend-neutral command-buffer integration can replace this hook
  without changing the shader algorithm or mode-selection contract.
- Runtime visual, profiler, and allocation evidence is still required before
  Phase 3 can leave experimental status.
