using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class PerformanceTimingTests
    {
        private const BackendSelection Mode = BackendSelection.NvidiaDlss;

        private static BackendPerformanceProfiler Warm(ulong timestamp = 0)
        {
            var profiler = new BackendPerformanceProfiler();
            profiler.Start(Mode);
            for (int i = 0; i < 30; i++) Tick(profiler, timestamp, 12, 6, timestamp != 0);
            return profiler;
        }

        private static void Tick(BackendPerformanceProfiler profiler, ulong timestamp,
            double cpu = 12, double gpu = 6, bool available = true, double fallback = 20)
        {
            var timing = new FrameTiming { frameStartTimestamp = timestamp, cpuFrameTime = cpu, gpuFrameTime = gpu };
            profiler.TickSample(Mode, Mode, available, in timing, fallback);
        }

        [Test]
        public void WarmupSeedsTimestampSoItsLastFrameIsNotCountedAgain()
        {
            var profiler = Warm(100);
            Tick(profiler, 100);
            PerformanceProfileSnapshot result = profiler.GetSnapshot(Mode);
            Assert.That(result.Samples, Is.EqualTo(1));
            Assert.That(result.GpuSamples, Is.Zero);
            Assert.That(result.CpuTimingSamples, Is.Zero);
            Assert.That(result.CpuFallbackSamples, Is.EqualTo(1));
            Assert.That(result.DuplicateTimingRecords, Is.EqualTo(1));
        }

        [Test]
        public void RepeatedAndOutOfOrderRecordsDoNotWeightGpuAverage()
        {
            var profiler = Warm();
            Tick(profiler, 100, gpu: 2);
            Tick(profiler, 100, gpu: 90);
            Tick(profiler, 99, gpu: 90);
            Tick(profiler, 101, gpu: 4);
            PerformanceProfileSnapshot result = profiler.GetSnapshot(Mode);
            Assert.That(result.GpuSamples, Is.EqualTo(2));
            Assert.That(result.AverageGpuFrameMilliseconds, Is.EqualTo(3));
            Assert.That(result.PeakGpuFrameMilliseconds, Is.EqualTo(4));
            Assert.That(result.DuplicateTimingRecords, Is.EqualTo(2));
            Assert.That(result.CpuTimingSamples, Is.EqualTo(2));
            Assert.That(result.CpuFallbackSamples, Is.EqualTo(2));
        }

        [Test]
        public void DelayedGpuCompletionIsAcceptedOnceForTheSameCpuFrame()
        {
            var profiler = Warm();
            Tick(profiler, 100, gpu: 0);
            Tick(profiler, 100, gpu: 7);
            var laterCompletion = new FrameTiming
            { frameStartTimestamp = 100, cpuFrameTime = 12, gpuFrameTime = 9, cpuTimeFrameComplete = 200 };
            profiler.TickSample(Mode, Mode, true, in laterCompletion, 20);
            PerformanceProfileSnapshot result = profiler.GetSnapshot(Mode);
            Assert.That(result.GpuSamples, Is.EqualTo(1));
            Assert.That(result.AverageGpuFrameMilliseconds, Is.EqualTo(7));
            Assert.That(result.InvalidGpuSamples, Is.EqualTo(1));
            Assert.That(result.CpuTimingSamples, Is.EqualTo(1));
            Assert.That(result.CpuFallbackSamples, Is.EqualTo(2));
        }

        [Test]
        public void DelayedWarmupGpuCompletionIsExcludedButSamplingCompletionIsAccepted()
        {
            var profiler = new BackendPerformanceProfiler();
            profiler.Start(Mode);
            for (int i = 0; i < 30; i++) Tick(profiler, 100, gpu: 0);
            Tick(profiler, 100, gpu: 7);
            Assert.That(profiler.GetSnapshot(Mode).GpuSamples, Is.Zero);
            Tick(profiler, 101, gpu: 0);
            Tick(profiler, 101, gpu: 8);
            Assert.That(profiler.GetSnapshot(Mode).GpuSamples, Is.EqualTo(1));
            Assert.That(profiler.GetSnapshot(Mode).AverageGpuFrameMilliseconds, Is.EqualTo(8));
        }

        [Test]
        public void EqualDurationsFromDifferentFramesRemainSeparateSamples()
        {
            var profiler = Warm();
            Tick(profiler, 100, gpu: 5);
            Tick(profiler, 101, gpu: 5);
            Assert.That(profiler.GetSnapshot(Mode).GpuSamples, Is.EqualTo(2));
        }

        [Test]
        public void ZeroTimestampAndMissingRecordsUseCpuFallbackWithoutInventingGpuData()
        {
            var profiler = Warm();
            Tick(profiler, 0, gpu: 500);
            Tick(profiler, 1, available: false);
            PerformanceProfileSnapshot result = profiler.GetSnapshot(Mode);
            Assert.That(result.GpuSamples, Is.Zero);
            Assert.That(result.InvalidTimingRecords, Is.EqualTo(1));
            Assert.That(result.TimingRecords, Is.EqualTo(1));
            Assert.That(result.CpuFallbackSamples, Is.EqualTo(2));
            Assert.That(result.AverageCpuFrameMilliseconds, Is.EqualTo(20));
            Assert.That(result.GpuUnavailableReason, Is.Not.Empty);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void InvalidDurationsCannotPoisonAverages(double invalid)
        {
            var profiler = Warm();
            Tick(profiler, 1, invalid, invalid, fallback: invalid);
            Tick(profiler, 2, 12, 6);
            PerformanceProfileSnapshot result = profiler.GetSnapshot(Mode);
            Assert.That(result.Samples, Is.EqualTo(2));
            Assert.That(result.GpuSamples, Is.EqualTo(1));
            Assert.That(result.InvalidGpuSamples, Is.EqualTo(1));
            Assert.That(result.AverageCpuFrameMilliseconds, Is.EqualTo(12));
            Assert.That(result.AverageGpuFrameMilliseconds, Is.EqualTo(6));
        }

        [Test]
        public void CompletedWindowStopsAt240AndPreservesActualSource()
        {
            var profiler = Warm();
            for (ulong i = 1; i <= 250; i++) Tick(profiler, i);
            PerformanceProfileSnapshot result = profiler.GetSnapshot(Mode);
            Assert.That(result.State, Is.EqualTo(PerformanceProfileState.Complete));
            Assert.That(result.Samples, Is.EqualTo(240));
            Assert.That(result.GpuSamples, Is.EqualTo(240));
            Assert.That(result.GpuSource, Is.EqualTo(GpuTimingSource.FrameTimingManager));
            Assert.That(result.GpuUnavailableReason, Is.Empty);
        }

        [Test]
        public void SwitchingProfileCancelsPreviousWindowAndResetsIdentity()
        {
            var profiler = Warm(100);
            Tick(profiler, 101);
            profiler.Start(BackendSelection.Off);
            Assert.That(profiler.GetSnapshot(Mode).State, Is.EqualTo(PerformanceProfileState.Cancelled));
            profiler.Start(Mode);
            var absent = default(FrameTiming);
            for (int i = 0; i < 30; i++) profiler.TickSample(Mode, Mode, false, in absent, 20);
            Tick(profiler, 1);
            Assert.That(profiler.GetSnapshot(Mode).GpuSamples, Is.EqualTo(1));
        }

        [Test]
        public void FallbackAndInvalidationStopFurtherAccumulation()
        {
            var profiler = Warm();
            Tick(profiler, 1);
            var timing = new FrameTiming { frameStartTimestamp = 2, gpuFrameTime = 6 };
            profiler.TickSample(Mode, BackendSelection.NvidiaDlaa, true, in timing, 20);
            Tick(profiler, 3);
            Assert.That(profiler.GetSnapshot(Mode).State, Is.EqualTo(PerformanceProfileState.BackendUnavailable));
            Assert.That(profiler.GetSnapshot(Mode).Samples, Is.EqualTo(1));
            profiler.InvalidateAll();
            Assert.That(profiler.GetSnapshot(Mode).State, Is.EqualTo(PerformanceProfileState.NeverRun));
            Assert.That(profiler.GetSnapshot(Mode).GpuSamples, Is.Zero);
        }
    }
}
