using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NUnit.Framework;
using Redux.UI.Settings;
using Redux.UI.Settings.Component;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Patches;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;
using UnityEngine;
using UnityEngine.UIElements;

namespace ReduxBetterAA.Tests
{
    public sealed class FrameGenerationSettingsDropdownTests
    {
        private const string AssetStubOwner = "ReduxBetterAA.Tests.FrameGenerationSettingsAssets";
        private Harmony _settings, _assetStub;
        private IConfigEntry _previousEntry;
        private readonly List<BaseSettingsMenuComponent> _rows = new List<BaseSettingsMenuComponent>();
        private static int _assetRequests;
        private GameObject _documentObject;
        private PanelSettings _panelSettings;

        [SetUp]
        public void SetUp()
        {
            _previousEntry = FrameGenerationSettingsBinding.Entry;
            _assetRequests = 0;
            FrameGenerationAvailability.SetNvidiaSupport(0);
            FrameGenerationAvailability.SetAmdSupport(0, 0, 0, 0, 0, 0);
            FrameGenerationAvailability.SetRequested("DLSS 3x");
            // Exercise the actual Redux factory/component, without asking the
            // editor to load player-only Addressables. The field is attached
            // explicitly at the same protected tree-initialized seam below.
            _assetStub = new Harmony(AssetStubOwner);
            _assetStub.Patch(AccessTools.Method(typeof(SettingsElementCache), nameof(SettingsElementCache.GetVisualTreeAsset)),
                prefix: new HarmonyMethod(typeof(FrameGenerationSettingsDropdownTests), nameof(DeferPlayerAsset)));
            _settings = FrameGenerationSettingsPatch.Install();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var row in _rows) row.Unbind();
            _rows.Clear();
            if (_documentObject != null) UnityEngine.Object.DestroyImmediate(_documentObject);
            if (_panelSettings != null) UnityEngine.Object.DestroyImmediate(_panelSettings);
            _settings?.UnpatchAll(_settings.Id);
            _assetStub?.UnpatchAll(_assetStub.Id);
            FrameGenerationSettingsBinding.Entry = _previousEntry;
            FrameGenerationAvailability.SetNvidiaSupport(0);
            FrameGenerationAvailability.SetAmdSupport(0, 0, 0, 0, 0, 0);
            FrameGenerationAvailability.SetRequested("Off");
        }

        private static bool DeferPlayerAsset() { ++_assetRequests; return false; }

        private static IConfigEntry Entry(string value)
        {
            var file = new JsonConfigFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
            return new JsonConfigEntry(file, typeof(string), "Frame generation", value);
        }

        private void Build(IConfigEntry entry)
        {
            var builder = new SettingsSubMenuBuilder(new VisualElement(), label => { }, component => _rows.Add(component));
            UitkPropertyDrawers.BuildDropdownFor(FrameGenerationPolicy.SettingName, entry, builder,
                new ListConstraint<string>(new[] { "Off", "DLSS 3x" }));
        }

        [Test]
        public void PreInitializationInstallsOnlyOneSettingsPrefixBeforeTheLoaderInitHooks()
        {
            FrameGenerationSettingsPatch.Install();
            var target = AccessTools.Method(typeof(UitkPropertyDrawers), nameof(UitkPropertyDrawers.BuildDropdownFor));
            Assert.That(Harmony.GetPatchInfo(target).Prefixes.Count(p => p.owner == FrameGenerationSettingsPatch.Owner), Is.EqualTo(1));
            Assert.That(typeof(FrameGenerationSettingsPatch).GetCustomAttributes(typeof(HarmonyPatch), false), Is.Empty,
                "Later assembly PatchAll must not install the UI prefix under the renderer owner");
            var installer = AccessTools.Method(typeof(FrameGenerationSettingsPatch), nameof(FrameGenerationSettingsPatch.Install));
            var pre = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ReduxBetterAAMod), nameof(ReduxBetterAAMod.OnPreInitialized)));
            var initialized = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ReduxBetterAAMod), nameof(ReduxBetterAAMod.OnInitialized)));
            Assert.That(pre.Any(i => i.opcode == OpCodes.Call && Equals(i.operand, installer)), Is.True,
                "Settings registration InitHooks precede OnInitialized, so its UI patch must already exist");
            Assert.That(initialized.Any(i => i.opcode == OpCodes.Call && Equals(i.operand, installer)), Is.False);
            Assert.That(Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method).Owners.Contains(FrameGenerationSettingsPatch.Owner)),
                Is.EquivalentTo(new[] { target }), "The early owner must not install renderer patches");
        }

        [Test]
        public void EarlyBuiltRowRefreshesAsyncChoicesAndWritesTheExistingSetting()
        {
            var entry = Entry("DLSS 3x");
            FrameGenerationSettingsBinding.Entry = entry;
            Build(entry);
            Assert.That(_rows.Count, Is.EqualTo(1));
            Assert.That(_rows[0], Is.TypeOf<FrameGenerationSettingsDropdown>());
            Assert.That(_assetRequests, Is.EqualTo(1));
            var row = (FrameGenerationSettingsDropdown)_rows[0];
            // A detached VisualElement drops SendEvent in this pinned Unity
            // version. Use a real disposable panel so field.value exercises its
            // registered ChangeEvent callback, as an actual menu selection does.
            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _documentObject = new GameObject("Frame generation settings test");
            var document = _documentObject.AddComponent<UIDocument>();
            document.panelSettings = _panelSettings;
            document.rootVisualElement.Add(row);
            var field = new DropdownField();
            row.Add(field);
            Assert.That(field.panel, Is.Not.Null);
            AccessTools.Method(typeof(FrameGenerationSettingsDropdown), "OnTreeInitialized").Invoke(row, null);
            Assert.That(field.choices, Is.EqualTo(new[] { "Off", "DLSS 3x" }));
            FrameGenerationAvailability.SetNvidiaSupport(28);
            FrameGenerationAvailability.SetAmdSupport(4, 3, 123, 4, 3, 123);
            Assert.That(field.choices, Is.EqualTo(FrameGenerationAvailability.BuildMenuChoices()));
            Assert.That(field.choices, Does.Contain("Auto").And.Contain("DLSS 4x").And.Contain("FSR 3.1 2x"));
            field.value = "FSR 3.1 2x";
            Assert.That(entry.Value, Is.EqualTo("FSR 3.1 2x"));
            row.Unbind();
            var choices = field.choices.ToArray();
            FrameGenerationAvailability.SetNvidiaSupport(0);
            Assert.That(field.choices, Is.EqualTo(choices), "An unbound row must not retain the catalog callback");
        }

        [Test]
        public void OtherSettingsKeepTheirOrdinaryReduxDropdown()
        {
            FrameGenerationSettingsBinding.Entry = Entry("Off");
            Build(Entry("DLSS 3x"));
            Assert.That(_rows.Count, Is.EqualTo(1));
            Assert.That(_rows[0], Is.TypeOf<SettingsDropdown<string>>());
        }
    }
}
