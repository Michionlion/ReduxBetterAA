# Decision 0027: choose a source repair for indirect vegetation motion

## Status

Accepted for the 0.5.24 Redux 2.8.5 comparison build and refined in 0.5.28.
The maintainer selected choice B as the default source repair and requested
independent Ctrl+F10 toggles for choices B and E. A sparse invalid object pass
remaining after B is now excluded with C's exact proven predicate as part of
the B lifecycle. Redux 2.9 snapshot compatibility remains unverified.

## Scope and compatibility baseline

Decision 0026 identifies the artifact as invalid object motion written by
`VegetationSystemPro.RenderVegetationItemLODIndirect`. The affected direct
branch uses the obsolete Built-in Render Pipeline
`Graphics.DrawMeshInstancedIndirect` API. Its leaf shader obtains current
instance placement from GPU buffers, while Unity's replacement object-motion
pass sees an identity previous object matrix and a camera-centred current
matrix.

The source repair was prototyped against the installed Redux
`0.2.8.5.103184-beta` player, Unity `6000.4.1f1`, on D3D12. As of 2026-08-28,
Redux's current 2.9 snapshot is `0.2.9.0.104355` (26w35b). Its public release
notes do not identify a vegetation or motion-vector change; 26w33a upgraded the
player from Unity 6000.5.0 to 6000.5.8. The public snapshot SDK likewise exposes
no replacement for this private game renderer. Therefore a snapshot upgrade by
itself is not evidence of a fix and must pass the same raw-buffer reproduction.

The preferred API, `Graphics.RenderMeshIndirect`, and
`RenderParams.motionVectorMode` exist in both Unity lines. A clean source
repair therefore does not require upgrading Redux.

## Runtime candidate result

A Test Harness-only Harmony prefix reconstructed the direct branch with the
same camera, layer, bounds, material property block, visible-instance buffer,
per-submesh indirect argument buffers, shadow mode, and light-probe policy. It
changed the submission to:

```text
Graphics.RenderMeshIndirect(...)
RenderParams.motionVectorMode = MotionVectorGenerationMode.Camera
```

At the deterministic failing pose this removed the radial object-motion field,
leaving the correct neutral stationary camera pass. A second test compared the
legacy and rerouted final color at four low-horizon yaw angles with the KSC tree
line visible. It rerouted 975 draw calls without missing vegetation, obvious
material changes, or new report warnings/errors. The paired stills are visually
equivalent; their 39.5--49.1 dB whole-frame PSNR is consistent with captures
eight frames apart while wind and UI continue updating, not a synchronized
render-target equality test.

This proves the API and camera-motion policy on stable Redux. It does not yet
prove all planets, shadow-only camera command buffers, origin rebases, animated
wind quality under a temporal backend, performance, or 2.9 compatibility.

## Viable choices

| Choice | Ownership | 2.8.5 | 2.9 snapshots | Raw buffer | Main tradeoff |
| --- | --- | --- | --- | --- | --- |
| A. Migrate the vegetation draw upstream to `RenderMeshIndirect` with camera-only motion | Redux/game renderer | Prototype passes | API available; runtime retest required | Correct at source | Best architecture, but requires an upstream/core change |
| B. Apply the same draw migration from Better AA with a guarded Harmony patch | Better AA | Prototype passes | Version/signature adapter and retest required | Correct at source | Available immediately, but couples the mod to a private renderer method |
| C. Conditionally discard only invalid object-history draws in a replacement Built-in motion shader | Better AA/Unity global shader | Prototype passes exact artifact | Requires a separate exact 6000.5.8 shader asset and validation | Correct after object pass exclusion | Avoids renderer-method patching, but globally owns a Unity built-in shader |
| D. Add a dedicated vegetation motion pass with current and previous GPU instance state | Redux/game renderer | Feasible | Feasible | Fully correct, including real object motion | Highest engineering, memory, shader, and validation cost |
| E. Keep the current post-process sanitizer and camera fallback | Better AA | Already available | Expected to port | Sanitized consumers only | Lowest integration risk, but thresholds can reject valid fast motion and PPv2/raw remain wrong |

### A. Upstream draw migration

This is the recommended clean repair. Static vegetation should not overwrite
the full-screen camera motion with an object vector. Camera-only motion makes
the object pass agree with camera reprojection while retaining ordinary color,
depth, and shadow submission. The change belongs beside the instance-buffer
owner in Redux/Vegetation Studio.

Before acceptance, extend the direct-path prototype to the command-buffer
shadow branch or confirm that branch never contributes motion, then test:

- all KSC directions and altitudes, another Kerbin vegetation biome, and a
  non-Kerbin body with vegetation;
- camera movement, vessel movement, wind, LOD transitions, and origin rebases;
- final color, depth, raw motion, shadow maps, and DLAA/FSR/TAA output;
- CPU/GPU frame time, draw count, steady-state allocations, and teardown;
- both Redux 2.8.5 and the selected 2.9 snapshot.

If camera-only wind motion produces visible leaf ghosting, do not return to the
invalid legacy object pass. Escalate to choice D for the affected materials.

### B. Better AA guarded renderer patch — selected

This performs the same correct submission on 2.8.5 without waiting for Redux
core. It should be isolated in a versioned compatibility component, verify the
target method signature and private field layout once, fail closed, and leave
the sanitizer active when unsupported. It must not format, reflect, or allocate
per draw after warm-up.

