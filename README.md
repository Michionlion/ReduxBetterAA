# Redux Better AA

Experimental anti-aliasing for KSP2 Redux. **0.6.0 beta preparation**, targeting
Redux **0.2.8.5 / Unity 6000.4.1f1** and **0.2.9.0 / Unity 6000.5.8f1**, Windows x64.
Use the runtime bundle matching your Redux version. Public release acceptance
is tracked in [beta review](docs/beta-review.md).

## Install and configure

1. Install the matching Redux build first. Redux supplies the mod loader and
   managed dependencies; Better Clouds is not required.
2. Close KSP2 and extract the release archive into the game root. The mod must
   be at `mods/ReduxBetterAA/swinfo.json`, with only one copy of that mod ID.
3. Open **Settings > Mods > Redux Better AA** and select a mode. Fresh installs
   start at **Off**, which disables scene AA including stock MSAA/PPv2.

Modes are Off, FXAA Low, FXAA High, SMAA, TAA, and hardware/runtime-supported
NVIDIA DLAA and FSR 2 Native AA. A missing vendor runtime hides its mode.
Sharpness is shared across reconstructing modes; zero disables it. TAA stability
controls stationary history. DLAA defaults to M. Foliage motion repair and
map-view AA are independent persistent switches. Advanced controls are
session-only and available in **Ctrl+F10**.

Full runtime bundles additionally contain `NVUnityPlugin.dll`, `nvngx_dlss.dll`
and `AMDUnityPlugin.dll` beside the game executable. Portable bundles omit
these. Archives labelled **local-only** are internal validation artifacts and
must not be published. Installation, update and dependency provenance details:
[distribution guide](docs/distribution.md).

## Report a rendering issue

Keep the issue visible and press **F10**, or select **Generate issue report ZIP**
in the normal mod settings or the Ctrl+F10 panel. The settings action immediately
resets itself. Buffer capture can pause rendering for several seconds.

A notification identifies the completed ZIP and offers **Open reports folder**.
Reports are written under `mods/ReduxBetterAA/diagnostics/reports`. Send the ZIP
with a description of what happened and what you expected. Nothing is uploaded
automatically. Review the images first: visible UI and user-chosen vessel names
can appear. Save files and full player logs are not bundled.

Each ZIP includes:

- The presented screenshot and scene input/output at the AA boundary.
- Floating-point EXRs and PNG previews of available depth, raw and sanitized
  motion, owned histories, masks and stock cloud targets, including alpha views.
- Camera graph, settings, requested/selected backend, fallback, cloud-bypass
  state, versions, GPU details, mod inventory and assembly hash.
- A bounded Better AA log excerpt and a manifest with frame numbers, file
  hashes, missing buffers and capture failures.

Vendor-internal history is opaque. Absent, uncreated or non-2D buffers are listed
as unavailable rather than represented by invented images. A partial capture is
labelled explicitly. Source files are retained beside the ZIP for inspection;
old reports can be removed manually when no longer needed.

**Shift+F10** keeps the individual screenshot/cloud-capture workflow.
**Ctrl+Alt+F8** writes a standalone capability report. Diagnostic hotkeys can be
disabled in settings. Mode cycling has an optional key and is disabled by default
so it does not claim Steam's F12 shortcut.

## Visual validation

From a checkout with the test harness installed and the new beta deployed:

```powershell
pwsh -NoProfile -File tools/Run-VisualTests.ps1
```

This builds/installs a small test-only adapter and captures ten screenshots:
three native settings-page views, six launchpad angles, and one map view, all
using DLAA M, plus a six-second sampled DLAA camera-pan MP4. Flight requires
FFmpeg and streams video frames directly to the encoder without retaining PNGs.
It bundles the gallery, video and test reports into a single ZIP under
`Artifacts`, without developer-panel captures or large buffer dumps. The game is
launched by the harness. Flight uses the local `launchpad-fly-safe-15` fixture;
see [visual testing](docs/visual-testing.md) for requirements and manual checks.
Images are review material, not an automated assertion of visual quality.
Remove `mods/ReduxBetterAAVisualTests` after testing.

## Build and architecture

```powershell
pwsh -NoProfile -File tools/Build-Beta.ps1
```

Uses the pinned Unity editor, EditMode tests and SDK/ThunderKit zip pipeline.
Then use [Package-Beta.ps1](tools/Package-Beta.ps1) to assemble the game-root
archive with reviewed dependencies, notices and a hash manifest. The public
packager requires the project license and, for vendor files, a reviewed runtime
receipt. It does not download binaries or include Redux.

- [Architecture and requirements](SPEC.md)
- [Beta review and remaining acceptance work](docs/beta-review.md)
- [Maintenance decision](docs/decisions/0034-beta-diagnostics-and-maintenance.md)
- [Previous implementation history](docs/history-through-0.5.28.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

Known limitations: map/menu reconstruction uses zero projection jitter; PPv2 is
an engineering comparison only. The stock-cloud DLAA suspension guard is disabled:
it could leave ordinary flight without AA indefinitely. DLAA now continues through
stock cloud temporal transitions; the original cloud-disappearance issue remains
unresolved. The stock renderer can retain lower-resolution clouds. Foliage repair is tied to the exact
Unity version above. FSR2 requires native 100% render scale. These modes do not
implement DLSS Super Resolution, Frame Generation or Ray Reconstruction.
