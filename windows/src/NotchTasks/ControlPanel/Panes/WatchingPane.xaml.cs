using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Watching pane (SettingsView.swift WatchListView, the agents part): one row per coding agent, with whether its CLI is
/// here, a switch for its hook, and the one step left before the notch can answer it (opened in a terminal). The rows are read
/// when the pane shows and when the Control Panel is activated, so a step done in the terminal shows at once.
public partial class WatchingPane : SettingsPane
{
    /// The error of the last switch that could not be saved, until the next one. Shown with the configuration error.
    private string? _actionError;

    /// The error of an agent whose settings file cannot be read as settings, found when the rows are built.
    private string? _configError;

    public WatchingPane(SettingsStore store) : base(store)
    {
        InitializeComponent();
    }

    protected override void Apply() => Render();

    protected override void OnWindowActivated() => ApplyStored();

    /// Builds one row per agent, in the AgentKind order, with a separator between rows. An agent whose settings file cannot
    /// be edited shows its error at once, not only after a switch fails.
    private void Render()
    {
        var home = AppFiles.Home;
        var geminiKeySet = Environment.GetEnvironmentVariable("GEMINI_API_KEY") is not null;

        Rows.Children.Clear();
        _configError = null;
        var kinds = Enum.GetValues<AgentKind>();
        for (var i = 0; i < kinds.Length; i++)
        {
            if (i > 0)
            {
                Rows.Children.Add(new Separator { Margin = new Thickness(16, 0, 16, 0) });
            }
            Rows.Children.Add(BuildRow(kinds[i], home, geminiKeySet));
            if (!HookInstaller.CanEdit(kinds[i], home))
            {
                _configError ??= L10n.L("Couldn't edit {0}'s settings: the file is not valid JSON.", AgentKinds.Spec(kinds[i]).Name);
            }
        }
        ShowErrorLine();
    }

    private UIElement BuildRow(AgentKind kind, string home, bool geminiKeySet)
    {
        var spec = AgentKinds.Spec(kind);
        var installed = HookInstaller.IsInstalled(kind, home);
        var present = AgentSetup.Present(kind, home);
        var step = AgentSetup.SetupStep(kind, home, geminiKeySet);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = spec.Name, FontWeight = FontWeights.Medium });
        text.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
            Text = present ? L10n.L("Found on this computer") : L10n.L("Not found on this computer"),
        });

        var grid = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(text, grid, 0);

        // What is left, with the one button that does it. Else, that it works. Gemini's step shows without its hook.
        if (step is not null && (installed || kind == AgentKind.Gemini))
        {
            var missing = new TextBlock
            {
                Text = step.Missing,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            missing.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            Place(missing, grid, 1);

            var button = new Button
            {
                Content = step.Button,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => SystemActions.OpenInTerminal(step.Command);
            Place(button, grid, 2);
        }
        else if (installed)
        {
            var ready = new TextBlock
            {
                Text = L10n.L("Ready"),
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ready.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            Place(ready, grid, 3);
        }

        var hook = new CheckBox
        {
            IsChecked = installed,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L10n.L("On"),
        };
        AutomationProperties.SetName(hook, spec.Name);
        hook.Click += (_, _) => ChangeHook(kind, hook.IsChecked == true, geminiKeySet);
        Place(hook, grid, 4);

        return grid;
    }

    /// Installs or removes the agent's hook. A settings file that cannot be edited is left as it is: the error shows and the
    /// rows are read again, so the switch takes the real state back. Turning a hook on opens the step that is left, as the Mac does.
    private void ChangeHook(AgentKind kind, bool on, bool geminiKeySet)
    {
        var home = AppFiles.Home;
        var name = AgentKinds.Spec(kind).Name;
        try
        {
            if (on)
            {
                HookInstaller.Install(kind, AppFiles.HookExe, home);
            }
            else
            {
                HookInstaller.Uninstall(kind, home);
            }
            _actionError = null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _actionError = L10n.L("Couldn't edit {0}'s settings: {1}", name, e.Message);
        }

        ApplyStored();
        if (on && HookInstaller.IsInstalled(kind, home) && AgentSetup.SetupStep(kind, home, geminiKeySet) is { } step)
        {
            SystemActions.OpenInTerminal(step.Command);
        }
    }

    /// Shows the last switch's error, else the configuration error of an agent, else nothing.
    private void ShowErrorLine()
    {
        var message = _actionError ?? _configError;
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Place(UIElement element, Grid grid, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
