using System;
using System.Collections;
using System.Reflection;
using MoonSharp.Interpreter;
using ReduxBetterAA;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;
using ReduxTestHarness;
using SpaceWarp2.API.Mods;
using UnityEngine;
using UnityEngine.UIElements;
using Redux.UI.Settings;
using Redux.UI.Settings.Component;
using HarmonyLib;

// Test-only plugin. Never included in the beta payload.
namespace ReduxBetterAAVisualTests
{
    public sealed class VisualTestMod : MonoBehaviourMod
    {
        private IDisposable _registration;
        private ReduxBetterAAMod _mod;
        private UitkSettingsMenuManager _settingsMenu;
        private object _settingsInfo;
        private SampledVideo _video;
        private Harmony _captureHarmony;
        private static bool _suppressCloudGuard;
        private static readonly string[] SettingFields =
        {
            "_modeEntry", "_sharpnessEntry", "_taaStabilityEntry", "_dlaaPresetEntry",
            "_foliageMotionRepairEntry", "_mapViewAaEntry", "_cycleKeyEntry", "_hotkeysEntry"
        };

        public override void OnInitialized()
        {
            _mod = UnityEngine.Object.FindAnyObjectByType<ReduxBetterAAMod>();
            _captureHarmony = new Harmony("ReduxBetterAA.VisualTests.CloudIsolation");
            Type dlaaType = typeof(TemporalCoordinator).Assembly.GetType("ReduxBetterAA.Backends.NvidiaDlaaBackend", true);
            _captureHarmony.Patch(dlaaType.GetMethod("NotifyCloudRenderResolution", BindingFlags.Instance | BindingFlags.NonPublic),
                prefix: new HarmonyMethod(typeof(VisualTestMod).GetMethod(nameof(AllowCloudObservation), BindingFlags.Static | BindingFlags.NonPublic)));
            _registration = TestApiRegistry.Register("ReduxBetterAA.Beta", (script, api) =>
            {
                Bind(api, "modes", (context, args) =>
                {
                    bool dlaa = (bool)typeof(ReduxBetterAAMod).GetField("_dlaaSelectable", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_mod);
                    bool fsr2 = (bool)typeof(ReduxBetterAAMod).GetField("_fsr2Selectable", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_mod);
                    var modes = new Table(script);
                    foreach (string mode in UserSettingsPolicy.BuildModeChoices(dlaa, fsr2))
                        modes.Append(DynValue.NewString(mode));
                    return DynValue.NewTable(modes);
                });
                Bind(api, "settings", (context, args) =>
                {
                    var values = new Table(script);
                    foreach (string field in SettingFields)
                        values.Set(field, DynValue.FromObject(script, Entry(field).Value));
                    return DynValue.NewTable(values);
                });
                Bind(api, "set_settings", (context, args) =>
                {
                    foreach (string field in SettingFields)
                    {
                        DynValue value = args[0].Table.Get(field);
                        if (value.IsNil()) continue;
                        IConfigEntry entry = Entry(field);
                        entry.Value = Convert.ChangeType(value.ToObject(), entry.Value.GetType());
                    }
                    return DynValue.Nil;
                });
                Bind(api, "snapshot", (context, args) =>
                {
                    TemporalCoordinator coordinator = TemporalCoordinator.Current;
                    var result = new Table(script);
                    result.Set("requested", DynValue.NewString(coordinator.RequestedBackend.ToString()));
                    result.Set("selected", DynValue.NewString(coordinator.SelectedBackend));
                    result.Set("active", DynValue.NewBoolean(coordinator.Active));
                    result.Set("status", DynValue.NewString(coordinator.Status));
                    result.Set("report_busy", DynValue.NewBoolean(Phase1ProbeService.Current.IssueReportBusy));
                    result.Set("report_zip", DynValue.NewString(Phase1ProbeService.Current.LastIssueReport ?? ""));
                    result.Set("sharpness", DynValue.NewNumber(coordinator.CustomConfig.Sharpening));
                    result.Set("stability", DynValue.NewNumber(coordinator.CustomConfig.StationaryHistory));
                    result.Set("map_override", DynValue.NewBoolean(coordinator.MapViewAaOverrideActive));
                    result.Set("temporal_input_captures", DynValue.NewNumber(Phase1ProbeService.Current.TemporalInputCaptureCount));
                    result.Set("test_cloud_guard_suppressed", DynValue.NewBoolean(_suppressCloudGuard));
                    int targets = 0, lost = 0;
                    foreach (object owner in coordinator.CaptureBufferOwners())
                        foreach (FieldInfo field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                            if (field.FieldType == typeof(RenderTexture) && field.GetValue(owner) is RenderTexture texture)
                            {
                                targets++;
                                if (!texture.IsCreated()) lost++;
                            }
                    result.Set("owned_targets", DynValue.NewNumber(targets));
                    result.Set("lost_targets", DynValue.NewNumber(lost));
                    return DynValue.NewTable(result);
                });
                Bind(api, "report", (context, args) =>
                {
                    return DynValue.NewBoolean(Phase1ProbeService.Current.RequestIssueReport());
                });
                Bind(api, "release_owned_targets", (context, args) =>
                {
                    int count = 0;
                    foreach (object owner in TemporalCoordinator.Current.CaptureBufferOwners())
                        foreach (FieldInfo field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                            if (field.FieldType == typeof(RenderTexture) && field.GetValue(owner) is RenderTexture texture && texture.IsCreated())
                            {
                                texture.Release();
                                count++;
                            }
                    return DynValue.NewNumber(count);
                });
                Bind(api, "panel", (context, args) =>
                {
                    Phase1ProbeService.Current.SetPanelVisible(args[0].CastToBool());
                    return DynValue.Nil;
                });
                Bind(api, "settings_menu_ready", (context, args) =>
                {
                    var menu = KSP.Game.GameManager.Instance.Game.SettingsMenuManager;
                    return DynValue.NewBoolean(menu != null && (bool)typeof(UitkSettingsMenuManager)
                        .GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(menu));
                });
                Bind(api, "settings_ui", (context, args) =>
                {
                    if (!args[0].CastToBool())
                    {
                        if (_settingsMenu != null) _settingsMenu.Close();
                        _settingsInfo = null;
                        return DynValue.Nil;
                    }
                    _settingsMenu = KSP.Game.GameManager.Instance.Game.SettingsMenuManager;
                    if (_settingsMenu == null) throw new InvalidOperationException("Redux settings menu is unavailable.");
                    _settingsMenu.Show(SettingsLocations.MainMenu);
                    // Select the registered production page; never recreate its controls.
                    var menus = (IEnumerable)typeof(UitkSettingsMenuManager)
                        .GetField("_mainMenuSubMenus", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_settingsMenu);
                    foreach (object item in menus)
                    {
                        var menu = (UitkSettingsSubMenu)item.GetType().GetField("Item1").GetValue(item);
                        if (menu.TitleLocalizationKey != "Redux Better AA") continue;
                        _settingsInfo = item.GetType().GetField("Item2").GetValue(item);
                        _settingsMenu.ShowSubMenu(menu);
                        return DynValue.NewBoolean(_settingsMenu.IsVisible);
                    }
                    throw new InvalidOperationException("Redux Better AA settings page was not registered.");
                });
                Bind(api, "settings_ui_ready", (context, args) =>
                    DynValue.NewBoolean(SettingsControl("Mode").Q<DropdownField>() != null &&
                        SettingsControl("Sharpness").Q<SliderInt>() != null &&
                        SettingsControl("DLAA preset").Q<DropdownField>() != null));
                Bind(api, "settings_ui_set", (context, args) =>
                {
                    var control = SettingsControl(args[0].String);
                    var dropdown = control.Q<DropdownField>();
                    if (dropdown != null)
                    {
                        string value = args[1].String;
                        if (!dropdown.choices.Contains(value)) throw new InvalidOperationException("Unavailable setting choice: " + value);
                        dropdown.value = value;
                    }
                    else
                    {
                        var slider = control.Q<SliderInt>();
                        if (slider == null) throw new InvalidOperationException("Expected a dropdown or slider.");
                        // Slider values are integer ticks; sharpness uses 0..100.
                        slider.value = checked((int)args[1].Number);
                    }
                    return DynValue.Nil;
                });
                Bind(api, "video_start", (context, args) =>
                {
                    if (_video != null) throw new InvalidOperationException("Video capture already started.");
                    _video = new SampledVideo();
                    _video.Start();
                    return DynValue.Nil;
                });
                Bind(api, "video_frame", (context, args) => { _video.QueueFrame(this); return DynValue.Nil; });
                Bind(api, "video_ready", (context, args) => DynValue.NewBoolean(_video.FrameReady()));
                Bind(api, "video_finish", (context, args) => DynValue.NewString(_video.Finish()));
                Bind(api, "video_dispose", (context, args) =>
                {
                    _video?.Dispose(); _video = null;
                    return DynValue.Nil;
                });
                Bind(api, "set_cloud_guard_suppressed", (context, args) =>
                {
                    if (TemporalCoordinator.Current.SelectedBackend != "Off")
                        throw new InvalidOperationException("Select Off before changing the test-only guard override.");
                    _suppressCloudGuard = args[0].CastToBool();
                    return DynValue.Nil;
                });
            });
        }

        private IConfigEntry Entry(string field) => (IConfigEntry)typeof(ReduxBetterAAMod)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_mod);

        private BaseSettingsMenuComponent SettingsControl(string label)
        {
            if (_settingsInfo == null) throw new InvalidOperationException("Open the native settings page first.");
            var components = (IEnumerable)_settingsInfo.GetType().GetProperty("Components").GetValue(_settingsInfo);
            foreach (object item in components)
            {
                var component = item as BaseSettingsMenuComponent;
                if (component == null) continue;
                string key = (string)typeof(BaseSettingsMenuComponent)
                    .GetField("LabelKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(component);
                if (key == label) return component;
            }
            throw new InvalidOperationException("Native settings control not found: " + label);
        }

        private static void Bind(Table table, string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> call)
        {
            table.Set(name, TestApiRegistry.Callback("BetterAA.Beta." + name, call));
        }

        private static bool AllowCloudObservation() => !_suppressCloudGuard;

        private void OnDestroy()
        {
            _video?.Dispose();
            _registration?.Dispose();
            _captureHarmony?.UnpatchAll("ReduxBetterAA.VisualTests.CloudIsolation");
            _suppressCloudGuard = false;
        }
    }
}
