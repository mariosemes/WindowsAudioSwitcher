using System.Windows;
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
/// </summary>
public partial class AddRuleDialog : FluentWindow
{
    /// <summary>The constructed rule, or null if the dialog was cancelled.</summary>
    public Rule? Result { get; private set; }

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

        var list = devices.ToList();
        DeviceList.ItemsSource = list;
        if (list.Count == 0)
        {
            DeviceEmptyText.Visibility = Visibility.Visible;
            DeviceList.IsEnabled = false;
        }

        if (existingRule != null)
        {
            OkButton.Content = "Save changes";
            PrePopulate(existingRule, list);
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
            else if (list.Count == 0)
            {
                NameInput.Focus();
            }
        };
    }

    private void PrePopulate(Rule rule, List<AudioDevice> connectedDevices)
    {
        if (rule.Kind == RuleKind.ExactDevice)
        {
            var match = connectedDevices.FirstOrDefault(d =>
                string.Equals(d.Id, rule.Value, StringComparison.OrdinalIgnoreCase));
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
        }
        UpdateOkEnabled();
    }

    private void DeviceList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is AudioDevice) Commit();
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
        UpdateOkEnabled();
    }

    // Tabbing or clicking into the empty textbox shouldn't yank a selection
    // away unless the user actually types — handled in TextChanged above.
    private void NameInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { }

    private void UpdateOkEnabled()
    {
        OkButton.IsEnabled = DeviceList.SelectedItem is AudioDevice
                          || !string.IsNullOrWhiteSpace(NameInput.Text);
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        // Name input takes priority only if a device isn't selected — the
        // mutual-exclusion logic above guarantees at most one is set anyway.
        if (DeviceList.SelectedItem is AudioDevice device)
        {
            Result = new Rule
            {
                Kind  = RuleKind.ExactDevice,
                Value = device.Id,
                Label = device.FriendlyName,
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
}
