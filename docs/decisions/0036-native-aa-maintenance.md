# Decision 0036: shared native-AA lifecycle and trustworthy input captures

Accepted for the requested maintenance review within existing Phase 2–4/native-AA
work. SPEC sections 7.5, 8, 11.2 and 12.2–12.4 apply. No upscaling is introduced.

- `SceneCameraState` owns reversible depth/PPv2 claims for Custom TAA, DLAA and
  FSR2. Shared/resolve aliases are claimed once. Per-render projection ownership
  stays separate from backend lifetime.
- `TemporalRenderHook` replaces the three identical components at the same
  camera component position. Backends retain their algorithms, native contexts,
  jitter and exposure conventions. An optional one-shot observer receives input
  immediately before execution; ordinary rendering creates no managed objects.
- Issue reports use that observer, rather than inferring image-effect order from
  `DefaultExecutionOrder`. Schema 2 records `inputStage` as
  `before-temporal-resolve`, `after-ppv2`, or `unavailable`. The other modes still
  capture after PPv2. A replaced hook produces a partial report, not a false pair.
- `TemporalTextures` centralizes validity, release and vendor output descriptors.
  Inputs must be correctly sized, live single 2D surfaces. Lost owned targets
  recreate their resource set; vendor output loss recreates the context too.
  Persistent outputs cannot inherit memoryless storage.
- One coordinator registry maps `BackendSelection` to its owner for selection,
  reporting and cleanup. Failed/throwing configuration releases partial camera
  ownership before the existing retry/Off policy runs.

Production shaders, motion-vector signs, samples, defaults, map/menu zero jitter,
native UI placement, optional vendor binding and exact-version foliage repair
are preserved. The stock-cloud observer remains observation-only; persistent TUS
cannot suspend DLAA. The original cloud-disappearance issue remains unresolved.

The render layer has no diagnostics dependency. Diagnostics owns the observer
and temporary output hook, detaching on completion, timeout and disposal. The
test adapter no longer patches DLAA execution to correct input reports.

`MaintenanceTests` covers camera restoration/rebinding/destruction, lost and
mismatched textures, persistent descriptors, partial setup failure, registry
identity, and a GPU red-input/green-output ordering/one-shot test.
`Run-VisualTests.ps1 -Scene Maintenance` exercises all public modes, repeated
switches, forced resource loss, map overrides and production issue ZIPs. The
usual `-Scene All` supplies comparable DLAA stills and sampled motion video.
See the [review and evidence](../maintenance-review.md).
