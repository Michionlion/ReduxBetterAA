using System;
using System.Diagnostics;
using ReduxBetterAA.Configuration;
using Unity.Profiling;
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

    internal enum GpuTimingSource
    {
        None = 0,
        FrameTimingManager = 1,
        ProfilerRecorder = 2
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
        public readonly GpuTimingSource GpuSource;
        public readonly bool FrameTimingEnabledAtStart;
        public readonly bool RuntimeGpuRecorderAvailable;
        public readonly int CpuTimingSamples;
        public readonly int CpuFallbackSamples;
        public readonly int TimingRecords;
        public readonly int DuplicateTimingRecords;
        public readonly int InvalidTimingRecords;
        public readonly int InvalidGpuSamples;
        public readonly string GpuUnavailableReason;
        public readonly string TimingRecorderError;

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
            int resolveSamples,
            GpuTimingSource gpuSource = GpuTimingSource.None,
            bool frameTimingEnabledAtStart = false,
            bool runtimeGpuRecorderAvailable = false,
            int cpuTimingSamples = 0,
            int cpuFallbackSamples = 0,
            int timingRecords = 0,
            int duplicateTimingRecords = 0,
            int invalidTimingRecords = 0,
            int invalidGpuSamples = 0,
            string gpuUnavailableReason = "",
            string timingRecorderError = "")
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
            GpuSource = gpuSource;
            FrameTimingEnabledAtStart = frameTimingEnabledAtStart;
            RuntimeGpuRecorderAvailable = runtimeGpuRecorderAvailable;
            CpuTimingSamples = cpuTimingSamples;
            CpuFallbackSamples = cpuFallbackSamples;
            TimingRecords = timingRecords;
            DuplicateTimingRecords = duplicateTimingRecords;
            InvalidTimingRecords = invalidTimingRecords;
            InvalidGpuSamples = invalidGpuSamples;
            GpuUnavailableReason = gpuUnavailableReason;
            TimingRecorderError = timingRecorderError;
        }

        public bool Running =>
            State == PerformanceProfileState.WarmingUp ||
            State == PerformanceProfileState.Sampling;
    }

    /// <summary>
    /// Fixed-storage frame profiler. Sampling performs no managed allocations
    /// after collector initialization. Runtime counters opt in only during a
    /// profile, including release players built without Frame Timing Stats.
    /// CPU frame intervals include waits; these are not CPU busy times.
    /// Project-owned render hooks additionally report CPU submission time.
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
            public GpuTimingSource GpuSource;
            public bool FrameTimingEnabledAtStart;
            public bool RuntimeGpuRecorderAvailable;
            public int CpuTimingSamples, CpuFallbackSamples;
            public int TimingRecords, DuplicateTimingRecords, InvalidTimingRecords, InvalidGpuSamples;
            public string TimingRecorderError;
        }

        private readonly Result[] _results =
            new Result[(int)BackendSelection.Supersampling + 1];
        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        private BackendSelection _runningMode;
        private long _pendingResolveTicks;
        private bool _running;
        private bool _collectorsInitialized;
        private bool _managerGpuDuringWarmup;
        private ulong _lastTimingTimestamp;
        private ulong _lastGpuTimestamp;
        private int _gpuRecorderCursor;
        private ProfilerRecorder _cpuRecorder, _gpuRecorder;

        public void Start(BackendSelection mode)
        {
            if (mode < BackendSelection.Off || mode > BackendSelection.Supersampling)
            {
                return;
            }
            Cancel();
            StopRecorders();
            _runningMode = mode;
            _pendingResolveTicks = 0;
            _running = true;
            _managerGpuDuringWarmup = false;
            _lastTimingTimestamp = 0;
            _lastGpuTimestamp = 0;
            _gpuRecorderCursor = 0;
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
            StopRecorders();
        }

        public void Invalidate(BackendSelection mode)
        {
            if (mode < BackendSelection.Off || mode > BackendSelection.Supersampling)
            {
                return;
            }
            if (_running && _runningMode == mode)
            {
                _running = false;
                _pendingResolveTicks = 0;
                StopRecorders();
            }
            _results[(int)mode] = default;
        }

        public void InvalidateAll()
        {
            _running = false;
            _pendingResolveTicks = 0;
            StopRecorders();
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
            if (!ValidateMode(requestedMode, activeMode)) return;
            ref Result result = ref _results[(int)_runningMode];
            InitializeRecorders(ref result);
            FrameTimingManager.CaptureFrameTimings();
            bool hasTiming = FrameTimingManager.GetLatestTimings(1, _frameTimings) > 0;
            ConsumeTick(ref result, hasTiming, in _frameTimings[0], Time.unscaledDeltaTime * 1000.0);
        }

        // Deterministic input seam: tests exercise the actual accumulator and
        // lifecycle without Unity's asynchronous native timing implementation.
        internal void TickSample(
            BackendSelection requestedMode,
            BackendSelection activeMode,
            bool hasTiming,
            in FrameTiming timing,
            double fallbackCpuMilliseconds)
        {
            if (!ValidateMode(requestedMode, activeMode)) return;
            ref Result result = ref _results[(int)_runningMode];
            ConsumeTick(ref result, hasTiming, in timing, fallbackCpuMilliseconds);
        }

        private bool ValidateMode(BackendSelection requestedMode, BackendSelection activeMode)
        {
            if (!_running) return false;
            if (requestedMode != _runningMode || activeMode != _runningMode)
            {
                ref Result result = ref _results[(int)_runningMode];
                result.State = PerformanceProfileState.BackendUnavailable;
                _running = false;
                _pendingResolveTicks = 0;
                StopRecorders();
                return false;
            }
            return true;
        }

        private void ConsumeTick(ref Result result, bool hasTiming, in FrameTiming timing, double fallbackCpuMilliseconds)
        {
            bool fresh = hasTiming && AcceptTimestamp(timing.frameStartTimestamp, ref _lastTimingTimestamp);
            // GPU completion may arrive after the CPU fields for this frame.
            // Consume that completion once without accepting a repeated CPU sample.
            bool freshGpu = hasTiming && IsFinitePositive(timing.gpuFrameTime) &&
                AcceptTimestamp(timing.frameStartTimestamp, ref _lastGpuTimestamp);
            if (result.WarmupRemaining > 0)
            {
                if (freshGpu) _managerGpuDuringWarmup = true;
                if (_gpuRecorder.Valid) _gpuRecorderCursor = _gpuRecorder.Count;
                result.WarmupRemaining--;
                result.State = result.WarmupRemaining > 0
                    ? PerformanceProfileState.WarmingUp
                    : PerformanceProfileState.Sampling;
                if (result.WarmupRemaining == 0)
                {
                    // A warmup CPU record can receive its GPU completion later.
                    // Exclude all warmup identities even if that field was zero.
                    if (_lastTimingTimestamp > _lastGpuTimestamp) _lastGpuTimestamp = _lastTimingTimestamp;
                    result.GpuSource = !_managerGpuDuringWarmup && _gpuRecorder.Valid
                        ? GpuTimingSource.ProfilerRecorder : GpuTimingSource.FrameTimingManager;
                }
                _pendingResolveTicks = 0;
                return;
            }

            if (hasTiming)
            {
                result.TimingRecords++;
                if (timing.frameStartTimestamp == 0) result.InvalidTimingRecords++;
                else if (!fresh) result.DuplicateTimingRecords++;
            }
            bool nativeCpu = fresh && IsFinitePositive(timing.cpuFrameTime);
            double cpuMilliseconds = nativeCpu ? timing.cpuFrameTime : fallbackCpuMilliseconds;
            if (IsFinitePositive(cpuMilliseconds))
            {
                if (nativeCpu) result.CpuTimingSamples++; else result.CpuFallbackSamples++;
                result.CpuSum += cpuMilliseconds;
                if (cpuMilliseconds > result.CpuPeak)
                {
                    result.CpuPeak = cpuMilliseconds;
                }
            }

            if (result.GpuSource == GpuTimingSource.ProfilerRecorder)
            {
                int count = _gpuRecorder.Valid ? _gpuRecorder.Count : 0;
                while (_gpuRecorderCursor < count)
                    AddGpuSample(ref result, _gpuRecorder.GetSample(_gpuRecorderCursor++).Value * 1e-6);
            }
            else if (freshGpu) AddGpuSample(ref result, timing.gpuFrameTime);
            else if (fresh && !IsFinitePositive(timing.gpuFrameTime)) result.InvalidGpuSamples++;

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
                StopRecorders();
            }
        }

        internal static bool AcceptTimestamp(ulong timestamp, ref ulong lastTimestamp)
        {
            if (timestamp == 0 || timestamp <= lastTimestamp) return false;
            lastTimestamp = timestamp;
            return true;
        }

        private static void AddGpuSample(ref Result result, double milliseconds)
        {
            if (!IsFinitePositive(milliseconds)) { result.InvalidGpuSamples++; return; }
            result.GpuSum += milliseconds;
            result.GpuSamples++;
            if (milliseconds > result.GpuPeak) result.GpuPeak = milliseconds;
        }

        private void InitializeRecorders(ref Result result)
        {
            if (_collectorsInitialized) return;
            _collectorsInitialized = true;
            result.FrameTimingEnabledAtStart = FrameTimingManager.IsFeatureEnabled();
            // These are counters containing frame durations, so do not use the
            // GpuRecorder option intended for measuring GPU profiler markers.
            // No wrap: every collected entry is consumed at most once.
            try
            {
                _cpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal,
                    "CPU Total Frame Time", 512, ProfilerRecorderOptions.SumAllSamplesInFrame);
                _gpuRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal,
                    "GPU Frame Time", 512, ProfilerRecorderOptions.SumAllSamplesInFrame);
                result.RuntimeGpuRecorderAvailable = _gpuRecorder.Valid;
            }
            catch (Exception error)
            {
                result.TimingRecorderError = error.GetType().Name + ": " + error.Message;
                StopRecorders();
                _collectorsInitialized = true;
            }
        }

        private void StopRecorders()
        {
            if (_cpuRecorder.Valid) _cpuRecorder.Dispose();
            if (_gpuRecorder.Valid) _gpuRecorder.Dispose();
            _cpuRecorder = default;
            _gpuRecorder = default;
            _collectorsInitialized = false;
        }

        public PerformanceProfileSnapshot GetSnapshot(BackendSelection mode)
        {
            if (mode < BackendSelection.Off || mode > BackendSelection.Supersampling)
            {
                mode = BackendSelection.Off;
            }
            Result result = _results[(int)mode];
            return new PerformanceProfileSnapshot(
                result.State,
                result.WarmupRemaining,
                result.Samples,
                ProfileFrames,
                Divide(result.CpuSum, result.CpuTimingSamples + result.CpuFallbackSamples),
                result.CpuPeak,
                Divide(result.GpuSum, result.GpuSamples),
                result.GpuPeak,
                result.GpuSamples,
                Divide(result.ResolveSum, result.ResolveSamples),
                result.ResolvePeak,
                result.ResolveSamples,
                result.GpuSource,
                result.FrameTimingEnabledAtStart,
                result.RuntimeGpuRecorderAvailable,
                result.CpuTimingSamples,
                result.CpuFallbackSamples,
                result.TimingRecords,
                result.DuplicateTimingRecords,
                result.InvalidTimingRecords,
                result.InvalidGpuSamples,
                result.GpuSamples > 0 ? string.Empty :
                    result.GpuSource == GpuTimingSource.ProfilerRecorder
                        ? "Unity's GPU counter returned no positive samples."
                        : !result.FrameTimingEnabledAtStart && !result.RuntimeGpuRecorderAvailable
                            ? "Unity's runtime GPU timing counter is unavailable."
                            : "Unity returned no fresh GPU frame timings.",
                result.TimingRecorderError ?? string.Empty
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
