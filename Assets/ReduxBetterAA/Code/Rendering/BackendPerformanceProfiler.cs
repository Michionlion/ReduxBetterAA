using System;
using System.Diagnostics;
using ReduxBetterAA.Configuration;
using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    internal enum PerformanceProfileState
    {
        NeverRun = 0,
        WarmingUp = 1,
        Sampling = 2,
        Complete = 3,
        BackendUnavailable = 4,
        Cancelled = 5
    }

    internal readonly struct PerformanceProfileSnapshot
    {
        public readonly PerformanceProfileState State;
        public readonly int WarmupFramesRemaining;
        public readonly int Samples;
        public readonly int TargetSamples;
        public readonly double AverageCpuFrameMilliseconds;
        public readonly double PeakCpuFrameMilliseconds;
        public readonly double AverageGpuFrameMilliseconds;
        public readonly double PeakGpuFrameMilliseconds;
        public readonly int GpuSamples;
        public readonly double AverageResolveCpuMilliseconds;
        public readonly double PeakResolveCpuMilliseconds;
        public readonly int ResolveSamples;

        public PerformanceProfileSnapshot(
            PerformanceProfileState state,
            int warmupFramesRemaining,
            int samples,
            int targetSamples,
            double averageCpuFrameMilliseconds,
            double peakCpuFrameMilliseconds,
            double averageGpuFrameMilliseconds,
            double peakGpuFrameMilliseconds,
            int gpuSamples,
            double averageResolveCpuMilliseconds,
            double peakResolveCpuMilliseconds,
            int resolveSamples)
        {
            State = state;
            WarmupFramesRemaining = warmupFramesRemaining;
            Samples = samples;
            TargetSamples = targetSamples;
            AverageCpuFrameMilliseconds = averageCpuFrameMilliseconds;
            PeakCpuFrameMilliseconds = peakCpuFrameMilliseconds;
            AverageGpuFrameMilliseconds = averageGpuFrameMilliseconds;
            PeakGpuFrameMilliseconds = peakGpuFrameMilliseconds;
            GpuSamples = gpuSamples;
            AverageResolveCpuMilliseconds = averageResolveCpuMilliseconds;
            PeakResolveCpuMilliseconds = peakResolveCpuMilliseconds;
            ResolveSamples = resolveSamples;
        }

        public bool Running =>
            State == PerformanceProfileState.WarmingUp ||
            State == PerformanceProfileState.Sampling;
    }

    /// <summary>
    /// Fixed-storage frame profiler. Sampling performs no managed allocations
    /// after construction. Whole-frame timings are comparable across all modes;
    /// project-owned render hooks additionally report CPU submission time.
    /// </summary>
    internal sealed class BackendPerformanceProfiler
    {
        private const int WarmupFrames = 30;
        private const int ProfileFrames = 240;
        private static readonly double TickToMilliseconds =
            1000.0 / Stopwatch.Frequency;

        private struct Result
        {
            public PerformanceProfileState State;
            public int WarmupRemaining;
            public int Samples;
            public double CpuSum;
            public double CpuPeak;
            public double GpuSum;
            public double GpuPeak;
            public int GpuSamples;
            public double ResolveSum;
            public double ResolvePeak;
            public int ResolveSamples;
        }

        private readonly Result[] _results =
            new Result[(int)BackendSelection.AmdFsr2 + 1];
        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        private BackendSelection _runningMode;
        private long _pendingResolveTicks;
        private bool _running;

        public void Start(BackendSelection mode)
        {
            if (mode < BackendSelection.Off || mode > BackendSelection.AmdFsr2)
            {
                return;
            }
            _runningMode = mode;
            _pendingResolveTicks = 0;
            _running = true;
            _results[(int)mode] = new Result
            {
                State = PerformanceProfileState.WarmingUp,
                WarmupRemaining = WarmupFrames
            };
        }

        public void Cancel()
        {
            if (!_running)
            {
                return;
            }
            Result result = _results[(int)_runningMode];
            result.State = PerformanceProfileState.Cancelled;
            _results[(int)_runningMode] = result;
            _running = false;
            _pendingResolveTicks = 0;
        }

        public void Invalidate(BackendSelection mode)
        {
            if (mode < BackendSelection.Off || mode > BackendSelection.AmdFsr2)
            {
                return;
            }
            if (_running && _runningMode == mode)
            {
                _running = false;
                _pendingResolveTicks = 0;
            }
            _results[(int)mode] = default;
        }

        public void InvalidateAll()
        {
            _running = false;
            _pendingResolveTicks = 0;
            Array.Clear(_results, 0, _results.Length);
        }

        public long BeginResolve(BackendSelection mode)
        {
            if (!_running || mode != _runningMode ||
                _results[(int)mode].WarmupRemaining > 0)
            {
                return 0;
            }
            return Stopwatch.GetTimestamp();
        }

        public void EndResolve(BackendSelection mode, long startTimestamp)
        {
            if (startTimestamp == 0 || !_running || mode != _runningMode)
            {
                return;
            }
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            if (elapsed > 0)
            {
                _pendingResolveTicks += elapsed;
            }
        }

        public void Tick(
            BackendSelection requestedMode,
            BackendSelection activeMode)
        {
            if (!_running)
            {
                return;
            }
            Result result = _results[(int)_runningMode];
            if (requestedMode != _runningMode || activeMode != _runningMode)
            {
                result.State = PerformanceProfileState.BackendUnavailable;
                _results[(int)_runningMode] = result;
                _running = false;
                _pendingResolveTicks = 0;
                return;
            }

            FrameTimingManager.CaptureFrameTimings();
            if (result.WarmupRemaining > 0)
            {
                result.WarmupRemaining--;
                result.State = result.WarmupRemaining > 0
                    ? PerformanceProfileState.WarmingUp
                    : PerformanceProfileState.Sampling;
                _results[(int)_runningMode] = result;
                _pendingResolveTicks = 0;
                return;
            }

            uint timingCount = FrameTimingManager.GetLatestTimings(
                1,
                _frameTimings
            );
            double cpuMilliseconds = timingCount > 0 &&
                IsFinitePositive(_frameTimings[0].cpuFrameTime)
                    ? _frameTimings[0].cpuFrameTime
                    : Time.unscaledDeltaTime * 1000.0;
            if (IsFinitePositive(cpuMilliseconds))
            {
                result.CpuSum += cpuMilliseconds;
                if (cpuMilliseconds > result.CpuPeak)
                {
                    result.CpuPeak = cpuMilliseconds;
                }
            }

            if (timingCount > 0)
            {
                double gpuMilliseconds = _frameTimings[0].gpuFrameTime;
                if (IsFinitePositive(gpuMilliseconds))
                {
                    result.GpuSum += gpuMilliseconds;
                    result.GpuSamples++;
                    if (gpuMilliseconds > result.GpuPeak)
                    {
                        result.GpuPeak = gpuMilliseconds;
                    }
                }
            }

            if (_pendingResolveTicks > 0)
            {
                double resolveMilliseconds =
                    _pendingResolveTicks * TickToMilliseconds;
                result.ResolveSum += resolveMilliseconds;
                result.ResolveSamples++;
                if (resolveMilliseconds > result.ResolvePeak)
                {
                    result.ResolvePeak = resolveMilliseconds;
                }
            }
            _pendingResolveTicks = 0;
            result.Samples++;
            if (result.Samples >= ProfileFrames)
            {
                result.State = PerformanceProfileState.Complete;
                _running = false;
            }
            _results[(int)_runningMode] = result;
        }

        public PerformanceProfileSnapshot GetSnapshot(BackendSelection mode)
        {
            if (mode < BackendSelection.Off || mode > BackendSelection.AmdFsr2)
            {
                mode = BackendSelection.Off;
            }
            Result result = _results[(int)mode];
            return new PerformanceProfileSnapshot(
                result.State,
                result.WarmupRemaining,
                result.Samples,
                ProfileFrames,
                Divide(result.CpuSum, result.Samples),
                result.CpuPeak,
                Divide(result.GpuSum, result.GpuSamples),
                result.GpuPeak,
                result.GpuSamples,
                Divide(result.ResolveSum, result.ResolveSamples),
                result.ResolvePeak,
                result.ResolveSamples
            );
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Divide(double value, int divisor)
        {
            return divisor > 0 ? value / divisor : 0.0;
        }
    }
}
