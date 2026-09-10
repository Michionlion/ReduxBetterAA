# Unified AA ownership and architecture review

Scope: current working tree based on `34525bb`, including the existing TAA and
live-comparison work. This review covers renderer integration, not a new TAA
algorithm or vendor runtime. No game assemblies are edited on disk.

## Behavior

- Better AA settings now contain Supersampling and a persistent 125/150/175/200%
  scale. F10 exposes the same choice. Native AA modes and Off select 100%.
- Both stock graphics selectors become disabled navigation hints to Better AA.
  The stock saved render-scale profile is left intact.
- Supersampling uses Redux's public `RenderScalePresenter.SetRenderScalePercent`.
  Redux still owns the scene target, bilinear presentation, UI composition and
  coordinate conversion. Better AA does not duplicate those systems. Production
  scale is capped at Redux's 200% limit and the device texture-size limit.
- Redux supplies no supersampled scene target for main-menu or map views.
  Selection is saved there with a truthful Off fallback; no replacement camera
  stack is introduced. The flight presenter is disabled when leaving flight.
- Off means native, unfiltered scene rendering. Temporal hooks, histories, native
  contexts, jitter and the foliage override are released. MSAA and scene PPv2 AA
  remain explicitly Off while Better AA owns the controls. Unload is distinct:
  it restores the pre-claim state where the current value still matches our write.
- The foliage preference is retained while Off or Supersampling is selected, but
  its global shader override and draw reroute are disabled until AA is active.
- The requested map policy applies to Supersampling too. Map Off preserves the
  selected mode/scale and returns the map scene to native rendering.
  With map AA enabled, the unsupported supersampling path also falls back to Off.

Removed subsystems, their patches, UI/callbacks, report fields, capture code and
dedicated tests/documents are absent from the current implementation. General
scene depth/motion diagnostics remain. Capability schema is 24; motion-sign
capture schema is 4. Old local artifacts and Git history describe earlier builds.

Deleted runtime files (including their Unity metadata):

- `Code/Rendering/KspPhysicsRenderInterpolation.cs`
- `Code/Rendering/CloudTemporalGuard.cs`
- `Code/Patches/VolumeCloudRendererPatch.cs`
- `Code/Diagnostics/CloudDiagnosticCapture.cs`

Their integrations were removed from `ReduxBetterAAMod`, `TemporalCoordinator`,
`NvidiaDlaaBackend`, `RenderLifecyclePatches`, `BackendSettingsPanel`,
`Phase1ProbeService`, `BufferVisualizer`, `BufferImageWriter`, capability reports
and issue-report capture/archive code. Dedicated Lua scenarios, EditMode cases
and obsolete investigation documents were removed too. The external test harness
is a separate repository and is not part of Better AA's shipped assembly.

## Ownership changes

| State | Owner and release rule |
|---|---|
| Camera PPv2 mode and depth flags | `SceneCameraState`: shared by Off, spatial, PPv2 TAA and custom/vendor backends. Restore each property only if it still equals the applied value. Spatial/Off do not request depth or motion textures. |
| Projection, non-jittered matrix, transparent jitter | `CameraProjectionState`: also replaces PPv2's duplicated shared-camera restoration. Checks the applied matrix before restoring; duplicate same-frame calls do not add jitter twice. |
| FXAA/SMAA settings | `Ppv2SpatialAaBackend`: only touches the selected algorithm's settings, checks object identity and applied values, and does not allocate the other algorithm's settings. |
| PPv2 temporal settings | `Ppv2TaaBackend`: restores matching fields on the same settings object; releases a project-created, unmodified TAA object through PPv2's release method. |
| Supersampling scale | `RenderScaleOwnership`: original/applied values per presenter, lifecycle-only discovery, destroyed-owner pruning, conditional restoration. Detectable external scale writes suspend our AA rather than being repeatedly overwritten. Explicit mode selection can reclaim ownership. |
| Global motion shader | `VegetationMotionCompatibility`: exact Unity allowlist, conflict detection, current-shader verification before rerouting draws, conditional restore. Starts disabled. |
| Diagnostic motion shader | `BufferVisualizer`: refuses another owner's custom shader and restores only while its own shader remains installed. |
| Global MSAA | Mod entry point and stock-setting patches: hold the stock control at Off; unload restores captured samples only when the current value is still zero. |
| Stock settings components | `GraphicsSettingsPatches`: captures labels, descriptions and enabled state; restores recognizable claims on unload. |

The stock-control patch now hooks non-generic `ShowSubMenu`, restricted to the
graphics page. The former closed-generic builder patch did not capture the
controls in the tested player. This also covers pages constructed before mod
initialization. Reflection runs only when the page is shown.

This intentionally uses small state snapshots, not a mod arbitration framework.
Two owners writing exactly the same value cannot be distinguished. Arbitrary mods
that continuously rewrite the same renderer or replace image-effect order still
require integration testing. If an external render-scale owner takes over, Off
fallback releases our AA while preserving that owner's scale; it cannot also
promise a native-size external scene.

## Organization

