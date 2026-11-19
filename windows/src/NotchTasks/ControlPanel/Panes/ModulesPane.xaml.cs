using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.ControlPanel.Panes;

/// The Modules pane (Preferences.swift modules): the modules on the notch in order, with arrows to move them, each one's
/// position when collapsed, colour and on/off switch. The modules not built yet keep their settings and show "Coming soon".
public partial class ModulesPane : SettingsPane
{
    private const string DefaultColor = "#808080";

    private readonly Dictionary<string, ModuleRow> _rows = new();
    private readonly bool _hasBattery;

    /// The pane. `hasBattery` says whether this PC has a battery: without one the battery row is hidden. The default asks
    /// Windows (PowerFacts.HasSystemBattery); tests pass their own answer.
    public ModulesPane(SettingsStore store, Func<bool>? hasBattery = null) : base(store)
    {
        InitializeComponent();
        _hasBattery = (hasBattery ?? PowerFacts.HasSystemBattery)();
        foreach (var id in ModuleCatalog.All.Where(ModuleCatalog.OnWindows))
        {
            _rows[id] = BuildRow(id);
        }
    }

    protected override void Apply()
    {
        var order = ModuleCatalog.Order(Store.Modules).Where(Shown).ToList();
        var disabled = Store.Disabled.ToHashSet();
        var placements = Store.Placements;
        var colors = Store.ModuleColors;

        Rows.Children.Clear();
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var row = _rows[id];
            var on = !disabled.Contains(id);
            var title = L10n.L(ModuleCatalog.Title(id));

            row.Title.Text = title;
            row.Title.SetResourceReference(TextBlock.ForegroundProperty,
                on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            row.Caption.Visibility = ModuleCatalog.Built(id) ? Visibility.Collapsed : Visibility.Visible;
            row.Tile.SetResourceReference(Border.BackgroundProperty,
                on ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");
            row.Up.IsEnabled = i > 0;
            row.Down.IsEnabled = i < order.Count - 1;
            row.On.IsChecked = on;
            AutomationProperties.SetName(row.On, title);
            Select(row.Placement, ChoiceItems.Placements, placements.TryGetValue(id, out var placement) ? placement : "auto");
            row.Color.Hex = colors.TryGetValue(id, out var hex) ? hex : DefaultColor;
            row.ResetColor.Header = L10n.L("Back to default color");

            if (i > 0)
            {
                Rows.Children.Add(new Separator { Margin = new Thickness(16, 0, 16, 0) });
            }
            Rows.Children.Add(row.Element);
        }
    }

    /// Whether a module's row is shown: the battery row only on a PC with a battery.
    private bool Shown(string id) => id != "battery" || _hasBattery;

    /// One module's controls: the arrows, the icon, the name and the note, the position and the switch on the first line,
    /// and the colour on a second line under the name.
    private ModuleRow BuildRow(string id)
    {
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.Medium,
        };
        var caption = new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
            Text = L10n.L("Coming soon"),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, caption } };

        var up = Arrow("", L10n.L("Move up"), () => Move(id, -1));
        var down = Arrow("", L10n.L("Move down"), () => Move(id, 1));
        var arrows = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { up, down },
        };

        var glyph = new TextBlock
        {
            Text = ModuleCatalog.Glyph(id),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var tile = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = glyph,
        };

        var placement = new ComboBox
        {
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = ChoiceItems.Placements,
            DisplayMemberPath = nameof(Choice.Title),
            ToolTip = L10n.L("Position when collapsed"),
        };
        placement.SelectionChanged += (_, _) => ChangePlacement(id, placement);

        var on = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L10n.L("On"),
        };
        on.Click += (_, _) => ChangeOn(id, on.IsChecked == true);

        var color = new ColorField();
        color.ValueChanged += (_, _) => ChangeColor(id, color.Hex);
        var resetColor = new MenuItem { Header = L10n.L("Back to default color") };
        resetColor.Click += (_, _) => ResetColor(id);
        color.ContextMenu = new ContextMenu { Items = { resetColor } };

        var colorLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        colorLine.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("RowLabel"),
            Margin = new Thickness(0, 0, 12, 0),
            Text = L10n.L("Color"),
        });
        colorLine.Children.Add(color);

        var element = new Grid { Margin = new Thickness(16, 10, 16, 10) };
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        element.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        element.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(arrows, element, 0, 0);
        Place(tile, element, 0, 1);
        Place(text, element, 0, 2);
        Place(placement, element, 0, 3);
        Place(on, element, 0, 4);
        Place(colorLine, element, 1, 2);

        return new ModuleRow(element, up, down, tile, title, caption, placement, on, color, resetColor);
    }

    private static Button Arrow(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = tip,
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static void Place(UIElement element, Grid grid, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// Swaps the module with its neighbour and saves the whole order once. The moved row keeps the keyboard focus.
    private void Move(string id, int delta)
    {
        // Hidden and unknown ids keep their stored places (ModuleCatalog.Reorder): a hidden row is not a neighbour, so an
        // arrow swaps with the nearest shown module and the stored list keeps the hidden battery where it was.
        var stored = Store.Modules;
        var order = ModuleCatalog.Reorder(stored, id, delta, Shown);
        if (order.SequenceEqual(stored))
        {
            return;
        }
        Write("modules", ToArray(order));

        var row = _rows[id];
        (delta < 0 ? row.Up : row.Down).Focus();
    }

    /// Saves the switch: a module that is on is not in the disabled list.
    private void ChangeOn(string id, bool on)
    {
        var disabled = Store.Disabled.Where(item => item != id).ToList();
        if (!on)
        {
            disabled.Add(id);
        }
        Write("disabled", ToArray(disabled));
    }

    /// Saves the position. Automatic is stored as no entry, as the Mac does.
    private void ChangePlacement(string id, ComboBox placement)
    {
        if (placement.SelectedItem is not Choice choice)
        {
            return;
        }
        var map = new Dictionary<string, string>(Store.Placements);
        if (choice.Value == "auto")
        {
            map.Remove(id);
        }
        else
        {
            map[id] = choice.Value;
        }
        Write("placements", ToObject(map));
    }

    private void ChangeColor(string id, string hex)
    {
        var map = new Dictionary<string, string>(Store.ModuleColors) { [id] = hex };
        Write("moduleColors", ToObject(map));
    }

    private void ResetColor(string id)
    {
        var map = new Dictionary<string, string>(Store.ModuleColors);
        map.Remove(id);
        Write("moduleColors", ToObject(map));
    }

    private static JsonArray ToArray(IEnumerable<string> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(JsonValue.Create(id));
        }
        return array;
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map)
        {
            obj[key] = JsonValue.Create(value);
        }
        return obj;
    }

    /// The controls of one module row, kept for the life of the pane.
    private sealed record ModuleRow(
        Grid Element,
        Button Up,
        Button Down,
        Border Tile,
        TextBlock Title,
        TextBlock Caption,
        ComboBox Placement,
        CheckBox On,
        ColorField Color,
        MenuItem ResetColor);
}
