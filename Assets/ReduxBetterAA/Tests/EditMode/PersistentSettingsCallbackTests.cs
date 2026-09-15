using System;
using System.Reflection;
using NUnit.Framework;
using ReduxLib.Configuration;
using UnityEngine;

namespace ReduxBetterAA.Tests
{
    public sealed class PersistentSettingsCallbackTests
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void FrameGenerationResetDoesNotReplayUiValueOrApplyAntiAliasingSettings()
        {
            var gameObject = new GameObject("BetterAA FG callback test");
            try
            {
                var mod = gameObject.AddComponent<ReduxBetterAAMod>();
                var file = new JsonConfigFile(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
                var entry = new JsonConfigEntry(file, typeof(string), "", "DLSS 4x");
                entry.Value = "Off";
                Set(mod, "_frameGenerationEntry", entry);
                int callbacks = 0;
                entry.RegisterCallback((previous, current) => {
                    if (++callbacks > 12) throw new InvalidOperationException("Recursive FG callback");
                });
                entry.RegisterCallback((Action<object, object>)Delegate.CreateDelegate(
                    typeof(Action<object, object>), mod,
                    typeof(ReduxBetterAAMod).GetMethod("OnFrameGenerationSettingChanged", InstancePrivate)));
                var ui = Redux.UI.Settings.Utility.WrapConfigValue<string>(entry);
                entry.Value = entry.Default;
                Assert.That(entry.Value, Is.EqualTo("DLSS 4x"), "Setter must unwind before normalization");
                Assert.That(typeof(ReduxBetterAAMod).GetField("_pendingPersistentSettings", InstancePrivate).GetValue(mod),
                    Is.False, "FG must not queue AA changes or reclaim render scale");
                // Deliberately no AA entries/coordinator: touching their apply
                // path here would fail as well as violating ownership.
                Assert.DoesNotThrow(mod.ApplyPendingPersistentSettings);
                Assert.That(entry.Value, Is.EqualTo("DLSS 4x"), "Unavailable hardware must not erase user intent");
                Assert.That(ui.GetValue(), Is.EqualTo("DLSS 4x"));
                Assert.That(Rendering.FrameGenerationAvailability.Selected, Is.EqualTo(Configuration.FrameGenerationMode.Off));
                Assert.That(callbacks, Is.EqualTo(1));
                mod.ApplyPendingPersistentSettings();
                Assert.That(callbacks, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(gameObject); }
        }

        [Test]
        public void ResetNormalizesAfterReduxUiCallbackAndDoesNotReplayStaleValue()
        {
            var gameObject = new GameObject("BetterAA settings callback test");
            try
            {
                var mod = gameObject.AddComponent<ReduxBetterAAMod>();
                // No registered sections means JsonConfigFile.Save returns
                // without file I/O, while using Redux's real entry callbacks.
                var file = new JsonConfigFile(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
                var mode = new JsonConfigEntry(file, typeof(string), "", "FSR 4.1 Native AA");
                mode.Value = "FSR 3.1 Native AA";
                Set(mod, "_modeEntry", mode);
                Set(mod, "_dlaaPresetEntry", new JsonConfigEntry(file, typeof(string), "", "K"));
                Set(mod, "_cycleKeyEntry", new JsonConfigEntry(file, typeof(string), "", "None"));
                Set(mod, "_dlaaSelectable", true);
                Set(mod, "_fsr2Selectable", true);
                Set(mod, "_upscalingSelectable", true);
                Set(mod, "_fsrProviderName", "FSR 3.1");

                int callbacks = 0;
                mode.RegisterCallback((previous, current) =>
                {
                    // Fail safely if normalization regresses into recursive
                    // Redux UI feedback, before it overflows the test runner.
                    if (++callbacks > 12) throw new InvalidOperationException("Recursive settings callback");
                });
                var onChanged = (Action<object, object>)Delegate.CreateDelegate(
                    typeof(Action<object, object>), mod,
                    typeof(ReduxBetterAAMod).GetMethod("OnPersistentSettingChanged", InstancePrivate));
                mode.RegisterCallback(onChanged);
                var uiProperty = Redux.UI.Settings.Utility.WrapConfigValue<string>(mode);

                Assert.DoesNotThrow(() => mode.Value = mode.Default);
                Assert.That(mode.Value, Is.EqualTo("FSR 4.1 Native AA"), "Original setter must unwind first");
                Assert.That(uiProperty.GetValue(), Is.EqualTo(mode.Value));
                Assert.That(callbacks, Is.EqualTo(1));

                Assert.DoesNotThrow(mod.ApplyPendingPersistentSettings);
                Assert.That(mode.Value, Is.EqualTo("FSR 3.1 Native AA"));
                Assert.That(uiProperty.GetValue(), Is.EqualTo(mode.Value), "UI and persisted value must converge");
                Assert.That(callbacks, Is.EqualTo(2));
                mod.ApplyPendingPersistentSettings();
                Assert.That(callbacks, Is.EqualTo(2), "Changes are coalesced and consumed once");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void Set(object target, string name, object value) =>
            typeof(ReduxBetterAAMod).GetField(name, InstancePrivate).SetValue(target, value);
    }
}
