using System;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Backends.Nvidia;
using ReduxBetterAA.Configuration;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class NvidiaReconstructionTests
    {
        [TestCase(0, 4)]
        [TestCase(1, 2)]
        [TestCase(2, 1)]
        [TestCase(3, 0)]
        public void PersistedQualityMapsToTheVerifiedUnityEnum(int quality, int expected)
        {
            Assert.That(NvidiaDlaaApi.GetDlssQualityValue((ReconstructionQuality)quality), Is.EqualTo(expected));
        }

        [Test]
        public void UnknownQualityIsRejectedInsteadOfEnablingAnotherMode()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvidiaDlaaApi.GetDlssQualityValue((ReconstructionQuality)42));
        }

        [Test]
        public void NativeSizeQueryPreservesDimensionsWithoutNativeInitialization()
        {
            var api = new NvidiaDlaaApi();
            Assert.That(api.TryGetOptimalRenderSize(2560, 1440, ReconstructionQuality.Native,
                out int width, out int height, out string reason), Is.True, reason);
            Assert.That(width, Is.EqualTo(2560));
            Assert.That(height, Is.EqualTo(1440));
            Assert.That(api.ContextCreated, Is.False);
            Assert.That(api.DeviceVersion, Is.Zero);
            Assert.That(api.TryGetRenderPercent(2560, 1440, ReconstructionQuality.Native,
                out int percent, out reason), Is.True, reason);
            Assert.That(percent, Is.EqualTo(100));
            Assert.That(api.DeviceVersion, Is.Zero);
        }

        [TestCase(2560, 1440, 1707, 960, 1280, 720, 1920, 1080, 67)]
        [TestCase(3840, 2160, 2227, 1253, 1920, 1080, 2560, 1440, 58)]
        [TestCase(1921, 1081, 961, 541, 961, 541, 961, 541, 50)]
        [TestCase(1919, 1079, 1280, 720, 1000, 500, 1280, 720, 66)]
        [TestCase(400, 400, 202, 202, 200, 200, 204, 204, 51)]
        public void IntegerRenderScaleUsesClosestSdkSizeInsideBothBounds(
            int outputWidth, int outputHeight, int optimalWidth, int optimalHeight,
            int minWidth, int minHeight, int maxWidth, int maxHeight, int expected)
        {
            var settings = new NvidiaDlaaApi.RenderSizeSettings(optimalWidth, optimalHeight,
                minWidth, minHeight, maxWidth, maxHeight);
            Assert.That(NvidiaDlaaApi.TryChooseRenderPercent(outputWidth, outputHeight,
                in settings, out int percent), Is.True);
            Assert.That(percent, Is.EqualTo(expected));
            Assert.That(settings.Contains(Mathf.CeilToInt(outputWidth * (percent / 100f)),
                Mathf.CeilToInt(outputHeight * (percent / 100f))), Is.True);
        }

        [TestCase(1920, 1080, 1280, 720, 1280, 720, 1280, 720)]
        [TestCase(1920, 1080, 1920, 1080, 1920, 1080, 1920, 1080)]
        [TestCase(100, 100, 67, 67, 50, 50, 66, 66)]
        [TestCase(100, 100, 67, 67, 0, 50, 100, 100)]
        [TestCase(0, 100, 67, 67, 50, 50, 100, 100)]
        public void UnrepresentableOrInvalidSdkBoundsNeverInventAScale(
            int outputWidth, int outputHeight, int optimalWidth, int optimalHeight,
            int minWidth, int minHeight, int maxWidth, int maxHeight)
        {
            var settings = new NvidiaDlaaApi.RenderSizeSettings(optimalWidth, optimalHeight,
                minWidth, minHeight, maxWidth, maxHeight);
            Assert.That(NvidiaDlaaApi.TryChooseRenderPercent(outputWidth, outputHeight,
                in settings, out int percent), Is.False);
            Assert.That(percent, Is.Zero);
        }

        [TestCase(999, 600, false)]
        [TestCase(1000, 599, false)]
        [TestCase(1201, 700, false)]
        [TestCase(1100, 701, false)]
        [TestCase(1000, 600, true)]
        [TestCase(1200, 700, true)]
        public void ContextRectangleValidationIncludesEverySdkBoundary(int width, int height, bool expected)
        {
            var settings = new NvidiaDlaaApi.RenderSizeSettings(1100, 650, 1000, 600, 1200, 700);
            Assert.That(settings.Contains(width, height), Is.EqualTo(expected));
        }

        [TestCase(0, 1440)]
        [TestCase(2560, 0)]
        [TestCase(-1, 1440)]
        public void InvalidOutputDimensionsNeverInitializeTheVendor(int outputWidth, int outputHeight)
        {
            var api = new NvidiaDlaaApi();
            Assert.That(api.TryGetOptimalRenderSize(outputWidth, outputHeight,
                ReconstructionQuality.Quality, out int width, out int height, out string reason), Is.False);
            Assert.That(width, Is.Zero);
            Assert.That(height, Is.Zero);
            Assert.That(reason, Is.Not.Empty);
            Assert.That(api.DeviceVersion, Is.Zero);
        }

        [Test]
        public void ReconstructionOutputKeepsDisplaySizeAndVendorStorageRequirements()
        {
            var source = new RenderTextureDescriptor(1280, 720)
            {
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                depthBufferBits = 24,
                msaaSamples = 4,
                useDynamicScale = true
            };
            var output = NvidiaDlaaBackend.BuildOutputDescriptor(source, 2560, 1440);
            Assert.That(output.width, Is.EqualTo(2560));
            Assert.That(output.height, Is.EqualTo(1440));
            Assert.That(output.graphicsFormat, Is.EqualTo(source.graphicsFormat));
            Assert.That(output.enableRandomWrite, Is.True);
            Assert.That(output.depthBufferBits, Is.Zero);
            Assert.That(output.msaaSamples, Is.EqualTo(1));
            Assert.That(output.useDynamicScale, Is.False);
            Assert.That(source.width, Is.EqualTo(1280));
            Assert.That(source.height, Is.EqualTo(720));
            var native = NvidiaDlaaBackend.BuildOutputDescriptor(source);
            Assert.That(native.width, Is.EqualTo(source.width));
            Assert.That(native.height, Is.EqualTo(source.height));
        }

        [TestCase(1920, 1080, 1920, 1080, 8)]
        [TestCase(1920, 1080, 2880, 1620, 18)]
        [TestCase(1600, 900, 2720, 1530, 24)]
        [TestCase(1280, 720, 2560, 1440, 32)]
        [TestCase(640, 360, 2560, 1440, 32)]
        [TestCase(0, 0, 2560, 1440, 32)]
        public void ReconstructionJitterUsesRenderRatioWithinTheSupportedSequence(
            int renderWidth, int renderHeight, int outputWidth, int outputHeight, int expected)
        {
            Assert.That(NvidiaDlaaBackend.GetReconstructionSequenceLength(
                renderWidth, renderHeight, outputWidth, outputHeight), Is.EqualTo(expected));
        }

        [Test]
        public void ConfiguringSrKeepsItsIdentityAndExposureSeparateFromNativeDlaa()
        {
            var backend = new NvidiaDlaaBackend(null, null, null, null, null);
            Assert.That(backend.Id, Is.EqualTo("NVIDIA DLAA"));
            backend.ConfigureReconstruction(ReconstructionQuality.Quality);
            Assert.That(backend.Id, Is.EqualTo("NVIDIA DLSS"));
            Assert.That(backend.ExposureSource, Is.EqualTo("post-PPv2 color (HDR processing, pre-exposure 1)"));
            backend.ConfigureReconstruction(ReconstructionQuality.Native);
            Assert.That(backend.Id, Is.EqualTo("NVIDIA DLAA"));
            Assert.That(backend.Active, Is.False);
        }
    }
}