| Area | Files and responsibility |
|---|---|
| Settings/entry point | `ReduxBetterAAMod`, `Configuration/UserSettingsPolicy`, `BackendSelection`: persistent choices, migration, wiring and shutdown. |
| Scheduling | `Rendering/TemporalCoordinator`: mode selection, map policy, frame/reset lifecycle and one backend registry. |
| Shared renderer state | `SceneCameraState`, `CameraProjectionState`, `RenderScaleOwnership`, `TemporalTextures`: narrowly scoped resource/state owners. |
| Algorithms | `Backends/`: each backend retains its algorithm/context contract. `SupersamplingBackend` composes the unfiltered scene owner instead of introducing another resolve. |
| Vendor APIs | Separate AMD/NVIDIA adapters retain their explicit reflected API contracts and cached execution delegates. |
| Render inputs | Camera discovery, jitter sequence, motion sanitation, exposure and depth mask remain independent of diagnostic capture. |
| Patches | Small adapters for stock controls and lifecycle events; no AA algorithm implementation in Harmony patches. |
| Diagnostics | UI, capability reports, buffer capture, explicit profiling and live comparison. These remain optional render consumers. |

The largest diagnostic file remains `BufferVisualizer`; a broad UI rewrite was
not required to make the ownership changes. The duplicate Off/spatial camera
claims and PPv2 projection code were worth combining because their safety rules
were identical. Vendor ABI implementations and TAA shader logic remain separate.

Paths in the tables are under `Assets/ReduxBetterAA/Code/`. New production files
are `Backends/SupersamplingBackend.cs` and `Rendering/RenderScaleOwnership.cs`.
`Tests/EditMode/OwnershipTests.cs` covers the shared claims, and the existing
`TemporalCoreTests`/`BetaDiagnosticsTests` cover policy and report contracts.
Player scenarios live in `tests/Visual/maintenance.lua`,
`tests/Visual/menu-settings.lua` and `tests/Quality/live-comparison.lua`, with
their diagnostic adapters beside them. `SPEC.md` and decision 0041 define the
new behavior. Existing uncommitted custom-TAA/comparison work is preserved.

## Live comparison

Comparison remains a diagnostic exception: two independent AA outputs render the
real scene stack twice. Normal AA is suspended and normal supersampling is
temporarily returned to native scale before comparison owns its output targets.
Stop resumes the requested normal mode, including Supersampling. Shutdown now
disposes comparison/capture first, then normal rendering, then global settings.

Other game effects still execute twice and do not have cloned histories. This is
visual inspection, not an isolated performance benchmark. The diagnostic's
existing 400% ceiling is distinct from the 200% production presenter ceiling.

## Verification

The pinned Unity build and 103 EditMode tests passed. Tests include exact state
restoration, displaced-owner preservation, replaced PPv2 settings, supersampling
bounds, native-scale transitions and idempotent cleanup.

Final installed DLL SHA-256:
`242F5B8397DE147FDA9ED81CB45FDFCC56FA27CD299A584492C93493CBF22FF4`.
SDK ZIP (`Deploy/ReduxBetterAA.zip`) SHA-256:
`80FC12C112F3FEF4EF83AD799076797458913021BC3D9743093BF56EB19D885B`.
The existing vendor runtime files were retained. Build-generated pipeline GUID
churn was reverted; there are no game-assembly edits or new vendor dependencies.

Player: Redux `0.2.9.0.104521-beta`, commit `d299b906`, Unity `6000.5.8f1`,
Windows, NVIDIA RTX 5070 Ti, 2560×1440, paused `launchpad-fly-safe-15` fixture.

| Final run | Result | Artifacts |
|---|---|---|
| Native settings | 21/21 assertions, 5 screenshots | `Artifacts/visual-20260910-134907/` |
| Maintenance | 268/268 assertions, 16 screenshots, 95.9 seconds | `Artifacts/visual-20260910-135008/` |
| Live comparison | 155/155 assertions, 12 mode pairs plus supersampling restoration, 31.7 seconds | `Artifacts/quality-ownership-20260910-134800/` |

Each harness report records zero errors/warnings. Maintenance checks all modes,
three switch cycles, forced texture loss/recovery, 125–200% actual flight target
dimensions, preserved stock scale, Off cleanup and map policy/fallback. Four
production issue ZIPs passed `tools/Test-IssueReport.ps1`: same-frame capture,
complete manifests and every file's size/SHA-256. Native UI and comparison
screenshots were inspected. The added map test initially expected stock map
supersampling; runtime target inspection and Redux's flight-view implementation
showed that assumption was wrong. The final test explicitly checks the fallback
and retained flight choice instead. No extra map renderer was added.

The harness counts cover its capture interval. Raw player shutdown still emits
an unattributed ComputeBuffer finalizer warning; another run included stock
`KSP.Map.CommNetLineRenderer.OnDestroy`. These do not establish a Better AA leak,
and the run is not proof that every engine/global allocation is released.
Stock format-fallback messages also remain in the raw logs.

Original persistent choices were compared against the pre-test backup and all
restored (DLAA M, sharpness 0.24, stability 0.99, foliage on, map AA off); the new
scale defaults to 150%. The test-only adapter was archived out of the game mods
directory. The installed build and source edits are uncommitted.

VAB supersampling has the stock presenter integration but was not exercised in
these player runs. No broad cross-mod, cross-GPU, frame-allocation benchmark or
long-duration stability certification is implied.
