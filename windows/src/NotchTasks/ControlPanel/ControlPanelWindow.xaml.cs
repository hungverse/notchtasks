using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel;

/// The Control Panel: a sidebar of panes, as the Mac's Preferences window. There is one window; Show brings it to the
/// front. Its size and last pane are kept in state.json.
public partial class ControlPanelWindow : Window
{
    private const string SizeKey = "windowControlPanel";
    private const string PaneKey = "lastPane";

    private static ControlPanelWindow? _window;

    private readonly Dictionary<PaneId, UserControl> _panes = new();
    private PaneId _pane;

    private ControlPanelWindow(AppState state, PaneId pane)
    {
        State = state;
        InitializeComponent();

        // The saved size (or the default), never larger than the work area: the window is then fully on screen, and
        // CenterScreen places it inside the work area.
        var (width, height) = ReadSize() ?? (Width, Height);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(width, area.Width);
        Height = Math.Min(height, area.Height);

        Sidebar.ItemsSource = PaneItem.All;
        Sidebar.SelectionChanged += OnSelectionChanged;
        VersionText.Text = AppVersion();
        Closing += (_, _) =>
        {
            SaveSize();
            SaveLastPane(_pane);
            IconSelection.Clear();
            SpriteCache.Retain(PillSprites());
        };
        // The pane's name is the window's title (the Mac's navigationTitle); it follows the language while the window is open.
        LocalizedStrings.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            LocalizedStrings.Instance.PropertyChanged -= OnLanguageChanged;
            _window = null;
        };
        SelectPane(pane);
    }

    /// The app state the window was opened with.
    public AppState State { get; }

    /// Opens the Control Panel on `pane`, or on the last pane shown. When it is already open, it selects `pane` (if given)
    /// and brings the window to the front: the user asked for it.
    public static void Show(AppState state, PaneId? pane = null)
    {
        if (_window is null)
        {
            _window = new ControlPanelWindow(state, pane ?? LastPane());
            _window.Show();
        }
        else if (pane is { } chosen)
        {
            _window.SelectPane(chosen);
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    /// The version shown at the foot of the sidebar: the informational version up to its build hash ("1.0.0+abc" shows
    /// "1.0.0"), or major.minor when the build has none.
    private static string AppVersion()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(ControlPanelWindow).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }
        var version = assembly.GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}";
    }

    /// The sprites the pill's badges name (settings "badges"): the ones that stay cached when the Control Panel closes.
    private HashSet<string> PillSprites()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in State.Settings.Badges)
        {
            if (node is JsonObject badge && badge["sprite"] is JsonValue value && value.TryGetValue(out string? id) && id is not null)
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private void SelectPane(PaneId pane)
    {
        // Selecting the row raises SelectionChanged, which shows the pane.
        Sidebar.SelectedItem = PaneItem.All.First(item => item.Pane == pane);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sidebar.SelectedItem is not PaneItem item)
        {
            return;
        }
        _pane = item.Pane;
        Title = item.Title;
        PaneHost.Content = PaneFor(item.Pane);
        // Every pane opens at its title, not wherever the previous pane was scrolled.
        PaneScroll.ScrollToTop();
    }

    /// The window's title follows the language: the selected pane's sidebar title, read again.
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Sidebar.SelectedItem is PaneItem item)
        {
            Title = item.Title;
        }
    }

    private UserControl PaneFor(PaneId pane)
    {
        if (!_panes.TryGetValue(pane, out var control))
        {
            control = pane switch
            {
                PaneId.Overview => new OverviewPane(State.Settings, State),
                PaneId.Modules => new ModulesPane(State.Settings),
                PaneId.Permissions => new PermissionsPane(State.Settings, State.Helper),
                PaneId.Watching => new WatchingPane(State.Settings, State.Processes, AppFiles.Home),
                PaneId.Appearance => new AppearancePane(State.Settings),
                PaneId.Badges => new BadgesPane(State.Settings, State),
                PaneId.Store => new IconLibraryPane(State.Settings),
                PaneId.Sounds => new SoundsPane(State.Settings),
                PaneId.Behavior => new BehaviorPane(State.Settings),
                _ => throw new ArgumentOutOfRangeException(nameof(pane)),
            };
            _panes[pane] = control;
        }
        return control;
    }

    private static PaneId LastPane()
    {
        try
        {
            var name = StateJson.Get(AppPaths.StateFile, PaneKey)?.GetValue<string>();
            return Enum.TryParse<PaneId>(name, ignoreCase: true, out var pane) ? pane : PaneId.Overview;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return PaneId.Overview;
        }
    }

    /// Written when the window closes, not on each selection.
    private static void SaveLastPane(PaneId pane)
    {
        try
        {
            StateJson.Set(AppPaths.StateFile, PaneKey, JsonValue.Create(pane.ToString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not save the last Control Panel pane", e));
        }
    }

    /// The size from state.json, never below the window's minimum; null when there is none.
    private (double Width, double Height)? ReadSize()
    {
        try
        {
            if (StateJson.Get(AppPaths.StateFile, SizeKey) is not JsonObject size)
            {
                return null;
            }
            var w = size["w"]?.GetValue<double>();
            var h = size["h"]?.GetValue<double>();
            if (w is not { } width || h is not { } height || width <= 0 || height <= 0)
            {
                return null;
            }
            return (Math.Max(width, MinWidth), Math.Max(height, MinHeight));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveSize()
    {
        try
        {
            // RestoreBounds is the normal size, also when the window is maximized.
            var size = new JsonObject
            {
                ["w"] = JsonValue.Create(Math.Round(RestoreBounds.Width)),
                ["h"] = JsonValue.Create(Math.Round(RestoreBounds.Height)),
            };
            StateJson.Set(AppPaths.StateFile, SizeKey, size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not save the Control Panel size", e));
        }
    }
}
