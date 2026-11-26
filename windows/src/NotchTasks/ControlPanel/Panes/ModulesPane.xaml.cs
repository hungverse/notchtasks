using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.ControlPanel.Panes;

/// The Modules pane (Preferences.swift modules): the modules on the notch in order, each one's position when collapsed,
/// colour and on/off switch. A row moves by its arrows (the keyboard) or by a drag (the Mac's list move). The modules not
/// built yet keep their settings and show "Coming soon".
public partial class ModulesPane : SettingsPane
{
    private const string DefaultColor = "#808080";

    /// The private data format a dragged row carries: the module's id.
    private const string DragFormat = "NotchTasks.Module";

    /// The tile of a module that is off: the Mac's grey, 50% (the Mac uses .gray at half opacity).
    private static readonly SolidColorBrush OffTile = new(Color.FromArgb(0x80, 0x80, 0x80, 0x80));

    private readonly Dictionary<string, ModuleRow> _rows = new();
    private readonly bool _hasBattery;
    private readonly AppState? _state;  // the calendar's state: its hint shows when the store cannot be opened

    /// The visible module ids in the order shown (set by Apply): a drag works on these indices.
    private List<string> _order = new();

    /// A press on a row that is not on one of its controls: the module and where it was pressed (window coordinates).
    private (string Id, Point At)? _pressed;

    /// The accent line where a dragged row will land, while a drag is over the rows.
    private DropLine? _dropLine;

    /// The pane. `hasBattery` says whether this PC has a battery: without one the battery row is hidden. The default asks
    /// Windows (PowerFacts.HasSystemBattery); tests pass their own answer.
    public ModulesPane(SettingsStore store, Func<bool>? hasBattery = null) : this(store, hasBattery, null)
    {
    }

    /// The pane with the app state, which tells the calendar's hint (null: no hint is shown).
    public ModulesPane(SettingsStore store, Func<bool>? hasBattery, AppState? state) : base(store)
    {
        InitializeComponent();
        _hasBattery = (hasBattery ?? PowerFacts.HasSystemBattery)();
        _state = state;
        // A dragged row lands on the rows panel: the drop line and the drop are handled there.
        Rows.AllowDrop = true;
        Rows.DragOver += OnRowsDragOver;
        Rows.DragLeave += (_, _) => HideDropLine();
        Rows.Drop += OnRowsDrop;
        foreach (var id in ModuleCatalog.All.Where(ModuleCatalog.OnWindows))
        {
            _rows[id] = BuildRow(id);
        }
    }

