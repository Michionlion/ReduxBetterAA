# Live AA comparison validation — 2026-09-10

Installed DLL SHA-256: `79E3DE8B719AECE8AC1BEB35190147E0E7064E99304F78890AA6722D058BA6CF`.
Source: working changes on `34525bbeeaa5c12b8f42c4c529db9d65b43500b1`.
Supported build pipeline: `tools/Build-Beta.ps1`, Unity 6000.4.1f1.

Runtime: Redux 0.2.9.0.104521-beta (d299b906), Unity 6000.5.8f1, D3D11,
RTX 5070 Ti, 2560×1440. Fixture: `local/launchpad-fly-safe-15`, vessel Fly Safe-15,
camera distance 45, pitch 35, FOV 55, orbit yaw changes while comparison runs.
Normal settings: DLAA M, sharpness 0.24, TAA stability 0.99, foliage repair enabled,
map AA disabled. The test restores saved settings and removes the test adapter.

`tools/Build-Beta.ps1`: **83/83 editor tests passed**, including five new GPU split
and dimension-bound cases. The shader tests verify divider endpoints, matching
full-frame coordinates, Y orientation, and different source resolutions.

`tools/Run-QualityTests.ps1 -LiveComparison -Label live-verified2`:
**153/153 assertions passed**, 15 screenshots, zero suite errors or warnings.
Run: `Artifacts/quality-live-verified2-20260910-075929`.
Twelve pairs exercised TAA/DLAA, TAA/FSR, None/SSAA at 200%, 300% and 400%,
DLAA/300% SSAA, TAA/TAA, DLAA/DLAA, FSR/FSR, PPv2/PPv2, FXAA/SMAA and PPv2/None.

The tests confirmed separate backend/target ownership, created vendor contexts,
valid persistent motion-matrix history, and actual use of custom TAA history.
Ordinary orbit movement did not reset the histories. Same-mode TAA passes measured
identical nonzero motion magnitudes during three pans (approximately 133.88, 66.68,
66.60 pixels), checking that replay does not lose the second pass's motion vectors.
Both outputs contained finite, nonconstant scene pixels. Actual SSAA dimensions
were 5120×2880, 7680×4320 and 10240×5760. UI remained native and correctly oriented
in inspected screenshots. Comparison also continued with simulation unpaused.
Stop restored DLAA; entering map view stopped comparison. The open menu left the
game EventSystem enabled. Menu, dropdown, split and supersampling screenshots were
visually reviewed.

The test deliberately checks renderer history state rather than the optional
global Unity-matrix diagnostic, which is unavailable in this render hook. During
development it caught a history-reset ordering defect: checking a temporarily
disabled camera invalidated history every frame. That ordering was corrected and
explicit accumulation/reset assertions now guard against recurrence.

Scope: launchpad flight rendering, orbit changes, a short unpaused interval and map
transition. VAB, orbital atmospheric/cloud-heavy flight, resolution changes, and
extended sessions were not runtime-tested in this pass. Stock cloud/exposure
histories are shared game effects and execute twice; this feature is for interactive
AA inspection, not isolated ground-truth or performance measurement.

## Divider follow-up

Added a three-display-pixel divider: white center, black borders, following the
split slider and omitted at 0%/100%. Supersampling does not change its width.
85/85 editor tests and 153/153 live assertions passed. Pixel readback of the
2560x1440 gameplay screenshots verified exact black/white/black RGB values at
the divider for both native AA and 400% supersampling. The real display-width
GPU case guards against derivative precision moving the line between pixels.
Run: `Artifacts/quality-divider-final-20260910-083429`.
Updated installed DLL: `E55164E8338B810DE9C7357EB758714218AFFD8FC96CB63D4E4EEB320848A929`.
Updated archive: `A362C5C05546F1D4F03C5886F636443AE01F072B4D14D822661075DE5D6B3521`.
