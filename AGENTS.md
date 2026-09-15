# Working on Better AA

Read [README.md](README.md), [CONTRIBUTING.md](CONTRIBUTING.md) and
[docs/architecture.md](docs/architecture.md). Maintainer instructions take precedence.

- Preserve the rendering and ownership contracts in the architecture document.
- Run the relevant [standard checks](tests/README.md); report checks not performed.
  Rendering changes need in-game validation, not just stills or unit tests.
- Keep dependencies pinned. Dependency/build changes need a fresh-checkout build
  and package review. Do not modify installed game assemblies or physics.
- Keep downloaded tools, binaries, captures and one-off experiments outside source.
  Only validated complete release ZIPs and internal runtime build inputs may contain
  approved vendor DLLs and their notices.
- Update the existing documentation when behavior changes. Record design choices
  in the architecture document and past decisions in Git, not new evidence ledgers.
