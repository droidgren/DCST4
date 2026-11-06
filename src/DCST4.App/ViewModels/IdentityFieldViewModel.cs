using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DCST4.ViewModels
{
    /// <summary>
    /// One row in the Identifiers tab. While <see cref="IsAuto"/> is on, <see cref="Value"/> previews the generated value
    /// and is read-only. Turning it off lets the user type the value, which then replaces the generated one.
    /// </summary>
    public sealed class IdentityFieldViewModel : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private bool _isAuto = true;
        private bool _manualAllowed = true;
        private string _value = string.Empty;
        private string _manualValue;
        private string _toolTip;

        public IdentityFieldViewModel(string label, string hint, Action changed)
        {
            Label = label;
            Hint = hint;
            _toolTip = hint;
            _changed = changed;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public string Label { get; }
        public string Hint { get; }

        public bool IsAuto
        {
            get => _isAuto;
            set
            {
                if (_isAuto == value) return;
                _isAuto = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEditable));
                if (!value)
                {
                    // Restore what the user typed earlier. Otherwise the generated value is a good starting point,
                    // but an explanatory "(generated for each image)" is not a value.
                    if (_manualValue != null) SetDisplayed(_manualValue);
                    else if (_value.StartsWith("(", StringComparison.Ordinal)) SetDisplayed(string.Empty);
                }
                _changed();
            }
        }

        /// <summary>False when a manual value would make no sense for the current exam, e.g. one SOP Instance UID for a stack.</summary>
        public bool ManualAllowed
        {
            get => _manualAllowed;
            private set { _manualAllowed = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEditable)); }
        }

        public bool IsEditable => !_isAuto && _manualAllowed;

        public string ToolTip { get => _toolTip; private set { _toolTip = value; OnPropertyChanged(); } }

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value ?? string.Empty;
                OnPropertyChanged();
                if (!_isAuto)
                {
                    _manualValue = _value;
                    _changed();
                }
            }
        }

        /// <summary>The text entered by the user, or null while on Auto.</summary>
        public string ManualValue => _isAuto ? null : _value;

        /// <summary>Shows a generated value. Ignored while the user is entering their own.</summary>
        public void ShowGenerated(string value)
        {
            if (_isAuto) SetDisplayed(value ?? string.Empty);
        }

        /// <summary>Allows or forbids a manual value. Forbidding it puts the field back on Auto.</summary>
        public void SetManualAllowed(bool allowed, string reason)
        {
            if (allowed == _manualAllowed) return;
            ManualAllowed = allowed;
            ToolTip = allowed ? Hint : reason;
            if (!allowed && !_isAuto)
            {
                _isAuto = true;
                OnPropertyChanged(nameof(IsAuto));
                OnPropertyChanged(nameof(IsEditable));
            }
        }

        private void SetDisplayed(string value)
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged(nameof(Value));
        }
    }
}