    protected override void Apply()
    {
        var order = ModuleCatalog.Order(Store.Modules).Where(Shown).ToList();
        _order = order;
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
            // The blurb says what the module does; a module not built yet says so after it.
            row.Caption.Text = ModuleCatalog.Built(id)
                ? L10n.L(ModuleCatalog.Blurb(id))
                : L10n.L(ModuleCatalog.Blurb(id)) + " · " + L10n.L("Coming soon");
            if (on)
            {
                row.Tile.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            }
            else
            {
                row.Tile.Background = OffTile;
            }
            // the calendar's hint under its row when the store cannot be opened (the pill shows nothing for calendar)
            row.Hint.Text = id == "calendar" && _state?.Calendar.Unavailable == true
                ? L10n.L("Calendar can't be opened. Check Settings > Privacy & security > Calendar.")
                : string.Empty;
            row.Hint.Visibility = row.Hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Up.IsEnabled = i > 0;
            row.Down.IsEnabled = i < order.Count - 1;
            row.On.IsChecked = on;
            AutomationProperties.SetName(row.On, title);
            Select(row.Placement, ChoiceItems.Placements, placements.TryGetValue(id, out var placement) ? placement : "auto");
            row.Color.Hex = colors.TryGetValue(id, out var hex) ? hex : DefaultColor;
            row.Swatch.Background = HexColor.Brush(row.Color.Hex);
            row.ResetColor.Header = L10n.L("Back to default color");

            if (i > 0)
            {
                Rows.Children.Add(new Separator { Margin = new Thickness(56, 0, 16, 0) });
            }
            Rows.Children.Add(row.Element);
        }
    }

    /// Whether a module's row is shown: the battery row only on a PC with a battery.
    private bool Shown(string id) => id != "battery" || _hasBattery;

    /// One module's controls on one line, as on the Mac: the arrows, the icon, the name and the note, the position, a colour
    /// swatch and the switch. The colour's hex field opens in a popup from the swatch, so the row never gets cut.
    private ModuleRow BuildRow(string id)
    {
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        };
        var caption = new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        // the calendar's hint, under its note, when the store cannot be opened (Apply shows it; the pill shows nothing for calendar)
        var hint = new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, caption, hint } };

        var up = Arrow("", L10n.L("Move up"), () => Move(id, -1));
        var down = Arrow("", L10n.L("Move down"), () => Move(id, 1));
        var arrows = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { up, down },
        };

        var glyph = new TextBlock
        {
            Text = ModuleCatalog.Glyph(id),
            FontFamily = ModuleCatalog.GlyphFilled(id) ? FluentIcons.Filled : FluentIcons.Regular,
            Style = (Style)FindResource("IconGlyph"),
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
            Width = 140,
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

        // The swatch shows the colour; a click opens the hex field and the palette in a popup beside it.
        var color = new ColorField();
        var resetColor = new MenuItem { Header = L10n.L("Back to default color") };
        resetColor.Click += (_, _) => ResetColor(id);
        var swatch = new Button
        {
            Style = (Style)FindResource("ColorWell"),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L10n.L("Color"),
            ContextMenu = new ContextMenu { Items = { resetColor } },
        };
        AutomationProperties.SetName(swatch, L10n.L("Color"));
        color.ValueChanged += (_, _) =>
        {
            swatch.Background = HexColor.Brush(color.Hex);
            ChangeColor(id, color.Hex);
        };
        var popup = new Popup
        {
            StaysOpen = false,
            Placement = PlacementMode.Bottom,
            PlacementTarget = swatch,
            AllowsTransparency = true,
            Child = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(10), Child = color },
        };
        swatch.Click += (_, _) => popup.IsOpen = true;

        // A press on the row, not on one of its controls, and a move of more than the drag distance drag the row.
        var element = new Grid { Margin = new Thickness(16, 0, 16, 0), MinHeight = 56, Cursor = Cursors.SizeNS };
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressed = null;
            if (!IsOnControl(e.OriginalSource as DependencyObject, element))
            {
                _pressed = (id, e.GetPosition(null));
            }
        };
        element.PreviewMouseLeftButtonUp += (_, _) => _pressed = null;
        element.MouseMove += (_, e) => StartDrag(id, element, e);
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        element.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(arrows, element, 0, 0);
        Place(tile, element, 0, 1);
        Place(text, element, 0, 2);
        Place(placement, element, 0, 3);
        Place(swatch, element, 0, 4);
        Place(on, element, 0, 5);

        return new ModuleRow(element, up, down, tile, title, caption, hint, placement, swatch, on, color, resetColor);
    }

    private Button Arrow(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("MoveArrow"),
            ToolTip = tip,
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = FluentIcons.Regular,
                FontSize = 10,
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
        if (!Commit(ModuleCatalog.Reorder(Store.Modules, id, delta, Shown)))
        {
            return;
        }
        var row = _rows[id];
        (delta < 0 ? row.Up : row.Down).Focus();
    }

    /// Moves a dragged row to visible index `to`: the same move and write as the arrows (Commit).
    private void MoveRow(string id, int to) =>
        Commit(ModuleCatalog.Move(Store.Modules, _order.IndexOf(id), to, Shown));

    /// Saves a module order, if it changed: the arrows and a drag both end here. Returns false when nothing moved.
    private bool Commit(IReadOnlyList<string> order)
    {
        if (order.SequenceEqual(Store.Modules))
        {
            return false;
        }
        Write("modules", ToArray(order));
        return true;
    }

    /// Whether a press landed on a control of a row (an arrow, the position, the colour well, the switch): such a press keeps
    /// its own click and does not start a drag.
    private static bool IsOnControl(DependencyObject? source, DependencyObject row)
    {
        for (var node = source; node is not null && node != row; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or ComboBox)
            {
                return true;
            }
        }
        return false;
    }

    /// Starts the drag of a row once the mouse has moved more than the drag distance from the press, vertically.
    private void StartDrag(string id, UIElement element, MouseEventArgs e)
    {
        if (_pressed is not { } press || press.Id != id || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        if (Math.Abs(e.GetPosition(null).Y - press.At.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _pressed = null;
        // DoDragDrop returns when the drop is made or the drag is cancelled (Esc, or released outside the pane).
        DragDrop.DoDragDrop(element, new DataObject(DragFormat, id), DragDropEffects.Move);
        HideDropLine();
    }

    private void OnRowsDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            HideDropLine();
            return;
        }
        e.Effects = DragDropEffects.Move;
        ShowDropLine(SlotAt(e.GetPosition(Rows).Y));
    }

    private void OnRowsDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideDropLine();
        if (e.Data.GetData(DragFormat) is not string id || !_order.Contains(id))
        {
            return;
        }
        // The slot is counted with the row still in its place, so a row dropped below itself lands one place lower.
        var from = _order.IndexOf(id);
        var slot = SlotAt(e.GetPosition(Rows).Y);
        MoveRow(id, slot > from ? slot - 1 : slot);
    }

    /// The slot a pointer at `y` (in the rows panel) falls in: 0 above the first row's middle, n below the last row's.
    private int SlotAt(double y)
    {
        var slot = 0;
        for (var i = 0; i < _order.Count; i++)
        {
            if (y < RowTop(_order[i]) + _rows[_order[i]].Element.ActualHeight / 2)
            {
                break;
            }
            slot = i + 1;
        }
        return slot;
    }

    /// The y of the top edge of a row, in the rows panel.
    private double RowTop(string id) => _rows[id].Element.TransformToAncestor(Rows).Transform(new Point(0, 0)).Y;

    /// The y of a slot's line: the top edge of the row in that slot, or the bottom edge of the last row.
    private double SlotY(int slot)
    {
        if (_order.Count == 0)
        {
            return 0;
        }
        if (slot < _order.Count)
        {
            return RowTop(_order[slot]);
        }
        var last = _rows[_order[^1]].Element;
        return RowTop(_order[^1]) + last.ActualHeight;
    }

    private void ShowDropLine(int slot)
    {
        if (AdornerLayer.GetAdornerLayer(Rows) is not { } layer)
        {
            return;
        }
        if (_dropLine is null)
        {
            _dropLine = new DropLine(Rows);
            layer.Add(_dropLine);
        }
        _dropLine.Y = SlotY(slot);
    }

    private void HideDropLine()
    {
        if (_dropLine is null)
        {
            return;
        }
        AdornerLayer.GetAdornerLayer(Rows)?.Remove(_dropLine);
        _dropLine = null;
    }

    /// The 2 DIP accent line where a dragged row will land. It is an adorner on the rows panel, so the rows do not move.
    private sealed class DropLine : Adorner
    {
        private double _y;

        public DropLine(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
        }

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var brush = (Brush)((FrameworkElement)AdornedElement).FindResource("AccentFillColorDefaultBrush");
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(0, _y - 1, ActualWidth, 2), 1, 1);
        }
    }

    /// Saves the switch: a module that is on is not in the disabled list.
    private void ChangeOn(string id, bool on) => Write("disabled", DisabledWith(Store.Disabled, id, on));

    /// The disabled list with module `id` turned on or off: a module that is on is never in the list. The Overview's switches
    /// write through here too, so both panes save the same list.
    public static JsonArray DisabledWith(IEnumerable<string> disabled, string id, bool on)
    {
        var list = disabled.Where(item => item != id).ToList();
        if (!on)
        {
            list.Add(id);
        }
        return ToArray(list);
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
        TextBlock Hint,
        ComboBox Placement,
        Button Swatch,
        CheckBox On,
        ColorField Color,
        MenuItem ResetColor);
}