This is the practical fallback if upstream integration cannot land soon. Its
cost is maintaining private API adapters for stable and snapshots. It should be
removed when Redux owns the repair.

The 0.5.24 implementation resolves the exact method signature and expected
private buffer-ID fields once during initialization, patches the direct branch
only, and fails closed to the original draw when unsupported. It reuses the
renderer-owned buffers, materials, property block, bounds, camera, layer,
shadow policy, and argument buffers without per-draw reflection or managed
allocation. It is enabled by default on the validated renderer. Decision 0032
adds an exact object-history exclusion required by Unity's replacement pass;
neither half runs alone in production.

### C. Exact invalid-history object-pass exclusion

The diagnostic motion shader proved two facts: discarding the entire object
pass preserves correct stationary camera motion, and a narrower predicate that
matches identity `_PreviousM` plus a current object translation at the camera
removes only the reproduced bad draw.

A production form would have to be an exact, audited copy of Unity's built-in
motion shader for each player revision. Replacing
`BuiltinShaderType.MotionVectors` is global, can conflict with other mods, and
can silently drift when Redux changes Unity. This is a credible compatibility
fallback, not the preferred owner-level fix.

### D. True per-instance vegetation history

This is the highest-fidelity option when wind-deformed leaves or moving
instances require their own vectors. The renderer would double-buffer current
and previous per-instance transforms (and any vertex-animation history), render
a dedicated motion pass, and explicitly handle LOD changes, spawning/culling,
camera-relative coordinates, and origin rebases.

It is disproportionate for static KSC trees unless temporal tests show a real
camera-only quality failure. It is an upstream renderer project, not a Better
AA patch.

### E. Sanitizer as optional defense in depth

The sanitizer remains useful for unsupported builds and unrelated invalid
producers. It should not be the primary fix for a producer whose exact ownership
is known. In 0.5.24 it is disabled by default and can be enabled independently
from Ctrl+F10's Buffers tab. Disabling it bypasses rejection and camera
substitution while retaining the fixed component-sign conversion required by
the vendor inputs.

## Rejected or non-solutions

- **Upgrade to 2.9 alone:** no published renderer fix; Unity version movement
  changes compatibility risk but does not supply missing previous instance
  history.
- **Invert X/Y:** changes vector signs and cannot repair an identity previous
  object matrix.
- **Set `Renderer.motionVectorGenerationMode`:** indirect graphics submissions
  have no `Renderer` component.
- **Overwrite the entire motion texture with camera motion:** destroys valid
  vessel, part, and other object motion.
- **Disable vegetation, its layer, or the leaf material:** removes visible
  content instead of repairing motion.
- **Change render queues/tags to dodge replacement rendering:** risks color,
  depth, deferred, shadow, and sorting behavior and leaves ownership implicit.
- **Keep `DrawMeshInstancedIndirect` and add a conventional `MotionVectors`
  pass:** Unity's Built-in indirect path is the documented limitation; without
  an explicit draw or supported submission API this does not provide coherent
  previous per-instance state.
- **Move the project to URP, BatchRendererGroup, or GameObjects/Renderers:** each
  is a renderer migration far larger than the verified `RenderMeshIndirect`
  change and has no demonstrated benefit for this defect.

## Selection

Ship B enabled by default on Redux 2.8.5 and expose B and E as independent
engineering toggles. Keep E disabled by default for the first comparison so
the renderer-level result can be evaluated without sanitizer intervention.
The longer-term recommendation remains A: move the same repair upstream and
remove Better AA's private renderer patch when Redux core owns it. Choose C
only if private-method patching becomes unacceptable, and reserve D for a
demonstrated wind/animation quality problem.

## Verification records

- Source-solution comparison: Test Harness run `5abd663a9bf6`.
- Vegetation-visible coverage: Test Harness run `977959225fab`.
- Production B/E comparison: Test Harness run `1c774870b64e`. With B on and
  E off the raw texture is neutral at the deterministic failing pose; turning
  B off restores the radial control artifact. The run completed 8/8 assertions,
  recorded 4,419 rerouted calls, and restored B on/E off at teardown.
- Redux 2.8.5 environment: KSP2 0.2.3.0, Unity 6000.4.1f1, D3D12,
  NVIDIA GeForce RTX 5070 Ti.
- Redux 2.9 status was evaluated from the official beta manifest, release
  notes, public SDK tag 26w35a, and snapshot template. It has not yet been
  installed or runtime-tested in this investigation.

## Primary references

- Redux beta manifest:
  <https://raw.githubusercontent.com/KSP2Redux/Redux/main/manifest-beta.json>
- Redux snapshot 26w35b release notes:
  <https://github.com/KSP2Redux/Redux/releases/tag/v0.2.9.0.104355>
- Redux snapshot 26w33a Unity-upgrade notes:
  <https://github.com/KSP2Redux/Redux/releases/tag/v0.2.9.0.104139>
- Unity `Graphics.RenderMeshIndirect`:
  <https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html>
- Unity `RenderParams.motionVectorMode`:
  <https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RenderParams-motionVectorMode.html>
- Unity Built-in indirect-motion limitation:
  <https://issuetracker.unity3d.com/issues/motionvectors-light-mode-pass-is-not-invoked-when-using-drawmeshinstancedindirect>
