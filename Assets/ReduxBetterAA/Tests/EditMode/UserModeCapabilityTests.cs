using System.Collections.Generic;
using NUnit.Framework;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;

namespace ReduxBetterAA.Tests
{
    public sealed class UserModeCapabilityTests
    {
        [TestCase("FSR 3.1")]
        [TestCase("FSR 4")]
        [TestCase("FSR 4.1")]
        public void ActualProviderNamesRoundTripWithStableBackendIds(string provider)
        {
            string[] modes = UserSettingsPolicy.BuildModeChoices(true, true, provider);
            Assert.That(modes, Does.Contain(provider + " Native AA"));
            Assert.That(modes, Does.Contain(provider + " Upscaling"));
            foreach (string mode in modes)
            {
                BackendSelection backend = UserSettingsPolicy.ParseBackend(mode, true, true, provider);
                Assert.That(UserSettingsPolicy.TryGetModeForBackend(backend, true, true,
                    out string persisted, provider), Is.True);
                Assert.That(persisted, Is.EqualTo(mode));
            }
            Assert.That((int)UserSettingsPolicy.ParseBackend(provider + " Native AA", true, true), Is.EqualTo(7));
            Assert.That((int)UserSettingsPolicy.ParseBackend(provider + " Upscaling", true, true), Is.EqualTo(10));
        }

        [TestCase("FSR 2")]
        [TestCase("FSR2")]
        [TestCase("Unknown")]
        [TestCase("")]
        [TestCase(null)]
        public void LegacyOrUnknownRuntimeCannotAdvertiseAnAmdMode(string provider)
        {
            Assert.That(UserSettingsPolicy.BuildModeChoices(true, true, provider),
                Is.EqualTo(UserSettingsPolicy.BuildModeChoices(true, false)));
            Assert.That(UserSettingsPolicy.NormalizeMode("FSR 2 Native AA", true, true, provider),
                Is.EqualTo("Off"));
            Assert.That(UserSettingsPolicy.NormalizeMode("FSR 2 Upscaling", true, true, provider),
                Is.EqualTo("Off"));
            Assert.That(UserSettingsPolicy.TryGetMode(BackendSelection.AmdFsr2, true, true,
                out _, provider), Is.False);
        }

