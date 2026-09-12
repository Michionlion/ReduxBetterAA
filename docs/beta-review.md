Historical 0.6.0 review. The maintainer accepted the terrain fix and approved
the MIT-licensed 0.6.1 public beta. Current scope and limitations are in the
[README](../README.md); [release packaging](distribution.md) supersedes the
full-runtime packaging proposal below. The results below describe earlier builds.

# 0.6.0 beta engineering review

Scope: production spatial AA, Custom TAA, managed DLAA, FSR2 Native AA, camera
ownership, lifecycle, diagnostics, configuration, shaders, packaging and visual
test integration. The existing foliage source repair is retained. This
review prepares a beta; it does not certify all scenes, hardware or visual quality.

## Findings addressed

| Area | Problem | Change |
|---|---|---|
| Projection state | Three implementations could diverge during interrupted frames | Shared `CameraProjectionState`, tested for perspective/orthographic restoration and duplicate calls |
| Diagnostics | Buffer visualizer also owned hundreds of AA-setting lines; report builder mixed snapshots and scheduling | Separate `BackendSettingsPanel` and `CapabilityReportBuilder`; named bindings replace 26 positional callbacks |
| Runtime failure | Custom TAA could continue reporting active after resource/execution failure | Failure latch, readable reason and Off fallback; explicit selection permits retry |
| Same-scene transitions | Load/revert/vessel changes could escape scene-change heuristics | Exact game-message subscriptions with deterministic release |
| Version-sensitive patches | Foliage shader needs explicit compatibility boundaries | Exact verified Unity allowlist |
| Debug collection | Users needed several separate captures with little provenance | Settings action/F10 ZIP, raw EXRs, PNGs, screenshot, settings/backend state, bounded logs, versions, frame IDs and file hashes |
| Capture correctness | Initial export shader could reuse a previous source binding | Explicit source property; GPU test verifies distinct EXRs and known-color PNGs |
| Capture lifecycle | Missing render/end-of-frame could leave capture armed | Timeout in Update, partial-report status, hook/panel restoration, worker-only compression |
| Input/UI | Forced F12 conflicted with screenshots; diagnostic panel obscured by scene content | Optional mode-cycle key, configurable diagnostic hotkeys, opaque panel content |
| Normal-play logging | Repeated hitch formatting complicated performance diagnosis | Frame-hitch trace disabled by default; structured explicit profiler remains |
| Test integration | GPU reads exceeded short RPC timeout; Redux 2.9 camera ABI changed; shutdown raced next launch | Configurable harness response timeout, rebuild against target, wait for exit |
| Distribution | Ad hoc local runtime ZIPs lacked a repeatable release contract | SDK build/test script plus constrained game-root packager, target-specific runtimes, notices and hash manifest; Redux excluded |

## Complexity retained deliberately

The NVIDIA and AMD bridges remain separate. They bind different optional managed
APIs, boxed native structures and by-reference execution contracts. Their cached
delegates avoid `MethodInfo.Invoke` allocations in rendering. A generic reflective
vendor framework would obscure these ABI requirements; extraction here would
trade explicit code for harder debugging.

Camera discovery, motion sanitation and vegetation draw submission also remain
explicit. They encode measured scaled/physics-camera behavior, UV conventions,
indirect draw semantics and ownership. Changes to those algorithms require
controlled render evidence, not a line-count reduction. The remaining large
visualizer houses engineering buffer/burst/statistics tools; its AA controls and
snapshot construction have been removed without inventing a UI framework.

No new native bridge, resolution scaling, reconstruction algorithm or automatic
dependency updater is introduced. Diagnostics perform GPU reads/reflection/file
work only on an explicit capture; compression never calls Unity off-thread.

## Validation record

Local final galleries:
[Redux 2.9](../Artifacts/visual-20260907-105941/index.html),
[Redux 2.8.5](../Artifacts/visual-20260907-110326/index.html).
Each contains a `reports` directory with the three validated ZIPs. First-run
Redux online-service choices remain untouched; that dialog is visible in 2.9
menu screenshots. The opaque Better AA panel and flight scene/UI were inspected.
No cross-GPU or long-session stability claim is implied.

The [portable 2.9 menu run](../Artifacts/visual-20260907-110741/index.html) also
passes with all three optional native files temporarily absent: only Off, FXAA
Low/High, SMAA and TAA are offered. The runtime files were restored afterward.
Original user settings (DLAA/M, sharpness 0.24, stability 0.99, foliage repair on,
map AA off) were restored from the pre-test backup. Test adapters and stale 0.5.28
PDBs were archived outside the game after validation.

Final SDK ZIP SHA-256:
`0b7854e22f44e03d6ab74ecbbacd804018214f85cb7c2ee5e2d4a5116d925328`.
Three prepared archives under `Deploy` provide full 2.8.5, full 2.9 and portable
variants. Each full ZIP is about 30 MB; neither contains Redux or test binaries.
They remain explicitly labelled **local-only**, pending release licensing.

The launcher applied the 2.9 update to the primary Steam installation despite a
named-install argument. The unchanged copy at `G:\KSP2\_diagnostics\Redux29` holds
the 2.8.5 regression player (the directory name reflects its original intended
use). The main game now runs 2.9. All native runtime files were matched to each
player, and the original mod was backed up before installation.

## Remaining work recorded for the 0.6.0 review

The maintainer must visually accept the generated material and inspect Redux's
native settings layout. Extend the existing harness to VAB/KSC, orbit/low-altitude,
quickload/revert, docking/vessel change, origin rebase, time warp, resolution
changes, repeated switching and non-NVIDIA hardware. Measure steady-state
allocations, GPU cost and long-session resource growth outside capture runs.

Choose the original-code license and supply reviewed runtime redistribution
provenance/notices before publishing a full package. Local validation archives
are marked accordingly. See [distribution](distribution.md) and
[visual testing](visual-testing.md) for reproducible commands and acceptance checks.
