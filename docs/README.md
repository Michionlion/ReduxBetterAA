# Developer documentation

Start with [building and publishing](building.md), [release contents](distribution.md)
and the [engineering specification](../SPEC.md). The [0.6.1 audit](release-audit.md)
records packaging decisions. Player instructions live in
the root [README](../README.md) and [native-library guide](../NATIVES.md).

Current test workflows:

- [Terrain flicker regression](terrain-flicker-testing.md), with the
  [cause, candidate ledger and measurements](decisions/0044-northwest-hills-flicker-investigation.md).
- [TAA pixel and motion quality](taa-quality-testing.md).
- [Visual and mode/lifecycle checks](visual-testing.md).
- [Performance profiling](performance-profiling.md).
- [AA ownership and supersampling](aa-ownership-review.md).

The numbered decision records and dated reviews are development history.
Their version numbers, test counts and pending work describe those builds;
the current release's status is in its release notes. Phase 5's DLSS upscaling
design remains a proposal. The closed 50 Hz motion investigation is retained
as history and is separate from the fixed terrain flicker.

Links into `Artifacts/` refer to generated local evidence. These captures and
private save fixtures are intentionally not in Git. Reproduce them with the
documented harness; selected public images are kept in `docs/images`.
