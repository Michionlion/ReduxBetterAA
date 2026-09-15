using System;
using System.Collections.Generic;
using Redux.UI.Settings;
using Redux.UI.Settings.Component;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using ReduxLib.Configuration;
using KSP.Api.CoreTypes;
using UnityEngine.UIElements;

namespace ReduxBetterAA.Diagnostics
{
    internal static class FrameGenerationSettingsBinding
    {
        internal static IConfigEntry Entry;
    }

    // Redux's ordinary dropdown copies its options when built. This one row uses
    // the same Redux component template while refreshing asynchronous capabilities
    // in an already open menu. No private Redux fields or renderer claims.
    internal sealed class FrameGenerationSettingsDropdown : BaseSettingsMenuComponent, IPropertyBoundComponent<string>
    {
        private DropdownField _field;
        private string[] _choices=Array.Empty<string>();
        private bool _subscribed;
        public Property<string> Property { get; }

        internal FrameGenerationSettingsDropdown(IConfigEntry entry)
            : base("Assets/ReduxAssets/UI/Settings/Component/SettingsDropdown.uxml", entry.Description, FrameGenerationPolicy.SettingName)
        {
            Property=Utility.WrapConfigValue<string>(entry);
            BeginTreeInitialization();
        }
        protected override void OnTreeInitialized()
        {
            _field=this.Q<DropdownField>();
            _field.RegisterValueChangedCallback(OnDropdownChanged);
            Property.OnChangedValue += OnPropertyBoundChanged;
            FrameGenerationAvailability.ChoicesChanged += RefreshChoices;
            _subscribed=true;
            RefreshChoices();
        }
        protected override void RelocalizeElement() { RefreshChoices(); }
        private void RefreshChoices()
        {
            if (_field == null) return;
            _choices=FrameGenerationAvailability.BuildMenuChoices();
            _field.choices=new List<string>(_choices);
            _field.SetValueWithoutNotify(Property.GetValue());
        }
        private void OnDropdownChanged(ChangeEvent<string> change)
        {
            int index=_field.index;
            if (index < 0 || index >= _choices.Length) return;
            Property.SetValue(_choices[index]);
            InvokeChanged();
        }
        public void OnPropertyBoundChanged(string value) { _field?.SetValueWithoutNotify(value); }
        public override void Unbind()
        {
            if (!_subscribed) return;
            _subscribed=false;
            Property.OnChangedValue -= OnPropertyBoundChanged;
            FrameGenerationAvailability.ChoicesChanged -= RefreshChoices;
            _field?.UnregisterValueChangedCallback(OnDropdownChanged);
        }
        public override void OnDisabled() { _field?.SetEnabled(false); }
        public override void OnEnabled() { _field?.SetEnabled(true); }
    }
}
