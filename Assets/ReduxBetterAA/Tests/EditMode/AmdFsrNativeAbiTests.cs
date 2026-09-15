using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Backends.Amd;
using ReduxBetterAA.Configuration;

namespace ReduxBetterAA.Tests
{
    public class AmdFsrNativeAbiTests
    {
        [Test]
        public void ManagedDescriptionsMatchNativeX64Layout()
        {
            Assert.That(IntPtr.Size, Is.EqualTo(8));
            Assert.That(AmdFsrNativeApi.DispatchSize, Is.EqualTo(136u));
            Assert.That(Marshal.SizeOf<AmdFsrNativeApi.CreateDescription>(), Is.EqualTo(40));
            Assert.That(Marshal.SizeOf<AmdFsrNativeApi.DispatchDescription>(), Is.EqualTo(136));
            Assert.That(Marshal.OffsetOf<AmdFsrNativeApi.DispatchDescription>("Context").ToInt32(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf<AmdFsrNativeApi.DispatchDescription>("FrameId").ToInt32(), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf<AmdFsrNativeApi.DispatchDescription>("JitterX").ToInt32(), Is.EqualTo(80));
            Assert.That(Marshal.OffsetOf<AmdFsrNativeApi.DispatchDescription>("Reset").ToInt32(), Is.EqualTo(124));
        }

        [TestCase(false, 1u)]
        [TestCase(true, 9u)]
        public void LinearHdrInputFlagsPreserveDepthConventionAndManualExposure(bool reversedDepth, uint expected)
        {
            // Official FSR API bits: HDR=0, inverted depth=3. Auto-exposure,
            // nonlinear input and display-resolution motion must remain disabled.
            Assert.That(AmdFsrNativeApi.GetContextFlags(reversedDepth), Is.EqualTo(expected));
        }

        [TestCase(false, 33u)]
        [TestCase(true, 41u)]
        public void AutomaticExposurePreservesLinearHdrAndDepthFlags(bool reversedDepth, uint expected) =>
            Assert.That(AmdFsrNativeApi.GetContextFlags(reversedDepth, true), Is.EqualTo(expected));

        [TestCase(true, true, true, 1.68f, 4f, true, false, 1.68f)]
        [TestCase(true, true, true, 0.01f, 4f, true, false, 0.2f)]
        [TestCase(true, true, true, 8f, 4f, true, false, 2f)]
        [TestCase(true, true, false, 1.68f, 4f, false, true, 1f)]
        [TestCase(true, false, true, 1.68f, 4f, false, true, 1f)]
        [TestCase(false, true, true, 1.68f, 4f, false, false, 4f)]
        [TestCase(true, true, true, float.NaN, 4f, false, true, 1f)]
        [TestCase(true, true, true, float.PositiveInfinity, 4f, false, true, 1f)]
        [TestCase(false, true, true, 1.68f, float.NaN, false, false, 1f)]
        public void ExposureSelectionHonorsSettingsAndFallsBackFromMissingOrInvalidSamples(
            bool automatic, bool preferPpv2, bool available, float sample, float manual,
            bool expectedPpv2, bool expectedAuto, float expectedPreExposure)
        {
            Fsr2Config config = Fsr2Config.Conservative.WithUserSettings(0.15f, manual, automatic)
                .WithExposurePreference(preferPpv2);
            AmdFsr2Backend.SelectExposure(in config, available, sample,
                out bool ppv2, out bool vendorAuto, out float preExposure);
            Assert.That(ppv2, Is.EqualTo(expectedPpv2));
            Assert.That(vendorAuto, Is.EqualTo(expectedAuto));
            Assert.That(preExposure, Is.EqualTo(expectedPreExposure));
        }

        [TestCase("3.1.5", "FSR 3.1")]
        [TestCase("4.1.1", "FSR 4.1")]
        [TestCase("4.0.0", "")]
        [TestCase("2.2.1", "")]
        [TestCase("unknown provider 4.1", "")]
        [TestCase(null, "")]
        public void ProviderLabelsRequireRecognizedRuntimeVersion(string actual, string expected) =>
            Assert.That(AmdFsrNativeApi.CanonicalProvider(actual), Is.EqualTo(expected));

        [TestCase(0u, 1, "3.1.5", "FSR 3.1")]
        [TestCase(0u, 1, "4.1.1", "FSR 4.1")]
        [TestCase(1u, 1, "4.1.1", "")]
        [TestCase(0u, 0, "3.1.5", "")]
        [TestCase(0u, 2, "3.1.5", "")]
        [TestCase(0u, 3, "3.1.5", "")]
        [TestCase(0u, 1, "2.2.1", "")]
        [TestCase(0u, 1, null, "")]
        public void ProviderAvailabilityRequiresSuccessfulReadyModernContext(
            uint result, int state, string actual, string expected)
        {
            bool available = AmdFsrNativeApi.TryResolveProvider(result, state, actual, out string provider);
            Assert.That(available, Is.EqualTo(expected.Length > 0));
            Assert.That(provider, Is.EqualTo(expected), "Failed probes must not leave a pretend provider label");
            if (!available)
                Assert.That(UserSettingsPolicy.NormalizeMode("FSR 2 Native AA", false, false, provider),
                    Is.EqualTo("Off"));
        }

        [TestCase("FSR 2", "FSR 3.1", "FSR 3.1 Native AA")]
        [TestCase("FSR 2 Native AA", "FSR 4.1", "FSR 4.1 Native AA")]
        [TestCase("FSR 2 Upscaling", "FSR 3.1", "Off")]
        [TestCase("FSR 2 Upscaling", "FSR 4.1", "Off")]
        public void LegacyLabelsSelectOnlyTheAvailableModernProvider(string saved, string provider, string expected)
        {
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, false, true, provider), Is.EqualTo(expected));
        }

        [Test]
        public void LegacyOnlyRuntimeDirectoryCannotSatisfyTheNativeBridgeLoader()
        {
            string directory = Path.Combine(Path.GetTempPath(), "BetterAA-runtime-policy-" + Guid.NewGuid().ToString("N"));
            string legacyPlugin = Path.Combine(directory, "UnityFsr2.dll");
            string legacyModule = Path.Combine(directory, "UnityEngine.AMDModule.dll");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllBytes(legacyPlugin, Array.Empty<byte>());
                File.WriteAllBytes(legacyModule, Array.Empty<byte>());
                FileNotFoundException failure = Assert.Throws<FileNotFoundException>(
                    () => AmdFsrNativeApi.FindRuntimeLibrary(directory));
                Assert.That(failure.FileName, Is.EqualTo(Path.Combine(directory, "ReduxBetterAA.FsrBridge.dll")));
                Assert.That(failure.Message, Does.Contain("complete Better AA ZIP"));
            }
            finally
            {
                File.Delete(legacyPlugin);
                File.Delete(legacyModule);
                Directory.Delete(directory);
            }
        }
    }
}
