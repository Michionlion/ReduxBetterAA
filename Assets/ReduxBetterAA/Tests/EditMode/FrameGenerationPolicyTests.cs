using System;
using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationPolicyTests
    {
        private const GraphicsDeviceType Api = GraphicsDeviceType.Direct3D11;
        private static FrameGenerationSupport Support(FrameGenerationProviderKind kind, uint mask = 1u << 2,
            bool presenter = true, bool available = true, GraphicsDeviceType api = Api, string version = "test runtime")
        {
            var caps = new FrameGenerationCapabilities(kind.ToString(), version, available, api,
                FrameGenerationColorDomain.DisplayLinear, FrameGenerationMotionUnits.NormalizedUv, mask, true, false);
            return new FrameGenerationSupport(kind, in caps, presenter);
        }

        [Test]
        public void UninstalledCatalogExposesOffWithoutClaimingDisplayedFrames()
        {
            Assert.That(FrameGenerationAvailability.BuildChoices(), Is.EqualTo(new[] { "Off" }));
            Assert.That(FrameGenerationAvailability.Active, Is.False);
            var report = CapabilityReportBuilder.CaptureTemporalBackend().frameGeneration;
            Assert.That(report.active, Is.False);
            Assert.That(report.displayedFramesPerSecond, Is.Null);
        }

        [Test]
        public void DiscoveredAmdFamiliesPreserveNvidiaAndDeduplicateCompatibility()
        {
            try
            {
                FrameGenerationAvailability.SetNvidiaSupport(28);
                FrameGenerationAvailability.SetAmdSupport(4,3,123,4,3,123);
                Assert.That(FrameGenerationAvailability.BuildChoices(),Is.EqualTo(new[] {
                    "Off","Auto","DLSS 2x","DLSS 3x","DLSS 4x","FSR 3.1 2x" }));
                FrameGenerationAvailability.SetAmdSupport(4,4,456,4,3,123);
                Assert.That(FrameGenerationAvailability.BuildChoices(),Is.EqualTo(new[] {
                    "Off","Auto","DLSS 2x","DLSS 3x","DLSS 4x","FSR 4 2x","FSR 3.1 2x" }));
                FrameGenerationAvailability.SetNvidiaSupport(0);
                Assert.That(FrameGenerationAvailability.Resolve(FrameGenerationMode.Dlss4x).Selected,
                    Is.EqualTo(FrameGenerationMode.Fsr4_2x));
                FrameGenerationAvailability.SetAmdSupport(4,9,456,4,4,123);
                Assert.That(FrameGenerationAvailability.BuildChoices(),Is.EqualTo(new[] {"Off"}));
            }
            finally
            {
                FrameGenerationAvailability.SetNvidiaSupport(0);
                FrameGenerationAvailability.SetAmdSupport(0,0,0,0,0,0);
            }
        }

        [Test]
        public void NativeSdkProbeDoesNotAuthorizePresentation()
        {
            var support = new[] { Support(FrameGenerationProviderKind.NvidiaDlss, 28u, presenter: false) };
            Assert.That(FrameGenerationPolicy.BuildChoices(support, Api), Is.EqualTo(new[] { "Off" }));
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Dlss4x, support, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Off));
        }

        [Test]
        public void FullCatalogUsesOneValuePerBackendAndTotalMultiplier()
        {
            var support = new[] {
                Support(FrameGenerationProviderKind.NvidiaDlss, 28u),
                Support(FrameGenerationProviderKind.AmdFsr4), Support(FrameGenerationProviderKind.AmdFsr31)
            };
            Assert.That(FrameGenerationPolicy.BuildChoices(support, Api),
                Is.EqualTo(new[] { "Off", "Auto", "DLSS 2x", "DLSS 3x", "DLSS 4x", "FSR 4 2x", "FSR 3.1 2x" }));
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Auto, support, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Dlss4x));
        }

        [TestCase((int)FrameGenerationMode.Dlss4x)]
        [TestCase((int)FrameGenerationMode.Dlss3x)]
        public void MfgSavedOnAnotherMachineFallsBackToOrdinaryDlssFg(int request)
        {
            var support = new[] { Support(FrameGenerationProviderKind.NvidiaDlss) };
            var result = FrameGenerationPolicy.Resolve((FrameGenerationMode)request, support, Api);
            Assert.That(result.Selected, Is.EqualTo(FrameGenerationMode.Dlss2x));
            Assert.That(result.FellBack, Is.True);
            Assert.That(result.Reason, Does.Contain("using DLSS 2x"));
        }

        [Test]
        public void OlderGpuCanUseFsrFgWithoutChangingReconstruction()
        {
            var support = new[] { Support(FrameGenerationProviderKind.AmdFsr31) };
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Dlss4x, support, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Fsr31_2x));
            Assert.That(FrameGenerationPolicy.BuildChoices(support, Api), Is.EqualTo(new[] { "Off", "Auto", "FSR 3.1 2x" }));
        }

        [Test]
        public void Fsr4FallsBackTo31ButExplicit31DoesNotChangeProvider()
        {
            var old = new[] { Support(FrameGenerationProviderKind.AmdFsr31) };
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Fsr4_2x, old, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Fsr31_2x));
            var newerOnly = new[] { Support(FrameGenerationProviderKind.AmdFsr4), Support(FrameGenerationProviderKind.NvidiaDlss, 28u) };
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Fsr31_2x, newerOnly, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Off));
        }

        [Test]
        public void SparseMultiplierMaskNeverInventsIntermediateModesOrExceedsRequest()
        {
            var support = new[] { Support(FrameGenerationProviderKind.NvidiaDlss, (1u << 2) | (1u << 4) | (1u << 6)) };
            Assert.That(FrameGenerationPolicy.BuildChoices(support, Api),
                Is.EqualTo(new[] { "Off", "Auto", "DLSS 2x", "DLSS 4x" }));
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Dlss3x, support, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Dlss2x));
            Assert.That(FrameGenerationPolicy.Resolve(FrameGenerationMode.Auto, support, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Dlss4x));
        }

        [Test]
        public void DeviceApiRuntimeFailureAndUnknownVersionCannotEnableFg()
        {
            var support = new[] {
                Support(FrameGenerationProviderKind.NvidiaDlss, 28u, api: GraphicsDeviceType.Direct3D12),
                Support(FrameGenerationProviderKind.AmdFsr4, available: false),
                Support(FrameGenerationProviderKind.AmdFsr31, version: "")
            };
            Assert.That(FrameGenerationPolicy.BuildChoices(support, Api), Is.EqualTo(new[] { "Off" }));
            Assert.That(FrameGenerationPolicy.BuildChoices(support, GraphicsDeviceType.Null), Is.EqualTo(new[] { "Off" }));
        }

        [Test]
        public void OffAndInvalidValuesNeverEnableAutoOrAVendor()
        {
            var support = new[] { Support(FrameGenerationProviderKind.NvidiaDlss, 28u) };
            foreach (string value in new[] { null, "", "Off", "2", "DLSS 6x", "unknown" })
            {
                var parsed = FrameGenerationPolicy.Parse(value);
                Assert.That(parsed, Is.EqualTo(FrameGenerationMode.Off));
                Assert.That(FrameGenerationPolicy.Resolve(parsed, support, Api).Selected, Is.EqualTo(FrameGenerationMode.Off));
            }
            Assert.That(FrameGenerationPolicy.Resolve((FrameGenerationMode)100, support, Api).Selected,
                Is.EqualTo(FrameGenerationMode.Off));
            foreach (FrameGenerationMode value in Enum.GetValues(typeof(FrameGenerationMode)))
                Assert.That(FrameGenerationPolicy.Parse(FrameGenerationPolicy.Label(value)), Is.EqualTo(value));
        }
    }
}
