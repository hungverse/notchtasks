using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Onboarding;
using NotchTasks.Core.Permissions;
using NotchTasks.Core.Settings;
using NotchTasks.Onboarding;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Overview pane (Preferences.swift overview): two stats (the modules on, the agents watched), the next steps still to do
/// (after the first run only), one cell per module (a click opens Modules) and the way back to the guide. The states are read
/// each time the pane is drawn and when the Control Panel is activated.
public partial class OverviewPane : SettingsPane
{
    /// The glyph of a permission row. The permission rows carry no glyph of their own, so the lock stands for all of them.
    private const string PermissionGlyph = "";

    /// The glyph of an agent row (the sparkle the Mac uses for agents; the same as the agent module).
    private const string AgentGlyph = "";

    private readonly AppState _state;
    private readonly RegistryAccess _registry = new();

    public OverviewPane(SettingsStore store, AppState state) : base(store)
    {
        _state = state;
        InitializeComponent();
        GuideButton.Click += (_, _) => OnboardingWindow.Show(_state);
    }

    protected override void Apply() => Render();

    /// The user may have come back from Settings or the Watching pane: the states are read again.
    protected override void OnWindowActivated() => ApplyStored();

    /// Draws the stats, the next steps and the module cells from the current states.
    private void Render()
    {
        Note.Text = L10n.L("The pill shows what's running, and stays quiet when nothing is.");
        var home = AppFiles.Home;
        var facts = PermissionProbe.Facts(_registry, home, AppFiles.HookExe, AppFiles.AppExe);
        var items = PermissionItems.Build(facts).Where(item => item.Applies).ToList();
        var agents = Enum.GetValues<AgentKind>();
        var present = agents.Where(kind => facts.Present.TryGetValue(kind, out var here) && here).ToList();
        var installed = agents.Where(kind => HookInstaller.IsInstalled(kind, home)).ToHashSet();
        var modules = ModuleCatalog.All.Where(id => ModuleCatalog.OnWindows(id) && !Store.Disabled.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        RenderStats(modules.Count, installed.Count);
        RenderNextSteps(NextSteps.Compute(items, modules, Store.SystemNotifications, present, installed), items);
        RenderModules();
    }

    private void RenderStats(int modulesOn, int agentsWatched)
    {
        Stats.Children.Clear();
        Stats.Children.Add(Stat(modulesOn.ToString(CultureInfo.InvariantCulture), L10n.L("Modules on")));
        Stats.Children.Add(Stat(agentsWatched.ToString(CultureInfo.InvariantCulture), L10n.L("Agents watched")));
    }

    /// One stat: its value over its label, in a card.
    private Border Stat(string value, string label)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Style = (Style)FindResource("CardNote"), Margin = new Thickness(0, 2, 0, 0), Text = label });
        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 8, 8),
            Child = text,
        };
    }

    private void RenderNextSteps(IReadOnlyList<NextStep> steps, IReadOnlyList<PermItem> items)
    {
        NextStepRows.Children.Clear();
        NextStepsSection.Visibility = Store.OnboardingDone && steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0)
            {
                NextStepRows.Children.Add(new Separator { Margin = new Thickness(16, 0, 16, 0) });
            }
            NextStepRows.Children.Add(StepRow(steps[i], items));
        }
    }

    /// A permission step opens its Settings page, or the Permissions pane when it has none. An agent step opens the Watching
    /// pane, where its hook is turned on.
    private UIElement StepRow(NextStep step, IReadOnlyList<PermItem> items)
    {
        switch (step)
        {
            case NextStep.Permission permission:
                var item = items.First(row => row.Id == permission.Id);
                return Row(PermissionGlyph, item.Title,
                    item.State == PermState.Denied ? L10n.L("Open Settings") : L10n.L("Allow"),
                    () => OpenPermission(item));
            case NextStep.Agent agent:
                var name = AgentKinds.Spec(agent.Kind).Name;
                return Row(AgentGlyph, name + " · " + L10n.L("Answer agents on the notch"), L10n.L("On"),
                    () => ControlPanelWindow.Show(_state, PaneId.Watching));
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    private void OpenPermission(PermItem item)
    {
        var uri = PermissionsPane.SettingsUri(item.Id);
        if (uri is not null)
        {
            SystemActions.OpenSettings(uri);
        }
        else
        {
            ControlPanelWindow.Show(_state, PaneId.Permissions);
        }
    }

    /// One step: its glyph, its text and the one button that does it.
    private static Grid Row(string glyph, string text, string buttonText, Action click)
    {
        var grid = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var button = new Button { Content = buttonText, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return grid;
    }

    /// One cell per module on Windows, in the user's order: its icon, its name and On or Off. A click opens Modules.
    private void RenderModules()
    {
        ModuleCells.Children.Clear();
        foreach (var id in ModuleCatalog.Order(Store.Modules))
        {
            var on = !Store.Disabled.Contains(id);
            var title = L10n.L(ModuleCatalog.Title(id));

            var tile = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ModuleCatalog.Glyph(id),
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            tile.SetResourceReference(Border.BackgroundProperty,
                on ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");

            var name = new TextBlock
            {
                Text = title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty,
                on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

            var state = new TextBlock
            {
                Text = on ? L10n.L("On") : L10n.L("Off"),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            state.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            var line = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(tile, Dock.Left);
            DockPanel.SetDock(state, Dock.Right);
            line.Children.Add(tile);
            line.Children.Add(state);
            line.Children.Add(name);

            var cell = new Button
            {
                Style = (Style)FindResource("TileButton"),
                ToolTip = L10n.L(ModuleCatalog.Blurb(id)),
                Content = new Border
                {
                    Style = (Style)FindResource("Card"),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 8, 8),
                    Child = line,
                },
            };
            AutomationProperties.SetName(cell, title);
            cell.Click += (_, _) => ControlPanelWindow.Show(_state, PaneId.Modules);
            ModuleCells.Children.Add(cell);
        }
    }
}
