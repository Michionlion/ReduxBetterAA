using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
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
        [TestCase("FSR 2 Upscaling", "FSR 3.1", "FSR 3.1 Upscaling")]
        [TestCase("FSR 2 Upscaling", "FSR 4.1", "FSR 4.1 Upscaling")]
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
                Assert.That(failure.Message, Does.Contain("FSR 4.1/3.1 runtime"));
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
