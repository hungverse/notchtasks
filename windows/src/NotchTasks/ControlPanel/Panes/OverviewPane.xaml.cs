using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Onboarding;
using NotchTasks.Core.Permissions;
using NotchTasks.Core.Settings;
using NotchTasks.Onboarding;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Overview pane (Preferences.swift overview): the hero, three stats (the agents running, the CI errors, the permissions
/// granted), the next steps still to do (after the first run only), the modules in groups with a switch each, and the way back
/// to the guide. The states are read each time the pane is drawn and when the Control Panel is activated; the running and error
/// counts follow the notch as it changes.
public partial class OverviewPane : SettingsPane
{
    /// The glyph of a permission row. The permission rows carry no glyph of their own, so the lock stands for all of them.
    private const string PermissionGlyph = "";  // lock_closed: the permission rows (Mac lock.fill)

    /// The glyph of an agent row (the sparkle the Mac uses for agents; the same as the agent module).
    private const string AgentGlyph = "";  // sparkle: the agent rows (Mac sparkles)

    /// The glyph of the phone step (the iPhone the Mac shows).
    private const string PhoneGlyph = "";

    /// The module groups of the Mac's overview (Settings.swift groups), in the Mac's order. The last group also takes every
    /// Windows module no other group names.
    private static readonly (string Title, string[] Ids)[] Groups =
    [
        ("Work", ["agents", "ci", "limits", "calendar", "timer"]),
        ("Music & devices", ["media", "devices", "battery", "levels"]),
        ("Calls & privacy", ["calls", "privacy", "mirror"]),
        ("System", ["network", "downloads", "health", "shelf"]),
    ];

    private readonly AppState _state;
    private readonly RegistryAccess _registry = new();
    private readonly bool _hasBattery;
    private readonly TextBlock _runningValue;
    private readonly TextBlock _alertsValue;
    private readonly TextBlock _permissionsValue;

    /// The error of the last hook switch that could not be saved, until the next one that works.
    private string? _actionError;

    public OverviewPane(SettingsStore store, AppState state) : base(store)
    {
        _state = state;
        _hasBattery = PowerFacts.HasSystemBattery();
        InitializeComponent();
        Stats.Children.Add(StatCard("", null, L10n.L("Running"), out _runningValue));
        Stats.Children.Add(StatCard("", ControlPanelLook.Red, L10n.L("CI errors"), out _alertsValue));
        Stats.Children.Add(StatCard("", ControlPanelLook.Green, L10n.L("Permissions"), out _permissionsValue));
        GuideButton.Click += (_, _) => OnboardingWindow.Show(_state);
        ReportButton.Click += (_, _) => ShowReport();

        // The running and error counts follow the notch while the pane is shown; the handler is removed when it is hidden.
        Loaded += (_, _) => _state.NotchChanged += OnNotchChanged;
        Unloaded += (_, _) => _state.NotchChanged -= OnNotchChanged;
    }

    protected override void Apply() => Render();

    /// The user may have come back from Settings or the Watching pane: the states are read again.
    protected override void OnWindowActivated() => ApplyStored();

    /// Draws the next steps and the module groups from the current states, and the counts.
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

        UpdateCounts();
        _permissionsValue.Text = string.Format(CultureInfo.InvariantCulture, "{0}/{1}",
            items.Count(item => item.State == PermState.Granted), items.Count);
        // The phone step is asked for only where sync is on, and while no phone is paired yet.
        var paired = _state.Bridge is null || _state.Bridge.State.Phones.Count > 0;
        RenderNextSteps(NextSteps.Compute(items, modules, Store.SystemNotifications, present, installed, paired), items);
        ErrorText.Text = _actionError ?? string.Empty;
        ErrorText.Visibility = _actionError is null ? Visibility.Collapsed : Visibility.Visible;
        RenderModules();
    }

    /// The notch changed: the running and error counts follow it. Its thread is the one that changed the notch.
    private void OnNotchChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateCounts();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(UpdateCounts));
        }
    }

    private void UpdateCounts()
    {
        var running = _state.Snapshot.Running.Count;
        var alerts = _state.Snapshot.Alerts.Count;
        _runningValue.Text = running.ToString(CultureInfo.InvariantCulture);
        _alertsValue.Text = alerts.ToString(CultureInfo.InvariantCulture);
        RenderPreview(running, alerts);
    }

    /// The accent the user chose, or null when the accent is still "auto" (the Mac's default, orange, and green bars).
    private Color? ChosenAccent()
    {
        if (Store.Accent == "auto")
        {
            return null;
        }
        return Appearance.ResolveAccent(Store.Accent, Store.CustomAccent, null) is { } hex ? HexColor.ToColor(hex) : null;
    }

    /// The notch preview (Preferences.swift NotchPreview): on the left, when something runs, the sparkle in a tile, the count
    /// and the pill's working bars; on the right, when there are alerts, the wrench and a 60% progress ring. The tint is the
    /// chosen accent, else orange; the bars are the chosen accent, else green.
    private void RenderPreview(int running, int alerts)
    {
        var tint = new SolidColorBrush(ChosenAccent() ?? Color.FromRgb(0xFF, 0x9F, 0x0A));

        RunningPart.Children.Clear();
        if (running > 0)
        {
            var sparkle = new FluentGlyph
            {
                Code = "",
                Size = 10,
                Foreground = tint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RunningPart.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = sparkle,
            });
            RunningPart.Children.Add(new TextBlock
            {
                Text = running.ToString(CultureInfo.InvariantCulture),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = tint,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var bars = Loops.Bars(new SolidColorBrush(ChosenAccent() ?? Color.FromRgb(0x30, 0xD1, 0x58)));
            bars.Margin = new Thickness(6, 0, 0, 0);
            RunningPart.Children.Add(bars);
        }

        AlertPart.Children.Clear();
        if (alerts > 0)
        {
            AlertPart.Children.Add(new FluentGlyph
            {
                Code = "",
                Size = 12,
                Foreground = tint,
                VerticalAlignment = VerticalAlignment.Center,
            });
            AlertPart.Children.Add(ProgressRing(tint, 0.6, new Thickness(6, 0, 0, 0)));
        }
    }

    /// A 12 DIP ring filled to `fraction` of a turn, clockwise from the top (a 2 DIP stroke on a faint track).
    private static Grid ProgressRing(Brush tint, double fraction, Thickness margin)
    {
        const double radius = 5;
        var sweep = fraction * 360 * Math.PI / 180;
        var start = new Point(6, 6 - radius);
        // The end of the arc: the angle counts clockwise from the top, so the sine and cosine are shifted by a quarter turn.
        var end = new Point(6 + radius * Math.Sin(sweep), 6 - radius * Math.Cos(sweep));
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true));

        var grid = new Grid { Width = 12, Height = 12, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2,
        });
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new PathGeometry { Figures = { figure } },
            Stroke = tint,
            StrokeThickness = 2,
        });
        return grid;
    }

    /// One stat: its glyph in its colour (the accent when color is null), its value over its label, in a card.
    private Border StatCard(string glyph, Color? color, string label, out TextBlock value)
    {
        var icon = new FluentGlyph
        {
            Code = glyph,
            Size = 16,
        };
        if (color is { } tint)
        {
            icon.Foreground = new SolidColorBrush(tint);
        }
        else
        {
            // no colour given: the primary text colour, which is legible on the card in both themes (the accent can be too dark)
            icon.SetResourceReference(FluentGlyph.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
        value = new TextBlock { FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(icon);
        text.Children.Add(value);
        text.Children.Add(new TextBlock { Style = (Style)FindResource("CardNote"), Margin = new Thickness(0, 2, 0, 0), Text = label });
        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 12, 8),
            Child = text,
        };
    }

    private void RenderNextSteps(IReadOnlyList<NextStep> steps, IReadOnlyList<PermItem> items)
    {
        NextStepRows.Children.Clear();
        NextStepsSection.Visibility = Store.OnboardingDone && steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var step in steps)
        {
            NextStepRows.Children.Add(StepRow(step, items));
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
                    () => TurnOnHook(agent.Kind));
            case NextStep.Phone:
                return Row(PhoneGlyph, L10n.L("Pairing"), L10n.L("Pairing"),
                    () => ControlPanelWindow.Show(_state, PaneId.Pairing));
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    /// Installs the agent's hook, as the Watching pane's switch does, then draws the pane again: the step is gone once the hook is
    /// on. A settings file that cannot be edited keeps its file as it is: the error is shown here, and Watching opens so the
    /// user can look at the agent's row there.
    private void TurnOnHook(AgentKind kind)
    {
        var name = AgentKinds.Spec(kind).Name;
        try
        {
            HookInstaller.Install(kind, AppFiles.HookExe, AppFiles.Home);
            App.Analytics?.Event("agent_hook_on", new Dictionary<string, object> { ["agent"] = kind.Id() });
            _actionError = null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _actionError = L10n.L("Couldn't edit {0}'s settings: {1}", name, e.Message);
            ControlPanelWindow.Show(_state, PaneId.Watching);
        }
        ApplyStored();
    }

    /// Opens Report a problem. It opens with or without sync: with no bridge the dialog explains it cannot send.
    private void ShowReport()
    {
        var dialog = new ReportDialog(_state.Bridge) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
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

    /// One step: a card with its glyph, its text and the one button that does it.
    private static Border Row(string glyph, string text, string buttonText, Action click)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FluentGlyph
        {
            Code = glyph,
            Size = 14,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var button = new Button
        {
            Content = buttonText,
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => click();
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);

        return new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    /// The module groups: a caps heading, then a two-column grid of cells. Each cell is the module's icon, its name and a switch
    /// that turns the module on or off at once (the Modules pane writes the same setting).
    private void RenderModules()
    {
        ModuleGroups.Children.Clear();
        var visible = ModuleCatalog.All.Where(id => ModuleCatalog.OnWindows(id) && (id != "battery" || _hasBattery)).ToList();
        var grouped = Groups.SelectMany(group => group.Ids).ToHashSet(StringComparer.Ordinal);
        for (var g = 0; g < Groups.Length; g++)
        {
            var (title, ids) = Groups[g];
            var last = g == Groups.Length - 1;
            var members = visible.Where(id => ids.Contains(id, StringComparer.Ordinal) || (last && !grouped.Contains(id))).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            ModuleGroups.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("GroupCaps"),
                Text = L10n.L(title).ToUpper(CultureInfo.CurrentUICulture),
            });
            var cells = new UniformGrid { Columns = 2 };
            foreach (var id in members)
            {
                cells.Children.Add(ModuleCell(id));
            }
            ModuleGroups.Children.Add(cells);
        }
    }

    /// One module's cell: its tile, its name and its switch. The tile is accent when the module is on and grey when it is off.
    private Border ModuleCell(string id)
    {
        var on = !Store.Disabled.Contains(id);
        var title = L10n.L(ModuleCatalog.Title(id));

        var tile = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FluentGlyph
            {
                Code = ModuleCatalog.Glyph(id),
                Filled = ModuleCatalog.GlyphFilled(id),
                Size = 16,
                Foreground = Brushes.White,  // a coloured chip carries a white glyph (the on-accent brush is black in dark mode)
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        tile.SetResourceReference(Border.BackgroundProperty, on ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");

        var name = new TextBlock
        {
            Text = title,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

        var toggle = new CheckBox
        {
            IsChecked = on,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = L10n.L(ModuleCatalog.Blurb(id)),
        };
        AutomationProperties.SetName(toggle, title);
        toggle.Click += (_, _) => Write("disabled", ModulesPane.DisabledWith(Store.Disabled, id, toggle.IsChecked == true));

        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(toggle, Dock.Right);
        DockPanel.SetDock(tile, Dock.Left);
        line.Children.Add(toggle);
        line.Children.Add(tile);
        line.Children.Add(name);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            CornerRadius = new CornerRadius(10),
            Height = 44,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = L10n.L(ModuleCatalog.Blurb(id)),
            Child = line,
        };
    }
}
