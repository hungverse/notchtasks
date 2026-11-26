using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Watching pane (SettingsView.swift WatchListView): the watch list on top (what the notch watches, with Add, Edit, On and
/// Remove), then one row per coding agent, with whether its CLI is here, a switch for its hook, and the one step left before the
/// notch can answer it (opened in a terminal). The rows are read when the pane shows and when the Control Panel is activated,
/// so a step done in the terminal shows at once.
public partial class WatchingPane : SettingsPane
{
    private const string DeleteGlyph = "";
    private const int RunningAppLimit = 80;

    private readonly IProcessReader _processes;
    private readonly BuildToolsBox _buildTools;
    private readonly string _home;
    private readonly string? _path;
    private readonly string? _appData;

    /// The error of the last switch that could not be saved, until the next one. Shown with the configuration error.
    private string? _actionError;

    /// The error of an agent whose settings file cannot be read as settings, found when the rows are built.
    private string? _configError;

    /// The pane for the settings store, the process table (for the running apps), the user's profile folder (where the agents'
    /// settings and CLIs are), and for tests the PATH and %APPDATA% folder to search for CLIs (null: this PC's).
    public WatchingPane(SettingsStore store, IProcessReader processes, string home, string? path = null, string? appData = null)
        : base(store)
    {
        _processes = processes;
        _home = home;
        _path = path;
        _appData = appData;
        InitializeComponent();
        _buildTools = new BuildToolsBox(store);
        BuildHost.Content = _buildTools;
        AddButton.Click += (_, _) =>
        {
            RebuildAddMenu();
            AddButton.ContextMenu.PlacementTarget = AddButton;
            AddButton.ContextMenu.IsOpen = true;
        };
    }

    protected override void Apply()
    {
        WatchCaption.Text = L10n.L("What the notch watches. Only what's working shows on the notch.");
        AddHint.Text = L10n.L("Choose an agent, an open app, or type a process name.");
        RenderWatchList();
        _buildTools.Render();
        Render();
    }

    protected override void OnWindowActivated() => ApplyStored();

    /// The watch list: one row per item, in the list's order, with a separator between rows.
    private void RenderWatchList()
    {
        WatchRows.Children.Clear();
        var items = Store.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                WatchRows.Children.Add(new Separator { Margin = new Thickness(16, 0, 16, 0) });
            }
            WatchRows.Children.Add(BuildWatchRow(items[i]));
        }
    }

    /// Icon, name and summary line (the Mac's "AI agent · " or "Build & CI · " then the summary), Edit, the On switch and Remove.
    private UIElement BuildWatchRow(WatchItem item)
    {
        var grid = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The item's own badge when it has one (the Mac's row icon), else the glyph of its symbol. The icon is a button that opens
        // the icon editor for this item.
        FrameworkElement face = ItemIcon.BadgeOf(item) is { } own
            ? BadgeGlyph.Create(own, 20, BadgeGlyph.AccentOf(Store))
            : SymbolGlyph(item);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        face.VerticalAlignment = VerticalAlignment.Center;
        var icon = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Content = face,
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L10n.L("Change icon"),
        };
        AutomationProperties.SetName(icon, L10n.L("Change icon"));
        var id = item.Id;
        icon.Click += (_, _) => ChangeIcon(id);
        Place(icon, grid, 0);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
            Text = (WatchItems.InBuild(item) ? "Build & CI · " : "AI agent · ") + WatchItems.Summary(item),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Place(text, grid, 1);

        var edit = new Button
        {
            Content = L10n.L("Edit"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        edit.Click += (_, _) => EditItem(item);
        Place(edit, grid, 2);

        var on = new CheckBox
        {
            Content = L10n.L("On"),
            IsChecked = item.Enabled,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(on, item.Name);
        on.Click += (_, _) => SetEnabled(item.Id, on.IsChecked == true);
        Place(on, grid, 3);

        var remove = new Button
        {
            Content = new FluentGlyph { Code = DeleteGlyph, Size = 14 }.ThemedBy("TextFillColorPrimaryBrush"),
            ToolTip = L10n.L("Remove"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => RemoveItem(item.Id);
        Place(remove, grid, 4);

        return grid;
    }

    /// The Add menu, built each time it opens: the preset groups (installed presets first, marked "✓ "; added ones disabled),
    /// the Build & CI presets, the running apps, and a process to type in.
    private void RebuildAddMenu()
    {
        var running = RunningAppNames();
        var menu = new ContextMenu();
        foreach (var group in WatchItems.PresetGroups.Append(new WatchPresetGroup("Build & CI", WatchItems.BuildPresets)))
        {
            menu.Items.Add(new MenuItem { Header = MenuLabel(group.Title), IsEnabled = false });
            foreach (var preset in group.Items.OrderByDescending(IsInstalled))
            {
                menu.Items.Add(PresetEntry(preset, IsInstalled(preset)));
            }
            menu.Items.Add(new Separator());
        }

        if (running.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = MenuLabel(L10n.L("Open apps")), IsEnabled = false });
            foreach (var name in running.Take(RunningAppLimit))
            {
                var app = new WatchItem(Guid.NewGuid().ToString(), Display(name), WatchKind.App, BundleID: name + ".exe");
                var entry = new MenuItem { Header = MenuLabel(app.Name), Tag = "running", IsEnabled = !Added(app) };
                entry.Click += (_, _) => AddItem(app);
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Separator());
        }

        var typed = new MenuItem { Header = MenuLabel(L10n.L("Enter a process…")) };
        typed.Click += (_, _) => AddNewProcess();
        menu.Items.Add(typed);
        AddButton.ContextMenu = menu;
    }

    /// A menu header as text. A TextBlock, not a string: a string header reads an "_" in a name as an access key.
    private static TextBlock MenuLabel(string text) => new() { Text = text };

    private MenuItem PresetEntry(WatchItem preset, bool installed)
    {
        var added = Added(preset);
        var entry = new MenuItem
        {
            Header = MenuLabel((installed ? "✓ " : string.Empty) + preset.Name + (added ? L10n.L(" (added)") : string.Empty)),
            IsEnabled = !added,
        };
        entry.Click += (_, _) => AddPreset(preset);
        return entry;
    }

    /// Whether a preset is here, as Electron decides it: Claude always is; a CLI is found on this PC; an app is installed when its
    /// exe is a name the Windows map knows, or a file that exists. The Mac checks the app on disk; Windows has no path for an app, so
    /// the known exe is the floor. Running is not required.
    private bool IsInstalled(WatchItem preset)
    {
        if (preset.Kind == WatchKind.Claude)
        {
            return true;
        }
        if (preset.Kind == WatchKind.App)
        {
            return AppInstalled(preset);
        }
        var cli = preset.Cli ?? (preset.Kind == WatchKind.Codex ? AgentKinds.Spec(AgentKind.Codex).Cli : null);
        return cli is not null && AgentSetup.FindCli(cli, _home, _path, _appData) is not null;
    }

    private static bool AppInstalled(WatchItem app)
    {
        var exe = WatchItems.AppExe(app);
        if (exe is null)
        {
            return false;
        }
        return WatchItems.WinAppExe.Values.Contains(exe, StringComparer.OrdinalIgnoreCase)
            || (Path.IsPathRooted(exe) && File.Exists(exe));
    }

    /// Whether the list already holds this item: the same kind, process, command line and bundle (the Mac's `added`).
    private bool Added(WatchItem item) => Store.Items.Any(i =>
        i.Kind == item.Kind && i.Process == item.Process && i.Args == item.Args && i.BundleID == item.BundleID);

    /// The programs the desktop started: the children of explorer.exe, by normalised name, sorted, without this app.
    /// Ported from main.js runningApps (the first 80 are offered in the menu).
    private List<string> RunningAppNames()
    {
        var snapshot = _processes.Snapshot();
        var shells = snapshot.ByNormName("explorer").Select(p => p.Pid).ToHashSet();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in snapshot.All)
        {
            var name = ProcTable.NormName(p.Name);
            if (shells.Contains(p.Ppid) && name.Length > 0 && name is not ("notchtasks" or "electron"))
            {
                names.Add(name);
            }
        }
        return names.ToList();
    }

    /// The name as the menu shows it: the normalised name with a capital first letter.
    private static string Display(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private void AddPreset(WatchItem preset)
    {
        if (!Added(preset))
        {
            AddItem(preset);
        }
    }

    private void AddNewProcess()
    {
        var editor = new WatchItemEditor(new WatchItem(Guid.NewGuid().ToString(), string.Empty, WatchKind.Process), isNew: true)
        {
            Owner = Window.GetWindow(this),
        };
        editor.ShowDialog();
        if (editor.Result is { } saved)
        {
            AddItem(saved);
        }
    }

    private void EditItem(WatchItem item)
    {
        var editor = new WatchItemEditor(item, isNew: false) { Owner = Window.GetWindow(this) };
        editor.ShowDialog();
        if (editor.Result is { } saved)
        {
            SaveItems(Store.Items.Select(i => i.Id == saved.Id ? saved : i).ToList());
        }
    }

    /// The item's symbol as its glyph when the glyph map has it, else the sparkles an agent without an app icon shows on the pill.
    private static FrameworkElement SymbolGlyph(WatchItem item)
    {
        var symbol = WatchItems.Symbol(item);
        var unknown = ModuleGlyphs.Get(symbol).Glyph == ModuleGlyphs.Fallback;
        // a 20 DIP glyph in the text colour, centred in its box (FluentGlyph); the row centres the element in its column
        var glyph = FluentGlyph.Named(unknown ? "sparkles" : symbol, 20);
        glyph.SetResourceReference(FluentGlyph.ForegroundProperty, "TextFillColorPrimaryBrush");
        return glyph;
    }

    /// Opens the icon editor of a watch item (the Mac's BadgeEditor for item.icon). It starts from the item's own badge, else from
    /// its symbol on a rounded shape. Each edit saves the item's icon at once; Reset removes it, so the symbol shows again.
    private void ChangeIcon(string id)
    {
        var item = Store.Items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            return;
        }
        var start = ItemIcon.BadgeOf(item) is { } own
            ? (JsonObject)own.DeepClone()
            : new JsonObject { ["symbol"] = WatchItems.Symbol(item), ["shape"] = "rounded" };
        var editor = new BadgeEditorDialog(L10n.L("Symbol"), item.Name, start, BadgeGlyph.AccentOf(Store),
            next => SaveIcon(id, next), () => SaveIcon(id, null))
        {
            Owner = Window.GetWindow(this),
        };
        editor.ShowDialog();
    }

    /// Saves an item's icon (null removes it) and reads the rows again. Returns the refusal, or null when saved. The editor's save
    /// and reset call it; a test calls it as the editor does.
    internal string? SaveIcon(string id, JsonObject? badge)
    {
        var refused = Store.SetItems(Store.Items.Select(i => i.Id == id ? i with { Icon = badge?.ToJsonString() } : i).ToList());
        RenderWatchList();
        return refused;
    }

    /// Appends an item under a new id, unless the list is full (the Mac and settings.js keep 64 items).
    private void AddItem(WatchItem item)
    {
        var items = Store.Items.ToList();
        if (items.Count >= WatchItemsJson.MaxItems)
        {
            _actionError = L10n.L("The watch list holds at most {0} items.", WatchItemsJson.MaxItems);
            ShowErrorLine();
            return;
        }
        items.Add(item with { Id = Guid.NewGuid().ToString() });
        SaveItems(items);
    }

    private void SetEnabled(string id, bool enabled) =>
        SaveItems(Store.Items.Select(i => i.Id == id ? i with { Enabled = enabled } : i).ToList());

    private void RemoveItem(string id) => SaveItems(Store.Items.Where(i => i.Id != id).ToList());

    /// Every change of the list goes through here, so sync and the agents source pick it up. A list that cannot be saved shows
    /// its error and the rows are read again.
    private void SaveItems(IReadOnlyList<WatchItem> items)
    {
        try
        {
            // a refused list (an item the reader would drop) is not saved: its reason is shown
            var refused = Store.SetItems(items);
            _actionError = refused is null ? null : L10n.L("Couldn't save the watch list: {0}", refused);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _actionError = L10n.L("Couldn't save the watch list: {0}", e.Message);
        }
        // Read the rows again either way: after a failed save a switch must show the state the store still holds.
        RenderWatchList();
        ShowErrorLine();
    }

    /// Builds one row per agent, in the AgentKind order, with a separator between rows. An agent whose settings file cannot
    /// be edited shows its error at once, not only after a switch fails.
    private void Render()
    {
        var home = _home;
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
        var home = _home;
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
