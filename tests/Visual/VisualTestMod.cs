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
        private static readonly string[] SettingFields =
        {
            "_modeEntry", "_supersamplingEntry", "_sharpnessEntry", "_taaStabilityEntry", "_dlaaPresetEntry",
            "_foliageMotionRepairEntry", "_mapViewAaEntry", "_cycleKeyEntry", "_hotkeysEntry"
        };

        public override void OnInitialized()
        {
            _mod = UnityEngine.Object.FindAnyObjectByType<ReduxBetterAAMod>();
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
                Bind(api, "stock_controls", (context, args) =>
                {
                    var result = new Table(script);
                    var claims = (IEnumerable)typeof(ReduxBetterAA.Patches.StockAntialiasingControlPatch)
                        .GetField("Claims", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    foreach (object claim in claims)
                    {
                        var component = (BaseSettingsMenuComponent)claim.GetType()
                            .GetField("Component", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(claim);
                        string label = (string)claim.GetType().GetField("AppliedLabel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(claim);
                        result.Set(label.StartsWith("Supersampling") ? "supersampling_disabled" : "aa_disabled", DynValue.NewBoolean(!component.enabledSelf));
                    }
                    return DynValue.NewTable(result);
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
                    result.Set("render_scale", DynValue.NewNumber(coordinator.AppliedRenderScalePercent));
                    result.Set("stock_scale", DynValue.NewNumber(KSP.Game.PersistentProfileManager.RenderScalePercent));
                    result.Set("msaa", DynValue.NewNumber(QualitySettings.antiAliasing));
                    result.Set("foliage_enabled", DynValue.NewBoolean(VegetationMotionCompatibility.Current.Enabled));
                    Camera resolve = coordinator.ResolveCamera;
                    result.Set("screen_width", DynValue.NewNumber(Screen.width));
                    result.Set("scene_width", DynValue.NewNumber(resolve == null ? 0 : resolve.targetTexture != null ? resolve.targetTexture.width : resolve.pixelWidth));
                    result.Set("temporal_hooks", DynValue.NewNumber(resolve == null ? 0 : resolve.GetComponents<TemporalRenderHook>().Length));
                    result.Set("report_busy", DynValue.NewBoolean(Phase1ProbeService.Current.IssueReportBusy));
                    result.Set("report_zip", DynValue.NewString(Phase1ProbeService.Current.LastIssueReport ?? ""));
                    result.Set("sharpness", DynValue.NewNumber(coordinator.CustomConfig.Sharpening));
                    result.Set("stability", DynValue.NewNumber(coordinator.CustomConfig.StationaryHistory));
                    result.Set("map_override", DynValue.NewBoolean(coordinator.MapViewAaOverrideActive));
                    result.Set("temporal_input_captures", DynValue.NewNumber(Phase1ProbeService.Current.TemporalInputCaptureCount));
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
                        bool graphics = args.Count > 1 && args[1].String == "graphics";
                        if (graphics ? !(menu is Redux.UI.Settings.Submenus.UitkGraphicsSettingsManager) :
                            menu.TitleLocalizationKey != "Redux Better AA") continue;
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


        private void OnDestroy()
        {
            _video?.Dispose();
            _registration?.Dispose();
        }
    }
}
