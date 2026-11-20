using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core.Devices;

namespace NotchTasks.Pill.Sections;

/// The Devices section of the open pill (Mac DevicesCard): one row of capsules, one per listed device, in a horizontal scroller whose
/// bar is hidden. A capsule shows its symbol in the module's colour (else the default blue), then the lowest battery level (red at 20 %
/// or less) or, with no level, the name. Its tooltip is "name · battery". The row is one element kept for the pill's life: Update
/// refreshes its capsules, and returns false (and leaves the card alone) while no device is listed.
public sealed class DevicesCard : Border
{
    private static readonly Color DefaultTint = Color.FromRgb(0x0A, 0x84, 0xFF);

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Focusable = false };
    private readonly Dictionary<string, DeviceCapsule> _capsules = new(StringComparer.Ordinal);
    private SolidColorBrush? _tint;

    public DevicesCard()
    {
        Focusable = false;
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _row,
        };
    }

    /// The capsules now shown, in order.
    public IReadOnlyList<DeviceCapsule> Capsules => _row.Children.OfType<DeviceCapsule>().ToList();

    /// Refreshes the row from the devices module and returns true. Returns false, and leaves the card alone, when no device is listed.
    public bool Update(AppState state)
    {
        var devices = state.Devices.Devices;
        if (devices.Count == 0)
        {
            return false;
        }
        var color = TintOf(state.Settings.ModuleColors);
        if (_tint is null || _tint.Color != color)
        {
            _tint = Frozen(new SolidColorBrush(color));  // one brush while the colour holds, so a capsule keeps its glyph
        }
        var tint = _tint;

        var wanted = new List<DeviceCapsule>();
        foreach (var device in devices)
        {
            if (!_capsules.TryGetValue(device.Id, out var capsule))
            {
                capsule = new DeviceCapsule();
                _capsules[device.Id] = capsule;
            }
            capsule.Update(device, tint);
            wanted.Add(capsule);
        }
        foreach (var id in _capsules.Keys.Where(id => !devices.Any(d => d.Id == id)).ToList())
        {
            _capsules.Remove(id);
        }

        if (!SameChildren(wanted))
        {
            _row.Children.Clear();
            foreach (var capsule in wanted)
            {
                _row.Children.Add(capsule);
            }
        }
        return true;
    }

    /// The devices colour: the module's colour when it is set, else the default blue (#0A84FF).
    public static Color TintOf(IReadOnlyDictionary<string, string> moduleColors) =>
        moduleColors.TryGetValue("devices", out var hex) && HexColor.TryNormalize(hex, out var normalized)
            ? HexColor.ToColor(normalized)
            : DefaultTint;

    /// True when the row holds exactly these capsules, in this order.
    private bool SameChildren(IReadOnlyList<DeviceCapsule> wanted)
    {
        if (_row.Children.Count != wanted.Count)
        {
            return false;
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!ReferenceEquals(_row.Children[i], wanted[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// One device's capsule: the symbol glyph, then the battery level or the name. Kept per device id, so an update changes its text in place.
public sealed class DeviceCapsule : Border
{
    private const double GlyphSize = 10;
    private const double LowPercent = 20;
    private const double NameMaxWidth = 70;

    private static readonly Brush FillBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly FontFamily TextFamily = new("Segoe UI Variable Text, Segoe UI");

    private readonly StackPanel _content = new() { Orientation = Orientation.Horizontal, Focusable = false };
    private readonly TextBlock _label;
    private string? _symbol;
    private Brush? _glyphBrush;
    private FrameworkElement? _glyph;

    public DeviceCapsule()
    {
        Background = FillBrush;
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(7, 3, 7, 3);
        Margin = new Thickness(0, 0, 6, 0);
        Focusable = false;
        _label = new TextBlock
        {
            FontFamily = TextFamily,
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Focusable = false,
        };
        Typography.SetNumeralAlignment(_label, FontNumeralAlignment.Tabular);
        _content.Children.Add(_label);
        Child = _content;
    }

    /// The label's text: the battery level, or the name when there is no level.
    public string Text => _label.Text;

    /// The label's colour: red at 20 % or less, else the light text.
    public Brush LabelBrush => _label.Foreground;

    /// Shows a device: its glyph in the tint, its label and its tooltip.
    public void Update(Device device, Brush tint)
    {
        if (_glyph is null || _symbol != device.Symbol || !ReferenceEquals(_glyphBrush, tint))
        {
            if (_glyph is not null)
            {
                _content.Children.Remove(_glyph);
            }
            _glyph = DeviceArtView.Glyph(device.Symbol, tint, GlyphSize);
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            _content.Children.Insert(0, _glyph);
            _symbol = device.Symbol;
            _glyphBrush = tint;
        }

        var low = device.Lowest is int level && level <= LowPercent;
        _label.Text = device.Battery ?? device.Name;
        _label.Foreground = device.Lowest is not null && low ? LowBrush : TextBrush;
        _label.MaxWidth = device.Battery is null ? NameMaxWidth : double.PositiveInfinity;
        ToolTip = device.Battery is null ? device.Name : $"{device.Name} · {device.Battery}";
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
