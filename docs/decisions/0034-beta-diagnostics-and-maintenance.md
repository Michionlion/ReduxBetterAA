# Decision 0034: beta diagnostics and maintenance

Accepted for 0.6.0 beta preparation, within existing Phase 2–4/native-AA paths.
No upscaling algorithm or custom vendor SDK is introduced. Decision 0035 adds
Redux 2.9 compatibility at the maintainer's request.

- One `CameraProjectionState` implementation owns projection restoration for
  Custom TAA, DLAA and FSR2. The existing sample sequence and transparent-jitter
  policy are retained.
- `CloudTemporalGuard` isolates Decision 0033's 120-observation state machine
  from NVIDIA execution and has exact entry/exit/reset tests. Schema 23 reports
  bypass state, remaining frames, resize count and dimensions.
- `BackendSettingsPanel` owns AA controls and their named bindings.
  `CapabilityReportBuilder` constructs snapshots. Neither installs render hooks.
- `IssueReportCapture` owns temporary before/after-AA hooks (orders 9999/10001),
  all one-shot buffer reads and the end-of-frame screenshot. Production AA
  hooks retain order 10000. The input for spatial/Off is the post-PPv2 scene;
  vendor/private internal histories are unavailable. Exported EXRs are sampled
  float copies, not RenderDoc captures or native byte-for-byte resource dumps.
- Each report is isolated in a unique directory. Compression starts only after
  image writes finish; a ZIP is published by renaming a completed `.partial`.
  A manifest identifies frames, errors, unavailable surfaces and SHA-256 hashes.
  Failure never prevents the render hooks from forwarding scene color.
- F10 creates a ZIP, Shift+F10 preserves the old screenshot path, Ctrl+F10 opens
  the panel. The normal settings page also offers the report action. Diagnostic
  hotkeys can be disabled and mode cycling is optional rather than forced F12.
- Custom TAA now latches runtime failure and selects Off, as the vendor paths
  already did. Vendor duplicate coordinator flags are removed. Explicit game
  load/revert/vessel messages invalidate same-scene history and camera discovery.
- The exact Unity foliage shader is restricted to verified engine versions (see
  Decision 0035). The optional
  stock-cloud observer checks its private-field contract before patching.

The capture suite is an optional test-only plugin plus Lua scripts in this repo.
It drives the normal configuration callbacks, restores its original settings,
and produces images and sampled-frame video sequences for human review.
It is excluded from release payloads. Public release still requires the scene,
hardware, resource/performance and redistribution evidence in the beta review.
