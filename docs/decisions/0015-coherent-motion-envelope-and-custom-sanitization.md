# Decision 0015: coherent motion envelope and shared Custom sanitization

## Status

Accepted for the 0.5.12 comparison build. Runtime acceptance requires launchpad
and fast-pan captures in Custom, DLAA, and FSR2.

## Evidence

The 0.5.11 launchpad captures cleanly separated two states. Corrupt frames made
the sanitizer-decision view uniformly orange, showing that at least six widely
spaced anchors disagreed with project-tracked camera reprojection. Healthy
frames made the same view uniformly green. The sanitized DLAA input removed the
launchpad field, but the 64-pixel absolute ceiling also rejected deliberate,
otherwise coherent camera pans.

The attempted managed reads of Unity `_NonJitteredVP` and `_PreviousVP` both
returned identity matrices. They are therefore unavailable through
`Shader.GetGlobalMatrix` at this hook and are no longer treated as valid
root-cause evidence. The raw field, project-tracked matrices, and sanitizer
result still prove that corruption originates upstream in Unity/KSP's composed
motion input. Distinguishing the exact internal matrix writer from render-target
reuse ultimately requires a GPU-side capture or a Redux renderer-level trace.

The first schema-3 motion-statistics burst also exposed that the statistics pass
stopped writing after it was appended to the multi-pass visualizer shader. A
dedicated one-pass statistics shader replaces that fragile pass index.

## Decision

- Raise the unverified raw-motion and bounded fallback envelopes from 64 to 256
  pixels/frame.
- Keep a tighter 96-pixel raw-versus-camera disagreement test. Motion above 256
  pixels remains valid when it agrees with project-tracked camera reprojection;
  the cap therefore targets unverified object/buffer motion rather than normal
  fast camera rotation.
- Continue classifying a frame as corrupt when six of sixteen anchors are
  invalid, unverified over-limit, or disagree with camera reprojection. A
  classified frame uses bounded project camera motion everywhere in that same
  frame.
- Feed Custom TAA from the same sanitizer with no component sign inversion.
  Custom's camera matrices and sanitizer fallback both use the saved
  non-jittered projection; temporal jitter is not interpreted as scene motion.
- Change Custom's conservative maximum-motion setting to 256 pixels so its
  second-line defensive check does not discard the shared fallback.
- Leave PPv2 unchanged. It remains a diagnostic comparison backend and is not a
  planned user-facing quality mode; overriding its private motion input is not
  justified.
- Mark identity managed Unity matrices unavailable and move raw anchor capture
  to a dedicated addressable shader.

## Expected behavior

Healthy fast pans remain green in the decision view even when their coherent
camera motion exceeds 256 pixels. The launchpad field remains orange and is
replaced. Custom, DLAA, and FSR2 should all stay stable on the pad; PPv2 may
continue to expose the upstream defect.

## Resource and lifecycle impact

Custom reuses the sanitizer texture and 1x1 classifier already owned by the
temporal coordinator; only one backend remains active. The dedicated statistics
shader is diagnostic-only and owns one material released with the visualizer.
No new readback or managed allocation is added to the production frame path.
