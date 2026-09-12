# Working on Better AA

Read README.md, CONTRIBUTING.md and SPEC.md before changing the mod.
Explicit maintainer instructions take precedence over these documents.

- Keep scene color, depth, motion, jitter and reset state coherent across cameras.
- Run one temporal backend. Failure falls back to Off.
- Keep UI and map icons outside temporal history.
- Allocate no managed memory per frame after warm-up. Cache lookups and resources.
- Restore only state the mod still owns. Release resources on mode changes,
  scene teardown and shutdown; cleanup must be idempotent.
- Use runtime patches, never modify installed game assemblies or physics.
- Verify rendering changes in the game. A screenshot or unit test alone cannot
  establish temporal stability or performance.
- Run the standard checks in tests/README.md. Report checks not performed.
- Keep downloaded tools, game assemblies, captures and one-off experiments out
  of this repository. Keep lasting regression tests; put investigation scripts
  and evidence in an external working directory.
- Keep SDK/editor/package versions pinned. Dependency changes need a build
  from a fresh checkout and review of the resulting package.
- Do not ship game or vendor libraries. Preserve third-party notices.
- Update current documentation when behavior changes. Use Git history for old
  decisions; do not create phase ledgers or evidence archives in the repository.
