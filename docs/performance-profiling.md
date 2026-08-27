# In-game performance profiling

Version 0.5.1 adds a fixed-window profiler to the Off, PPv2, Custom, DLAA, and
FSR2 pages in the Ctrl+F10 panel. Each run uses 30 warm-up frames followed by 240
measured frames. Starting a run closes the panel so IMGUI rendering is not part
of the sample; reopen it with Ctrl+F10 after the run to view the result.

The profiler records:

- whole-frame CPU time from Unity's frame-timing API, with unscaled frame time
  as a fallback;
- whole-frame GPU time when Unity and the active graphics API expose it;
- average and peak values for both;
- CPU submission time around the project-owned Custom, DLAA, and FSR2 resolve
  hooks.

PPv2 is executed internally by Unity's Post Processing Stack, so its isolated
pass timing is not exposed here. Compare PPv2 using whole-frame results. Resolve
CPU submission is also not GPU pass duration: it measures the main-thread time
spent submitting or synchronously executing the mod-owned resolve.

## Comparison procedure

1. Hold resolution, render scale, scene, camera view, frame cap, and graphics
   settings constant.
2. Profile Off first. Its completed result becomes the session baseline.
3. Select and profile each AA mode without changing the scene. A mode that
   falls back is rejected instead of being mislabeled as the requested backend.
4. Reopen Ctrl+F10 after each run. The mode page shows averages, peaks, available
   resolve submission time, and approximate CPU/GPU deltas against Off.
5. After all runs, click **Write report**. Capability-report schema 9 serializes
   every session profile so results can be compared or shared.

Whole-frame deltas are approximate because gameplay, simulation, streaming,
temperature, and clock variation remain part of the measurement. Repeat runs
in a stable, GPU-bound scene before drawing performance conclusions.
