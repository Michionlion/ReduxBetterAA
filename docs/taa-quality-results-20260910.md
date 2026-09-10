# Custom TAA quality and cost review — 2026-09-10

The candidate fixes specific history/edge/halo failure modes and reduces resolve
cost and memory. It does **not** establish an overall quality win over the old
TAA or over DLAA/FSR in all scenes. Retain that distinction when reviewing the
pixel scores below.

## Implementation review

The original backend copied every resolve into a separate history target and
used 16 reads for Catmull-Rom. It also sampled uninitialized history on reset,
raised moving-edge history toward .99 even when the configured motion weight
was .10, and allowed an adjacent depth outside the central reconstruction
footprint to validate stale color. Unbounded sharpening could overshoot a
step edge; alpha accumulated old coverage. Decision 0037 describes the fixes.

These changes retain the existing renderer hook, jitter conventions, sanitizer,
vendor modes, cloud policy and settings. No vendor libraries were changed.

## Environment and evidence

- Game: Redux `0.2.9.0.104521-beta` (`26w36c`, `d299b906`), Unity `6000.5.8f1`.
- GPU: NVIDIA GeForce RTX 5070 Ti, driver `610.88`; D3D11, native 2560x1440,
  render scale 100%.
- Fixture: `local/launchpad-fly-safe-15`, paused, fixed camera poses.
- Custom stability .99, all sharpness zero; DLAA M; FSR2 Native AA.
- Reference: Off at 5120x2880, four frames, linear 2x2 downsample.
- Baseline installed DLL SHA-256:
  `d984d1496aacc9584c589fb1d8756953e9fb7aa40dc25073ac36ab1cfa8303db`.
- Candidate installed DLL SHA-256:
  `63febfbbec6e4b74c2b7dfffb3b4787f3dd47737d6629d8f43ba8c86f2eb66ef`.
- Source base: `34525bbeeaa5c12b8f42c4c529db9d65b43500b1`; candidate is the
  uncommitted working-tree change, identified by its installed binary hash.

Both controlled runs passed all 12 runtime assertions, with zero harness-reported
errors/warnings. Each captured 124 frame pairs (248 finite RGBA buffers), with
exact frame IDs and SHA-256 manifests. The preliminary native-only run also
passed but is not used in the controlled reference comparison. One intervening
launch attempt encountered a player still exiting; it produced no capture and
is excluded.

Local artifacts (generated, ignored by git):

- `Artifacts/quality-baseline-reference-20260910-005844/`
- `Artifacts/quality-candidate-20260910-010028/`
- `Artifacts/quality-review-20260910/index.html` and `comparison.json`
- `Artifacts/quality-gpu-cost/cost.json`

## Pixel results and tradeoffs

Both versions below use the **same baseline spatial reference**. Comparing each
against its own separately timed reference would introduce reference drift;
the unchanged FSR/DLAA controls demonstrate why this matters. Lower RMSE and
higher PSNR indicate closeness to this reference, not a universal AA ranking.

| Output | Linear RGB RMSE | PSNR (peak 1) | Structure ROI RMSE |
|---|---:|---:|---:|
| Original Custom TAA | 0.025231 | 31.961 dB | 0.034810 |
| Candidate Custom TAA | 0.025395 | 31.905 dB | 0.034921 |
| FSR2 Native AA, candidate run | 0.026713 | 31.466 dB | 0.036672 |
| DLAA M, candidate run | 0.027878 | 31.095 dB | 0.039761 |

Candidate TAA's reference RMSE increases **0.65%** (structure ROI **0.32%**).
Stationary temporal standard deviation increases from **0.001547 to 0.001563**
(**1.0%**); settle variation increases from **0.002064 to 0.002073** (**0.45%**).
This is a small measurable stability/detail tradeoff, not a demonstrated
stationary quality improvement. FSR/DLAA outputs stay effectively unchanged.
The inspected crops also show DLAA preserving sharper fine structures than
Custom despite the full-image reference score favoring Custom. Smoothing and
render-scale differences can affect this metric; inspect the images.

The actual quality improvements established by the GPU fixtures are narrower:
reset/poisoned history cannot contaminate the output, current alpha remains
current, sharpening cannot invent step-edge halos, out-of-footprint depth no
longer accepts stale color, and a fast edge respects .10 history instead of
restoring .99. A 32-frame analytic stationary edge has unchanged MSE
`0.0003997597013`. Fractional nine-read versus 16-read HDR filter differences
peak at `0.006596`; integer-phase output matches exactly.

## Cost and allocation results

The editor microbenchmark used native-size persistent textures, alternate ABBA
arm order, four 32-frame batches per arm, and one synchronous readback fence per
batch. It measures complete resolve-pipeline GPU+CPU wall cost, not hardware
GPU timestamps or gameplay FPS. Unity editor version was `6000.4.1f1`.

| Metric | Original | Candidate |
|---|---:|---:|
| Median synchronized synthetic pipeline | 0.3004 ms | 0.2595 ms |
| In-game median whole frame, three 240-frame windows | 7.7094 ms | 7.6765 ms |
| In-game median resolve CPU submission | 0.03058 ms | 0.02921 ms |
| In-game median P95 hook CPU | 0.0377 ms | 0.0372 ms |
| Backend-owned histories at 1440p | 112.5 MiB | 84.375 MiB |
| Managed bytes in 720 measured render calls | 0 | 0 |

Synthetic pipeline cost fell **13.6%**, with every measured paired batch faster.
Owned memory fell **25%**, or **28.125 MiB** at 1440p. One full-screen copy and
one color target are eliminated. The in-game whole-frame delta is only **0.43%**,
and the unchanged FSR/DLAA timing controls also improved between runs, so there
is no defensible gameplay FPS gain claim. The player reported no GPU timing
samples; isolated GPU duration remains unavailable. Allocation measurements
cover the backend render call, not all game scripts or the full coordinator.

## Validation and remaining scope

The supported Unity/ThunderKit pipeline built the candidate package. The full
EditMode suite passed **77/77**, followed by the added isolated cost fixture
**1/1**. Four Python analysis fixtures passed, covering known pixel errors,
identity PSNR serialization, raw orientation/HDR preservation, truncation and
nonfinite alpha rejection. Both complete raw runs were rechecked for finite
RGBA data. User settings were restored and the quality adapter removed.

The installed candidate additionally passed **113/113 maintenance assertions**
and produced 11 screenshots: all public modes, three switch cycles, forced
loss/recreation of TAA/DLAA/FSR2 textures, map override and four production issue
reports. This specifically exercises recovery after removing the extra Custom
color target. The run is `Artifacts/visual-20260910-011149/`; it reported zero
errors/warnings. Its temporary adapter was also removed after testing.
All four diagnostic ZIPs were complete and passed file-hash and same-frame
validation; unavailable optional buffers remained explicitly marked.

The candidate is installed in the tested game folder. The previous DLL and
Addressables are preserved under `Artifacts/quality-deployment-backup/`.
Broader VAB/orbit/high-speed flight/exhaust testing and hardware GPU pass timing
remain outside this evidence. No public beta acceptance or universal quality
superiority is claimed.
