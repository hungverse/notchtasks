using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel;

/// The editor body of one badge, shared by the Icons pane's editor and the tool dialog: the symbol tiles, and the style (shape,
/// border, background, symbol colour). Show gives it a badge's values. A change the user makes is raised as Edit with the change
/// to apply to the badge; the host applies it, saves it and shows the badge again. Values set by Show raise nothing.
public partial class BadgeEditorBody : UserControl
{

    private readonly IReadOnlyList<Choice> _shapes = [Choice.Key("circle", "Circle"), Choice.Key("rounded", "Square")];

    /// True while Show gives the controls their values, so the handlers that follow a control's own change stay quiet.
    private bool _showing;

    public BadgeEditorBody()
    {
        InitializeComponent();

        ShapeBox.ItemsSource = _shapes;
        ShapeBox.SelectionChanged += (_, _) =>
        {
            if (!_showing && ShapeBox.SelectedItem is Choice choice)
            {
                Change(badge => badge["shape"] = JsonValue.Create(choice.Value));
            }
        };
        BorderBox.Click += (_, _) => Change(badge => badge["border"] = JsonValue.Create(BorderBox.IsChecked == true));
        NoFillBox.Click += (_, _) => Change(badge => badge["plain"] = NoFillBox.IsChecked == true ? JsonValue.Create(true) : null);
        FillColor.ValueChanged += (_, _) => Change(badge =>
        {
            // Picking a colour turns the fill back on, as the Mac does.
            badge["color"] = JsonValue.Create(FillColor.Hex);
            badge["plain"] = null;
        });
        SymbolColor.ValueChanged += (_, _) => Change(badge => badge["fg"] = JsonValue.Create(SymbolColor.Hex));
        LibraryButton.Click += (_, _) => LibraryRequested?.Invoke(this, EventArgs.Empty);

        foreach (var code in BadgeEdits.SymbolCodes)
        {
            var face = new Border
            {
                Width = 40,
                Height = 36,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Child = new TextBlock
                {
                    Text = ModuleGlyphs.Legacy(code) ?? ModuleGlyphs.Fallback,
                    FontFamily = FluentIcons.Regular,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var button = new Button
            {
                Style = (Style)FindResource("TileButton"),
                Content = face,
                Margin = new Thickness(0, 0, 6, 6),
                Tag = code,
            };
            button.Click += (_, _) => Change(badge => BadgeEdits.ApplySymbol(badge, BadgeEdits.SymbolFor(code)));
            SymbolGrid.Children.Add(button);
        }
    }

    /// Raised when the user changes the badge: the change to apply to it.
    public event Action<Action<JsonObject>>? Edit;

    /// Raised when the user asks for the Icon Library. The button shows only when ShowLibrary is set.
    public event EventHandler? LibraryRequested;

    /// Whether the "Choose an animated icon" button shows (the Icons pane has the library; the tool dialog does not).
    public bool ShowLibrary
    {
        get => LibraryButton.Visibility == Visibility.Visible;
        set => LibraryButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Gives the controls the badge's values. accent is what a badge without a colour fills with. Raises nothing.
    public void Show(JsonObject badge, Color accent)
    {
        _showing = true;
        try
        {
            var shape = StringOf(badge, "shape") == "rounded" ? "rounded" : "circle";
            ShapeBox.SelectedItem = _shapes.First(choice => choice.Value == shape);
            BorderBox.IsChecked = FlagOf(badge, "border");
            NoFillBox.IsChecked = FlagOf(badge, "plain");
            FillColor.Hex = HexColor.TryNormalize(StringOf(badge, "color"), out var fill) ? fill : Hex(accent);
            SymbolColor.Hex = HexColor.TryNormalize(StringOf(badge, "fg"), out var ink) ? ink : "#FFFFFF";

            var symbol = StringOf(badge, "symbol");
            var highlight = Solid(accent);
            foreach (var child in SymbolGrid.Children.OfType<Button>())
            {
                var selected = symbol == BadgeEdits.SymbolFor((string)child.Tag);
                if (child.Content is Border face)
                {
                    face.BorderThickness = new Thickness(selected ? 2 : 0);
                    face.BorderBrush = highlight;
                }
            }
        }
        finally
        {
            _showing = false;
        }
    }

    /// Raises a change the user made, unless Show is giving the values.
    private void Change(Action<JsonObject> change)
    {
        if (!_showing)
        {
            Edit?.Invoke(change);
        }
    }

    private static string? StringOf(JsonObject badge, string key) =>
        badge[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool FlagOf(JsonObject badge, string key) =>
        badge[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
