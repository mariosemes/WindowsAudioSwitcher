using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using Wpf.Ui.Controls;
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Rules;

namespace WindowsAudioSwitcher;

/// <summary>
/// Unified "add a rule" modal. Lets the user either pick an exact connected
/// device (top section) or type a name-contains substring (bottom section).
/// The two sections are mutually exclusive: interacting with one clears the
/// other so the OK button always knows which kind of rule to produce.
/// While the user types a name, the devices it would match are highlighted.
/// </summary>
public partial class AddRuleDialog : FluentWindow
{
    /// <summary>The constructed rule, or null if the dialog was cancelled.</summary>
    public Rule? Result { get; private set; }

    private readonly List<DeviceChoice> _choices;
    private bool _syncing;

    /// <param name="existingRule">
    /// If non-null, the dialog opens in edit mode: the matching device or name
    /// text is pre-populated and the OK button reads "Save changes".
    /// </param>
    public AddRuleDialog(string title, IEnumerable<AudioDevice> devices, Rule? existingRule = null)
    {
        InitializeComponent();
        Title = title;
        TitleBar.Title = title;

        _choices = devices.Select(d => new DeviceChoice(d)).ToList();
        DeviceList.ItemsSource = _choices;
        if (_choices.Count == 0)
        {
            DeviceEmptyText.Visibility = Visibility.Visible;
            DeviceList.IsEnabled = false;
        }

        if (existingRule != null)
        {
            OkButton.Content = "Save changes";
            PrePopulate(existingRule);
        }

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(NameInput.Text))
            {
                NameInput.Focus();
                NameInput.SelectAll();
            }
            else if (DeviceList.SelectedItem != null)
            {
                DeviceList.Focus();
            }
            else if (_choices.Count == 0)
            {
                NameInput.Focus();
            }
        };
    }

    private void PrePopulate(Rule rule)
    {
        if (rule.Kind == RuleKind.ExactDevice)
        {
            var match = _choices.FirstOrDefault(c =>
                string.Equals(c.Device.Id, rule.Value, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                DeviceList.SelectedItem = match;
            }
            else
            {
                // The pinned device isn't connected right now. Surface that
                // so the user understands why nothing's selected in the list.
                var label = string.IsNullOrWhiteSpace(rule.Label) ? rule.Value : rule.Label;
                IntroText.Text = $"This rule pins \"{label}\", which isn't currently connected. " +
                                 "Pick a different connected device below, or switch to a name match.";
            }
        }
        else // NameContains
        {
            _syncing = true;
            NameInput.Text = rule.Value;
            _syncing = false;
            UpdateNamePreview();
        }
        UpdateOkEnabled();
    }

    // Selecting a device implies the user wants an exact-device rule, so
    // clear the name input to avoid an ambiguous "both are filled" state.
    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (DeviceList.SelectedItem is not null)
        {
            _syncing = true;
            NameInput.Text = string.Empty;
            _syncing = false;
            UpdateNamePreview();
        }
        UpdateOkEnabled();
    }

    private void DeviceList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceChoice) Commit();
    }

    // Typing in the name box implies the user wants a name rule. The first
    // keystroke that produces non-empty text wins over a stale device selection.
    private void NameInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!string.IsNullOrWhiteSpace(NameInput.Text) && DeviceList.SelectedItem is not null)
        {
            _syncing = true;
            DeviceList.SelectedItem = null;
            _syncing = false;
        }
        UpdateNamePreview();
        UpdateOkEnabled();
    }

    /// <summary>
    /// Highlights the connected devices the typed text matches and explains
    /// the outcome. Several matches is the common Bluetooth trap: a headset's
    /// Stereo and Hands-Free endpoints both contain the brand name.
    /// </summary>
    private void UpdateNamePreview()
    {
        var needle = NameInput.Text.Trim();
        var probe = new Rule { Kind = RuleKind.NameContains, Value = needle };
        var matches = new List<DeviceChoice>();
        foreach (var c in _choices)
        {
            c.IsNameMatch = needle.Length > 0 && probe.Matches(c.Device.Id, c.Device.FriendlyName);
            if (c.IsNameMatch) matches.Add(c);
        }

        if (needle.Length == 0)
        {
            NameMatchSummary.Visibility = Visibility.Collapsed;
            return;
        }
        NameMatchSummary.Visibility = Visibility.Visible;
        NameMatchSummary.Text = matches.Count switch
        {
            0 => "No connected device matches right now. The rule takes effect when one connects.",
            1 => $"Matches 1 connected device: {matches[0].FriendlyName}.",
            _ => $"Matches {matches.Count} connected devices: {string.Join(", ", matches.Select(m => m.FriendlyName))}. " +
                 "If the current default is one of them it stays; otherwise the first one listed is used. " +
                 "Type more of the name to target just one.",
        };
        // WPF doesn't raise live-region events by itself when Text changes.
        var peer = UIElementAutomationPeer.FromElement(NameMatchSummary)
                   ?? UIElementAutomationPeer.CreatePeerForElement(NameMatchSummary);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateOkEnabled()
    {
        OkButton.IsEnabled = DeviceList.SelectedItem is DeviceChoice
                          || !string.IsNullOrWhiteSpace(NameInput.Text);
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        // The mutual-exclusion logic above guarantees at most one is set.
        if (DeviceList.SelectedItem is DeviceChoice choice)
        {
            Result = new Rule
            {
                Kind  = RuleKind.ExactDevice,
                Value = choice.Device.Id,
                Label = choice.Device.FriendlyName,
            };
        }
        else if (!string.IsNullOrWhiteSpace(NameInput.Text))
        {
            Result = new Rule
            {
                Kind  = RuleKind.NameContains,
                Value = NameInput.Text.Trim(),
            };
        }
        else
        {
            return; // OK shouldn't be clickable in this state, but be defensive.
        }

        DialogResult = true;
        Close();
    }

    /// <summary>A device row in the picker, with the live name-match highlight.</summary>
    public sealed class DeviceChoice : INotifyPropertyChanged
    {
        private bool _isNameMatch;

        public DeviceChoice(AudioDevice device) => Device = device;

        public AudioDevice Device { get; }
        public string FriendlyName => Device.FriendlyName;

        public bool IsNameMatch
        {
            get => _isNameMatch;
            set { if (_isNameMatch != value) { _isNameMatch = value; OnChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override string ToString() => FriendlyName;
    }
}
