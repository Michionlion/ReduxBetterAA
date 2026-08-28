# Decision 0019: one user-facing anti-aliasing owner

## Status

Accepted for the 0.5.16 comparison build. The disabled KSP graphics control and
hardware-filtered mode list still require in-game UI verification.

The spatial mode portion is extended by Decision 0020: the normal list now also
contains the stock FXAA Low/High variants and the separate PPv2 SMAA option.
Decision 0030 supersedes the four-control limit by adding persistent foliage
repair and map-view AA toggles; the original four AA-quality controls remain
unchanged.

## Context

Redux Better AA previously registered its complete experimental configuration
surface in the normal mod settings. That duplicated the Ctrl+F10 engineering panel,
exposed implementation details such as exposure experiments to users,
and allowed an unavailable vendor backend to be selected before its later
fallback. KSP's graphics page also retained a separate Off/4x/8x MSAA selector,
which could enable a second filter independently of the selected temporal
backend.

The Unity NVIDIA preset values retained for DLAA are F, J, K, L, and M. NVIDIA's
Streamline definitions mark F as deprecated, recommend K as the current DLAA
default, and describe L and M as newer quality/performance alternatives:
<https://github.com/NVIDIA-RTX/Streamline/blob/main/include/sl_dlss.h>.

## Decision

- Redux Better AA owns scene anti-aliasing while loaded and holds Unity MSAA at
  Off. Harmony prevents current and legacy KSP graphics handlers from enabling
  it.
- The stock graphics selector remains visible but disabled. Its label and hover
  text direct the user to `Settings > Mods > Redux Better AA` instead of leaving
  a second apparently-functional AA control.
- The normal mod page registers exactly four settings: Mode, Sharpness, TAA
  stability, and DLAA preset.
- The public mode name is `TAA`. PPv2 is absent from the normal list and F12
  cycle but remains available in Ctrl+F10 for controlled comparison.
- DLAA is listed only when the active adapter is NVIDIA and Unity's NVIDIA
  runtime both initializes and reports DLSS/DLAA support. FSR2 is listed only
  when its Unity runtime initializes. When both are supported, DLAA is listed
  and cycled first.
- Shared Sharpness is clamped to 0 through 1 and is applied to every existing
  backend with a sharpening control. Zero disables FSR2 sharpening and passes
  zero to the other backends.
- TAA stability maps to the custom TAA stationary-history weight. DLAA preset
  has no `Default` choice; K is the configuration and migration default.
- Old `PPv2 TAA` and `Custom TAA` normal-setting values migrate to `TAA`.
  Unsupported stored vendor modes migrate to Off, and an old or invalid DLAA
  preset migrates to K.
- Removed normal settings do not reconfigure their subsystems at startup. Their
  conservative code defaults remain active; all advanced controls remain
  available through Ctrl+F10 and are deliberately session-only.

## Lifecycle and performance

Capability checks run once during mod pre-initialization. They create no DLAA or
FSR2 feature context and add no work or managed allocation to the steady-state
render path. Backend selection retains the existing coordinator-owned creation,
fallback, switch, and disposal lifecycle.

## Verification

Automated EditMode coverage verifies that the public list omits PPv2 and
unsupported vendor modes, prefers DLAA when both vendor paths are available,
and normalizes invalid DLAA presets to K. In-game verification must confirm:

1. KSP's graphics AA selector is disabled, shows the Redux Better AA navigation
   hint, and cannot enable MSAA.
2. The normal mod page contains only the four decided controls.
3. This NVIDIA installation lists Off, FXAA Low, FXAA High, SMAA, TAA, NVIDIA
   DLAA, and FSR 2 Native AA in that order.
4. Sharpness, TAA stability, and preset changes synchronize with Ctrl+F10 and survive
   a restart, while Ctrl+F10-only expert changes remain session-only.
