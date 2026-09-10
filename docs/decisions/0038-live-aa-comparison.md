# 0038 — Live AA comparison and non-modal F10 menu

Status: implemented; runtime evidence recorded below.

The final build passed 83/83 editor tests and 153/153 in-game assertions; see
[validation results](../live-aa-comparison-validation.md).

The requested comparison must keep moving. A screenshot pair or alternating
backends that discard history cannot reveal temporal quality. The diagnostic
therefore replays the existing `RenderScalePresenter._sourceCameras` twice after
game LateUpdate, using each camera's real components and render order. No scene
objects or game behaviours are cloned. Private presenter fields match the inspected
Redux 0.2.9 implementation; absence of that verified stack stops comparison.

Each arm owns its backend, motion/depth helpers, PPv2 temporal-AA object and render
target. A small render-enable gate selects exactly one jitter/resolve owner per
pass. The other hook passes color through. None and supersampling select PPv2
None without projection jitter. Native modes receive native-sized color, depth and
motion; only supersampling allocates a larger scene target. The split shader samples
the original full-frame UV on each side, using the same bilinear downsampling as
Redux's base presenter. A separate scene presentation camera runs before UI.

This is an explicit diagnostic exception to the single-backend rule: two independent
scene outputs exist, and no temporal output feeds another temporal backend. Normal
rendering still owns one backend and has no diagnostic resource requirement. Saved
settings are not changed. The maximum supersampling control is 400% per dimension,
bounded by `SystemInfo.maxTextureSize` (400% is sixteen times the pixel count).

History discontinuities are evaluated before temporarily disabling source cameras.
The cameras are disabled only between the manual scene passes and end of frame;
original targets are restored in `finally`. End-of-frame and stop paths restore
camera flags. Scene/screen-size changes stop comparison; origin-rebase and normal
coordinator reset messages reset both histories. Backend failure stops the comparison
without substituting a differently labelled mode. Start failures are also caught
and release the normal renderer. Resources and claims are released in reverse order.

Stock clouds, exposure, and other game effects remain game-owned and execute during
both passes. Their own internal temporal state is not cloned. The comparison is an
interactive diagnostic, not an isolated vendor benchmark or supersampled ground
truth generator. GPU memory use can be substantial at the high end. It deliberately
does not report timing; issue ZIPs require returning to normal AA first.

The F10 menu has AA, Compare and Diagnostics pages. AA selection is an expandable
dropdown; common settings appear before collapsible advanced and performance
controls. The window is smaller and draggable. EventSystem suppression was removed,
so outside clicks reach the game and inside clicks may pass through as requested.
F10 and Ctrl+F10 open the menu; Shift+F10 still captures a screenshot.

Validation command: `pwsh -NoProfile -File tools/Run-QualityTests.ps1 -LiveComparison`.
Editor GPU tests cover original-UV cropping, orientation, mixed source resolutions,
divider endpoints and texture-size bounds. Runtime checks cover independent backend
and target ownership, created vendor contexts, finite scene pixels, temporal history
validity and reset counts during camera movement, actual supersampling dimensions,
normal-mode restoration, the enabled EventSystem, and stopping on a map transition.
