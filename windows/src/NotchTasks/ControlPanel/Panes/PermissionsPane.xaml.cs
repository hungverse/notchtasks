using System.ComponentModel;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Permissions pane (Preferences.swift PermissionList): the permissions the chosen modules need, the ones allowed, and the
/// ones not used. Each row shows its state and the one button that fixes it. The states are read when the pane shows, when the
/// Control Panel is activated (the user came back from Settings) and on Refresh; the firewall rule is read off the UI thread.
public partial class PermissionsPane : SettingsPane
{
    private static readonly PermissionFacts Unknown = new(null, false, null, null, null,
        new Dictionary<AgentKind, bool?>(), new Dictionary<AgentKind, bool>());

    /// The firewall probe (netsh) runs at most this often when the pane shows; Refresh and a finished grant always run it.
    private static readonly TimeSpan FirewallMaxAge = TimeSpan.FromSeconds(10);

    private readonly IRegistry _registry = new RegistryAccess();
    private readonly SensorHelper _helper;
    private PermissionFacts? _facts;
    private int _probe;
    private DateTime _firewallProbedAt = DateTime.MinValue;

    /// The pane for the settings store and the temperature helper (its state fills the Temperatures row).
    public PermissionsPane(SettingsStore store, SensorHelper helper) : base(store)
    {
        _helper = helper;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _helper.StateChanged -= OnHelperChanged;
            _helper.StateChanged += OnHelperChanged;
            Refresh(firewall: DateTime.UtcNow - _firewallProbedAt > FirewallMaxAge);
        };
        Unloaded += (_, _) => _helper.StateChanged -= OnHelperChanged;
        RefreshButton.Click += (_, _) => Refresh(firewall: true);
    }

    /// The helper turned on, off or declined: the row follows it while the pane shows.
    private void OnHelperChanged() => Refresh(firewall: false);

    protected override void Apply() => Render();

    /// The registry and file facts only: netsh is not started by a window activation.
    protected override void OnWindowActivated() => Refresh(firewall: false);

    /// Reads the states again. The registry and the agents' files are read at once. The firewall rule (netsh) is read only
    /// when `firewall` is true; it keeps its last answer otherwise.
    private void Refresh(bool firewall)
    {
        var previousRule = _facts?.FirewallRule;
        _facts = PermissionProbe.Facts(_registry, AppFiles.Home, AppFiles.HookExe, AppFiles.AppExe)
            with { FirewallRule = previousRule, Temperatures = TemperatureState() };
        ApplyStored();
        if (firewall && PermissionItems.FirewallRowShown)
        {
            _firewallProbedAt = DateTime.UtcNow;
            _ = ReadFirewallAsync(++_probe);
        }
    }

    private async Task ReadFirewallAsync(int probe)
    {
        var appExe = AppFiles.AppExe;
        var rule = await Task.Run(() => PermissionProbe.FirewallRule(appExe));
        if (probe != _probe || !IsLoaded || _facts is null)  // a later probe, or the pane was hidden: drop this answer
        {
            return;
        }
        _facts = _facts with { FirewallRule = rule };
        ApplyStored();
    }

    /// Builds the three groups from the last facts and the enabled modules.
    private void Render()
    {
        var facts = _facts ?? Unknown;
        // Agents that are not installed here are hidden, as the Mac's permission list has no rows for them.
        var items = PermissionItems.Build(facts).Where(item => item.Applies).ToList();
        var modules = ModuleCatalog.All.Where(id => !Store.Disabled.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var notifications = Store.SystemNotifications;

        Groups.Children.Clear();
        foreach (var group in new[] { PermGroup.Needed, PermGroup.Granted, PermGroup.NotInUse })
        {
            var rows = items.Where(item => PermissionItems.GroupOf(item, modules, notifications) == group).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            Groups.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("SectionHeader"),
                Text = GroupTitle(group),
            });
            var rowsPanel = new StackPanel();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    rowsPanel.Children.Add(new Separator { Margin = new Thickness(16, 0, 16, 0) });
                }
                rowsPanel.Children.Add(rows[i].Id == PermissionItems.TemperaturesId ? BuildTemperatureRow(rows[i]) : BuildRow(rows[i]));
            }
            Groups.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = rowsPanel });
        }
    }

    /// The Temperatures row's state, as the row's tile and label show it: on is granted, starting is checking, the rest are not granted.
    private PermState TemperatureState() => _helper.State switch
    {
        HelperState.On => PermState.Granted,
        HelperState.Starting => PermState.Checking,
        _ => PermState.NotAsked,
    };

    /// The Temperatures row: the standard row, then its own line. The line says why (declined, paused, unavailable) and holds the
    /// buttons: Turn on (with the sign-in check box), Try again, or Turn off. Nothing starts without one of these clicks.
    private UIElement BuildTemperatureRow(PermItem item)
    {
        var stack = new StackPanel();
        stack.Children.Add(BuildRow(item, _helper.State switch
        {
            HelperState.On => L10n.L("On"),
            _ => null,
        }));

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(40, 0, 16, 12) };
        string? message = _helper.State switch
        {
            HelperState.Declined => L10n.L("Not turned on: administrator was not allowed"),
            HelperState.Paused => L10n.L("Turn on again to read temperatures this session"),
            HelperState.Unavailable => L10n.L("This PC does not report temperatures"),
            _ => null,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (message is not null)
        {
            text.Children.Add(new TextBlock { Style = (Style)FindResource("CardNote"), Text = message });
        }

        var signIn = new CheckBox
        {
            Content = L10n.L("Start with Windows without asking again"),
            IsChecked = Store.SensorHelperAtSignIn,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        switch (_helper.State)
        {
            case HelperState.Off:
                line.Children.Add(signIn);
                line.Children.Add(ActionButton(L10n.L("Turn on"), () => TurnOnFromClick(signIn.IsChecked == true)));
                break;
            case HelperState.Declined:
            case HelperState.Paused:
                line.Children.Add(signIn);
                line.Children.Add(ActionButton(L10n.L("Try again"), () => TurnOnFromClick(signIn.IsChecked == true)));
                break;
            case HelperState.On:
                line.Children.Add(ActionButton(L10n.L("Turn off"), TurnOffFromClick));
                break;
        }

        // The sign-in helper is installed (its marker is there) in any state: Turn off is offered, so an orphaned task can be removed.
        if (_helper.HasSignInTask && _helper.State != HelperState.On)
        {
            line.Children.Add(ActionButton(L10n.L("Turn off"), TurnOffFromClick));
        }

        if (text.Children.Count > 0)
        {
            stack.Children.Add(text);
        }
        if (line.Children.Count > 0)
        {
            stack.Children.Add(line);
        }
        return stack;
    }

    /// The user clicked Turn on or Try again. The helper asks UAC once; a cancelled prompt is Declined and nothing else happens.
    private async void TurnOnFromClick(bool atSignIn)
    {
        try
        {
            await _helper.TurnOnAsync(atSignIn);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            ErrorLog.Write(e);
        }
        Refresh(firewall: false);
    }

    /// The user clicked Turn off: one UAC prompt when the logon task exists, else the helper is only asked to stop.
    private async void TurnOffFromClick()
    {
        try
        {
            await _helper.TurnOffAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            ErrorLog.Write(e);
        }
        Refresh(firewall: false);
    }

    /// One row: the glyph in its state's colour, the title and detail, the state, and the button or switch that acts on it.
    /// stateText replaces the state's label (the Temperatures row shows On).
    private UIElement BuildRow(PermItem item, string? stateText = null)
    {
        var tile = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = item.Glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        tile.SetResourceReference(Border.BackgroundProperty, StateBrush(item.State, filled: true));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
            Text = item.Detail,
        });

        var state = new TextBlock
        {
            Text = stateText ?? PermissionItems.Label(item.State),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        state.SetResourceReference(TextBlock.ForegroundProperty, StateBrush(item.State, filled: false));

        var grid = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(tile, grid, 0);
        Place(text, grid, 1);
        Place(state, grid, 2);

        var action = ActionFor(item);
        if (action is not null)
        {
            Place(action, grid, 3);
        }
        return grid;
    }

    /// The control that acts on a row: the start-at-login switch, or the button that opens the page or asks for the grant.
    private UIElement? ActionFor(PermItem item)
    {
        if (item.Id == PermissionItems.LoginId)
        {
            var box = new CheckBox
            {
                IsChecked = item.State == PermState.Granted,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = item.Title,
            };
            AutomationProperties.SetName(box, item.Title);
            box.Click += (_, _) => ChangeLogin(box.IsChecked == true);
            return box;
        }

        if (item.State is PermState.Checking)
        {
            return null;
        }

        var settings = SettingsUri(item.Id);
        if (settings is not null && item.State is PermState.Denied or PermState.NotAsked)
        {
            return ActionButton(L10n.L("Open Settings"), () => SystemActions.OpenSettings(settings));
        }
        if (item.Id == "firewall" && item.State == PermState.NotAsked)
        {
            return ActionButton(L10n.L("Allow"), button => AllowFirewall(button));
        }
        if (AgentKinds.Parse(item.Id.StartsWith("hook.", StringComparison.Ordinal) ? item.Id[5..] : null) is { } kind)
        {
            return item.State == PermState.Granted
                ? ActionButton(L10n.L("Revoke"), () => ChangeHook(kind, on: false))
                : ActionButton(L10n.L("Allow"), () => ChangeHook(kind, on: true));
        }
        return null;
    }

    /// The ms-settings page of a row, or null when the row has none.
    internal static string? SettingsUri(string id) => id switch
    {
        "notifications" => "ms-settings:notifications",
        "mic" => "ms-settings:privacy-microphone",
        "camera" => "ms-settings:privacy-webcam",
        _ => null,
    };

    private static Button ActionButton(string text, Action<Button> click)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => click(button);
        return button;
    }

    private static Button ActionButton(string text, Action click) =>
        ActionButton(text, _ => click());

    /// Saves start at login, as the switch says. A failure is shown and the switch takes the real state back.
    private void ChangeLogin(bool on)
    {
        try
        {
            StartAtLogin.Set(_registry, AppFiles.AppExe, on);
            ShowError(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowError(L10n.L("Couldn't change {0}: {1}", L10n.L("Open at login"), e.Message));
        }
        Refresh(firewall: false);
    }

    /// Adds or removes the agent's hook. A settings file that cannot be edited is left as it is, and the error is shown.
    private void ChangeHook(AgentKind kind, bool on)
    {
        var name = AgentKinds.Spec(kind).Name;
        try
        {
            if (on)
            {
                HookInstaller.Install(kind, AppFiles.HookExe, AppFiles.Home);
            }
            else
            {
                HookInstaller.Uninstall(kind, AppFiles.Home);
            }
            ShowError(null);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowError(L10n.L("Couldn't edit {0}'s settings: {1}", name, e.Message));
        }
        Refresh(firewall: false);
    }

    /// Adds the firewall rule through the UAC prompt, then reads the rule again. The button is off while netsh runs, so one
    /// click asks once. A cancelled UAC prompt is silent; any other failure is logged and shows nothing.
    private async void AllowFirewall(Button button)
    {
        button.IsEnabled = false;
        try
        {
            await Task.Run(() => SystemActions.AddFirewallRule(AppFiles.AppExe));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user said no to the UAC prompt.
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        if (IsLoaded)
        {
            Refresh(firewall: true);
        }
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Place(UIElement element, Grid grid, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// The group headers, spelled as the Mac shows them (Preferences.swift PermissionList).
    private static string GroupTitle(PermGroup group) => group switch
    {
        PermGroup.Needed => L10n.L("Needed now"),
        PermGroup.Granted => L10n.L("Granted"),
        _ => L10n.L("Not in use"),
    };

    /// The theme brush of a state: green when allowed, red when denied, amber when not granted. While checking, the tile is
    /// grey and the state text is secondary. Filled is the tile's background; otherwise the state text's foreground.
    private static string StateBrush(PermState state, bool filled) => state switch
    {
        PermState.Granted => "SystemFillColorSuccessBrush",
        PermState.Denied => "SystemFillColorCriticalBrush",
        PermState.NotAsked => "SystemFillColorCautionBrush",
        _ => filled ? "ControlStrongFillColorDefaultBrush" : "TextFillColorSecondaryBrush",
    };
}
