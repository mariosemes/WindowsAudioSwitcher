using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WindowsAudioSwitcher.Rules;

public enum RuleKind
{
    ExactDevice,
    NameContains,
}

public sealed class Rule : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private RuleKind _kind = RuleKind.ExactDevice;
    private string _value = string.Empty;
    private string? _label;
    private bool _isEnabled = true;

    public RuleKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; OnChanged(); OnChanged(nameof(DisplayText)); } }
    }

    /// <summary>Device endpoint ID for ExactDevice, or substring for NameContains.</summary>
    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnChanged(); OnChanged(nameof(DisplayText)); } }
    }

    /// <summary>Friendly label shown in the UI. Optional.</summary>
    public string? Label
    {
        get => _label;
        set { if (_label != value) { _label = value; OnChanged(); OnChanged(nameof(DisplayText)); } }
    }

    /// <summary>If false, the rule is skipped during evaluation but kept in the priority list.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnChanged(); } }
    }

    [JsonIgnore]
    public string DisplayText => Kind switch
    {
        RuleKind.NameContains => $"Name contains: \"{Value}\"",
        _ => Label ?? Value,
    };

    /// <summary>Pure match — does not consult <see cref="IsEnabled"/>. Callers (RuleEngine) handle that.</summary>
    public bool Matches(string deviceId, string friendlyName) => Kind switch
    {
        RuleKind.ExactDevice => string.Equals(deviceId, Value, StringComparison.OrdinalIgnoreCase),
        RuleKind.NameContains => !string.IsNullOrWhiteSpace(Value) &&
            friendlyName.Contains(Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
