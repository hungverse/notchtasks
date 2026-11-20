using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.Core;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;

namespace NotchTasks.ControlPanel.Panes;

/// The Build & CI box of the Watching pane (SettingsView.swift BuildToolsBox): the tools this PC has built with, each with its icon.
/// Show all adds every known tool, after the seen ones. A tool with a custom icon has a dot. A click gives the tool its icon (the
/// default symbol, on a rounded shape, the first time) and opens its editor. The pane calls Render when the settings change.
public partial class BuildToolsBox : UserControl
{
    private const int IconSize = 20;
    private const double TileWidth = 150;

    private readonly SettingsStore _store;
    private readonly Action<string, JsonObject> _openEditor;

    /// The box over a settings store. openEditor shows a tool's editor (the default shows BadgeEditorDialog); a test passes its own.
    public BuildToolsBox(SettingsStore store, Action<string, JsonObject>? openEditor = null)
    {
        _store = store;
        _openEditor = openEditor ?? ((tool, badge) => ShowEditor(tool, badge));
        InitializeComponent();
        ShowAllBox.Click += (_, _) => Render();
        Render();
    }

    /// Rebuilds the caption and the tool buttons from the settings.
    public void Render()
    {
        var seen = _store.SeenTools;
        var showAll = ShowAllBox.IsChecked == true;
        CaptionText.Text = seen.Count == 0 && !showAll
            ? L10n.L("No builds seen yet. Turn on \"Show all\" to set icons ahead of time.")
            : L10n.L("Nothing to add: any running tool shows up. Click a tool to change its icon.");

        var tools = showAll
            ? seen.Concat(DevRules.KnownTools.Where(tool => !seen.Contains(tool)).OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase))
            : seen;

        var icons = _store.ToolIcons;
        var accent = BadgeGlyph.AccentOf(_store);
        Tools.Children.Clear();
        foreach (var tool in tools)
        {
            Tools.Children.Add(ToolButton(tool, icons.TryGetValue(tool, out var badge) ? badge : null, accent));
        }
    }

    /// One tool: its icon (the custom badge, else the default symbol in the accent, as the Mac draws it), its name, and the dot
    /// when it has a custom icon.
    private Button ToolButton(string tool, JsonObject? custom, Color accent)
    {
        FrameworkElement icon = custom is not null
            ? BadgeGlyph.Create(custom, IconSize, accent)
            : JobLead.Element(DevRules.DefaultSymbol(tool), IconSize, Solid(accent)) ?? new Border { Width = IconSize, Height = IconSize };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(icon, row, 0);

        var name = new TextBlock
        {
            Text = tool,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Place(name, row, 1);

        if (custom is not null)
        {
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Solid(accent),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = L10n.L("Changed"),
            };
            Place(dot, row, 2);
        }

        var face = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(8),
            Child = row,
        };
        face.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");

        var button = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Tag = tool,
            Width = TileWidth,
            Margin = new Thickness(0, 0, 8, 8),
            Content = face,
        };
        button.Click += (_, _) => Open(tool);
        return button;
    }

    /// Gives the tool its icon when it has none, then opens its editor. The box is read again when the editor closes.
    private void Open(string tool)
    {
        if (!_store.ToolIcons.ContainsKey(tool))
        {
            _store.SetToolIcon(tool, new JsonObject { ["symbol"] = DevRules.DefaultSymbol(tool), ["shape"] = "rounded" });
        }
        if (_store.ToolIcons.TryGetValue(tool, out var badge))
        {
            _openEditor(tool, badge);
        }
        Render();
    }

    private void ShowEditor(string tool, JsonObject badge)
    {
        var dialog = new BadgeEditorDialog(_store, tool, badge) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private static void Place(UIElement element, Grid grid, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
