using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WindowsAudioSwitcher.Rules;

public enum RuleKind
{
    ExactDevice,
    NameContains,
}

/// <summary>How a rule relates to the devices connected right now. UI-only; never persisted.</summary>
public enum RuleStatus
{
    /// <summary>No device snapshot yet.</summary>
    Unknown,
    /// <summary>No connected device matches this rule.</summary>
    NotConnected,
    /// <summary>A device matches, but a higher rule wins or this rule is turned off.</summary>
    Connected,
    /// <summary>This is the winning rule: its device is the one the engine selects.</summary>
    Active,
}

public sealed class Rule : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private RuleKind _kind = RuleKind.ExactDevice;
    private string _value = string.Empty;
    private string? _label;
    private bool _isEnabled = true;
    private RuleStatus _status = RuleStatus.Unknown;
    private string? _statusDevices;

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
        set { if (_isEnabled != value) { _isEnabled = value; OnChanged(); OnChanged(nameof(StatusText)); } }
    }

    [JsonIgnore]
    public string DisplayText => Kind switch
    {
        RuleKind.NameContains => $"Name contains: \"{Value}\"",
        _ => Label ?? Value,
    };

    [JsonIgnore]
    public RuleStatus Status => _status;

    /// <summary>Short sentence for the row's status dot tooltip and screen readers.</summary>
    [JsonIgnore]
    public string StatusText => _status switch
    {
        RuleStatus.Active       => $"In use: {_statusDevices}",
        RuleStatus.Connected    => IsEnabled
                                     ? $"Connected: {_statusDevices}. A higher rule wins."
                                     : $"Connected: {_statusDevices}. This rule is turned off.",
        RuleStatus.NotConnected => "Not connected",
        _                       => "Checking devices",
    };

    /// <summary>Called by the settings window after each device snapshot.</summary>
    public void SetStatus(RuleStatus status, string? deviceNames)
    {
        if (_status == status && _statusDevices == deviceNames) return;
        _status = status;
        _statusDevices = deviceNames;
        OnChanged(nameof(Status));
        OnChanged(nameof(StatusText));
    }

    /// <summary>Pure match. Does not consult <see cref="IsEnabled"/>; RuleEngine handles that.</summary>
    public bool Matches(string deviceId, string friendlyName) => Kind switch
    {
        RuleKind.ExactDevice => string.Equals(deviceId, Value, StringComparison.OrdinalIgnoreCase),
        RuleKind.NameContains => !string.IsNullOrWhiteSpace(Value) &&
            friendlyName.Contains(Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Copy of the persisted fields. The rule engine runs on a worker thread
    /// against copies so the UI can keep editing the originals.
    /// </summary>
    public Rule Clone() => new()
    {
        Kind = Kind,
        Value = Value,
        Label = Label,
        IsEnabled = IsEnabled,
    };

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