        [TestCase("FSR 2")]
        [TestCase("FSR 2 Native AA")]
        [TestCase("FSR3.1 Native AA")]
        [TestCase("FSR 3.1 Native AA")]
        [TestCase("FSR 4 Native AA")]
        [TestCase("FSR 4.1 Native AA")]
        [TestCase("AMD FSR Native AA")]
        [TestCase("AMD FSR 4.1 Native AA")]
        public void PersistedNativeModesMigrateToActualProvider(string saved)
        {
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, false, true, "FSR 4.1"),
                Is.EqualTo("FSR 4.1 Native AA"));
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, false, true, "FSR 3.1"),
                Is.EqualTo("FSR 3.1 Native AA"));
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, true, false, "FSR 4.1"), Is.EqualTo("Off"));
        }

        [TestCase("FSR 2 Upscaling")]
        [TestCase("FSR3.1 Upscaling")]
        [TestCase("FSR 3.1 Upscaling")]
        [TestCase("FSR 4 Upscaling")]
        [TestCase("FSR 4.1 Upscaling")]
        [TestCase("AMD FSR Upscaling")]
        [TestCase("AMD FSR 4.1 Upscaling")]
        public void PersistedUpscalingModesKeepIntentAcrossProviderChanges(string saved)
        {
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, false, true, "FSR 4.1"),
                Is.EqualTo("FSR 4.1 Upscaling"));
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, false, true, "FSR 3.1"),
                Is.EqualTo("FSR 3.1 Upscaling"));
            Assert.That(UserSettingsPolicy.NormalizeMode(saved, false, true, "FSR 4.1", false, false),
                Is.EqualTo("Off"));
        }

        [Test]
        public void UpscalingEligibilityCannotEnableAnUnsupportedVendor()
        {
            Assert.That(UserSettingsPolicy.BuildModeChoices(false, false, "FSR 4.1", true, true),
                Is.EqualTo(UserSettingsPolicy.BuildModeChoices(false, false)));
            Assert.That(UserSettingsPolicy.NormalizeMode(UserSettingsPolicy.ModeDlaa, false, false), Is.EqualTo("Off"));
            Assert.That(UserSettingsPolicy.NormalizeMode(UserSettingsPolicy.ModeDlss, false, true,
                "FSR 4.1", true, true), Is.EqualTo("Off"));
            Assert.That(UserSettingsPolicy.NormalizeMode("FSR 4.1 Upscaling", true, false,
                "FSR 4.1", true, true), Is.EqualTo("Off"));
        }

        [Test]
        public void NativeModesStayAvailableWhenOutputTransportCannotUpscale()
        {
            string[] choices = UserSettingsPolicy.BuildModeChoices(true, true, "FSR 4.1", false, false);
            Assert.That(choices, Does.Contain(UserSettingsPolicy.ModeDlaa));
            Assert.That(choices, Does.Contain("FSR 4.1 Native AA"));
            Assert.That(choices, Does.Not.Contain(UserSettingsPolicy.ModeDlss));
            Assert.That(choices, Does.Not.Contain("FSR 4.1 Upscaling"));
            Assert.That(UserSettingsPolicy.NormalizeMode(UserSettingsPolicy.ModeDlss,
                true, true, "FSR 4.1", false, false), Is.EqualTo("Off"));
            Assert.That(UserSettingsPolicy.TryGetMode(BackendSelection.NvidiaDlss,
                true, true, out _, "FSR 4.1", false, false), Is.False);
        }

        [TestCase(false, false, true, true)]
        [TestCase(true, false, false, true)]
        [TestCase(false, true, true, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, true, true, true)]
        public void ModeCycleContainsExactlyTheAvailableChoices(bool nvidia, bool amd, bool dlss, bool fsrUpscaling)
        {
            string[] choices = UserSettingsPolicy.BuildModeChoices(nvidia, amd, "FSR 4.1", dlss, fsrUpscaling);
            var seen = new HashSet<string>();
            BackendSelection current = BackendSelection.Off;
            for (int index = 0; index < choices.Length; index++)
            {
                Assert.That(UserSettingsPolicy.TryGetMode(current, nvidia, amd, out string label,
                    "FSR 4.1", dlss, fsrUpscaling), Is.True);
                Assert.That(seen.Add(label), Is.True, "The cycle repeated before visiting every available mode.");
                current = UserSettingsPolicy.NextBackend(current, nvidia, amd, dlss, fsrUpscaling);
            }
            Assert.That(current, Is.EqualTo(BackendSelection.Off));
            Assert.That(seen, Is.EquivalentTo(choices));
        }

        [Test]
        public void DebugMenuUsesCapabilitySnapshotAndRemovesUnavailableModes()
        {
            string[] previous = DebugMenu.Modes;
            try
            {
                string[] actual = UserSettingsPolicy.BuildModeChoices(false, true, "FSR 4.1", false, false);
                string[] expected = (string[])actual.Clone();
                DebugMenu.ConfigureModes(actual);
                actual[0] = "Mutated caller array";
                Assert.That(DebugMenu.Modes, Is.EqualTo(expected));
                Assert.That(DebugMenu.Backends, Has.No.Member(BackendSelection.NvidiaDlaa));
                Assert.That(DebugMenu.Backends, Has.No.Member(BackendSelection.NvidiaDlss));
                Assert.That(DebugMenu.Backends, Has.No.Member(BackendSelection.AmdFsrUpscaling));
                Assert.That(DebugMenu.ModeName(BackendSelection.AmdFsr2), Is.EqualTo("FSR 4.1 Native AA"));
                DebugMenu.ConfigureModes(null);
                Assert.That(DebugMenu.Modes, Is.EqualTo(UserSettingsPolicy.BuildModeChoices(false, false)));
                Assert.That(DebugMenu.Backends, Has.No.Member(BackendSelection.AmdFsr2));
            }
            finally
            {
                DebugMenu.ConfigureModes(previous);
            }
        }
    }
}
