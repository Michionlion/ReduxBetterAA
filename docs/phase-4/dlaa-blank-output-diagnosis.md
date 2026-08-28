# Phase 4 DLAA blank-output diagnosis

## Observed failure

The first 0.4.0 in-player test displayed a blank scene while later UI remained
visible. The 2026-08-24 capability report and log showed that the managed API
bound, device API version 6 was active, and an equal-size 2560x1440 DLAA context
was repeatedly created without a managed exception. That rules out loader,
feature-query, context-creation, and UI-composition failures and localizes the
problem to native execution or output presentation.

## Root cause

The 0.4.0 backend copied the source render-texture descriptor and explicitly set
`enableRandomWrite` to false. Unity 6000.4.1f1's exact built-in
`DLSSIUpscaler.cs` instead converts the output to a linear graphics format and
sets `enableRandomWrite = true`, identifying it as a compute resource. NVIDIA
NGX therefore accepted the context but did not have a writable UAV output in
the mod integration. The untouched output was then blitted to the camera,
producing the blank scene while UI rendered normally afterward.

## 0.4.1 correction

- Convert the DLAA output descriptor to the source format's linear variant.
- Require `GraphicsFormatUsage.LoadStore` support before enabling DLAA.
- Set `enableRandomWrite = true`, disable MSAA/mips/dynamic scaling, and report
  the actual graphics format plus UAV state in the Ctrl+F10 panel and schema-5 JSON.
- Prefill the output from current scene color in the same command buffer before
  recording `ExecuteDLSS`. A silently rejected vendor command now degrades to
  an unfiltered current frame instead of a black scene.
- Keep runtime execution fail-open for the current frame, then switch to Off.

## Verification gate

Automated descriptor tests prove that the output is linear, single-sample, and
random-write enabled. In-player verification must still establish that NVIDIA
actually overwrites the prefill and produces DLAA rather than pass-through.
Capture the same moving thin-geometry view using Off and DLAA, confirm the Ctrl+F10
status ends in `UAV`, and write a report so `outputRandomWrite` and
`outputGraphicsFormat` are recorded.
