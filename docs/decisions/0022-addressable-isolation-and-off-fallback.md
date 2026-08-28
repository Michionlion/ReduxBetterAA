# Decision 0022: Addressable isolation and Off failure fallback

## Status

Accepted for the 0.5.21 comparison build. Unity compilation, focused EditMode
tests, cross-mod bundle loading, and main-menu runtime initialization pass;
representative in-player visual verification remains required.

## Context

After Redux Better Clouds was split from this project, both mods could load
their catalogs but the mod loaded second lost every shader-backed feature. The
runtime log reported that Better AA's bundle could not load because another
bundle with the same files was already loaded. The external bundle names and
content hashes differed, so the filename was not the collision.

The two projects were cloned with the same Unity product GUID, Addressables
group GUID, and `GroupGuidProjectIdHash` internal-ID policy. Unity therefore
assigned the two independently built shader bundles the same internal bundle
identity. When Better Clouds loaded first, Better AA could not load its
diagnostic, motion-statistics, sanitizer, depth-edge, or Custom TAA shaders.
That single resource failure disabled Buffers, Custom TAA, and the sanitizer
inputs required by DLAA and FSR2.

The same log exposed an independent initialization failure. The Harmony prefix
for `GraphicsSettings.SetAntiAliasing(int quality)` named its argument `level`.
Harmony binds ordinary patch arguments by original parameter name, rejected the
prefix, and aborted the patch group.

The old backend failure chain also selected another reconstruction backend and
eventually PPv2 TAA. That is unsafe when the failed component is a shared scene
input or resource, and it does not match the desired no-AA failure behavior.

## Decision

- Better AA's Addressables group uses
  `GroupGuidProjectIdEntriesHash`. Including the entry GUIDs makes the internal
  bundle identity distinct from the cloned Better Clouds project while keeping
  stable IDs for unchanged Better AA content. The idempotent editor preparation
  method pins this policy so a regenerated group cannot silently return to the
  colliding default.
- The stock AA Harmony prefix addresses the original integer as `__0`, so it is
  independent of KSP's managed parameter name.
- Any unavailable or runtime-failed AA backend falls directly to the
  coordinator-owned Off backend. The requested selection remains visible for
  diagnosis, but no temporal or spatial fallback backend runs.
- PPv2 remains an explicitly selected engineering comparison only. It is never
  an automatic fallback.

This supersedes the fallback-chain clauses in Decisions 0002, 0003, 0004, and
0006. It does not change their backend implementation or comparison status.

## Verification

Focused EditMode coverage checks both regression boundaries: the Harmony prefix
must retain a positional by-reference integer argument, and the Better AA
Addressables schema must include entry GUIDs in the internal bundle identity.
The complete Unity 6000.4.1f1 EditMode suite passes all 37 tests.

The editor isolation check loads the installed Better Clouds bundle first and
the rebuilt Better AA bundle second in one process. A clean KSP2 main-menu smoke
then loads the two catalogs in that same order and records successful loading of
the diagnostic, motion-statistics, sanitizer, depth-edge, and Custom TAA
shaders, with no bundle identity or Harmony exception. The Redux Test Harness
runtime-ready test also passes with log-error checking enabled.

Representative Custom, DLAA, FSR2, and Buffers image checks and explicit backend
failure injection remain manual. Failure injection must report `Off fallback
active` without activating PPv2, Custom TAA, SMAA, or FXAA.
