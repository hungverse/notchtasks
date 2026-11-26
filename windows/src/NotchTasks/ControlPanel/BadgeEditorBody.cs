using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel;

/// The editor body of one badge, shared by the Icons pane's editor and the tool dialog: what it shows (a symbol tile, letters or an
/// emoji, a picture, or a GIF) and the style (shape, border, background, ink). Show gives it a badge's values. A change the user makes
/// is raised as Edit with the change to apply to the badge; the host applies it, saves it and shows the badge again. Values set by Show
/// raise nothing. A picture or GIF is copied into the Badges folder first, so a file the user picked is never held open.
public partial class BadgeEditorBody : UserControl
{
    /// The kinds of content, as the Mac's BadgeEditor offers them.
    private enum Kind
    {
        Symbol,
        Text,
        Picture,
        Animation,
    }

    private readonly IReadOnlyList<Choice> _shapes = [Choice.Key("circle", "Circle"), Choice.Key("rounded", "Square")];

    private readonly IReadOnlyList<Choice> _kinds =
    [
        Choice.Key("symbol", "Symbol"), Choice.Key("text", "Text, emoji"), Choice.Key("picture", "Picture"),
        Choice.Key("animation", "Animation"),
    ];

    /// The quick picks of the text kind (the Mac's list), as code points so the source holds no emoji symbols.
    private static readonly string[] Emoji =
    [
        "\U0001F525", "\U0001F680", "⚡", "\U0001F916", "\U0001F431", "\U0001F436", "\U0001F4BB", "☕",
        "✅", "\U0001F3AF", "\U0001F31F", "\U0001F4A1", "\U0001F6E0", "\U0001F340", "\U0001F3A7", "\U0001F9E0",
    ];

    /// True while Show gives the controls their values, so the handlers that follow a control's own change stay quiet.
    private bool _showing;

    /// The kind on screen. It is the one the user chose, or the badge's own kind when the badge is shown for the first time.
    private Kind? _kind;

    /// The badge last shown: a different badge starts on its own kind, the same one keeps the kind the user picked.
    private string? _shownId;

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
        KindBox.ItemsSource = _kinds;
        KindBox.SelectionChanged += (_, _) =>
        {
            // choosing a kind only shows its controls; the badge changes when something is picked
            if (!_showing && KindBox.SelectedItem is Choice choice && KindOfValue(choice.Value) is { } kind)
            {
                _kind = kind;
                ShowKind(kind);
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
        LetterBox.TextChanged += (_, _) =>
        {
            if (!_showing)
            {
                Change(badge => BadgeEdits.ApplyText(badge, LetterBox.Text));
            }
        };
        PictureButton.Click += (_, _) => PickPicture();
        GifButton.Click += (_, _) => PickGif();
        LibraryButton.Click += (_, _) => LibraryRequested?.Invoke(this, EventArgs.Empty);

        foreach (var emoji in Emoji)
        {
            var button = new Button
            {
                Style = (Style)FindResource("TileButton"),
                Content = new TextBlock
                {
                    Text = emoji,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Width = 40,
                Height = 36,
                Margin = new Thickness(0, 0, 6, 6),
            };
            button.Click += (_, _) => LetterBox.Text = emoji;  // the text change raises the edit
            EmojiGrid.Children.Add(button);
        }

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

            var id = StringOf(badge, "id");
            if (id != _shownId)
            {
                _shownId = id;
                _kind = null;
            }
            var kind = _kind ?? KindOf(badge);
            _kind = kind;
            KindBox.SelectedItem = _kinds.First(choice => choice.Value == ValueOf(kind));
            ShowKind(kind);

            var letters = StringOf(badge, "text") ?? string.Empty;
            if (LetterBox.Text != letters)
            {
                LetterBox.Text = letters;
            }
        }
        finally
        {
            _showing = false;
        }
    }

    /// Uses a picture the user picked: a copy is made in the Badges folder and the badge shows it. A file that is too big or is not a
    /// picture is refused, and its reason shows; nothing changes then.
    internal void UsePicture(string source)
    {
        var copy = BadgeImages.Copy(source, BadgeImages.Folder);
        if (copy.Path is not { } path)
        {
            ShowError(copy.Refusal);
            return;
        }
        ShowError(null);
        Change(badge => BadgeEdits.ApplyPicture(badge, path));
    }

    /// Uses a GIF the user picked, copied as UsePicture copies a picture. Only a GIF is taken; the fill is turned off for it, as the Mac does.
    internal void UseAnimation(string source)
    {
        var copy = BadgeImages.Copy(source, BadgeImages.Folder);
        if (copy.Path is not { } path)
        {
            ShowError(copy.Refusal);
            return;
        }
        if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            BadgeImages.DiscardUnused(path, BadgeImages.Folder, Array.Empty<string?>());  // the copy of a picture that is not a GIF
            ShowError(L10n.L("Choose a GIF."));
            return;
        }
        ShowError(null);
        Change(badge => BadgeEdits.ApplyAnimation(badge, path));
    }

    private void PickPicture()
    {
        if (PickFile(L10n.L("Picture files") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp") is { } file)
        {
            UsePicture(file);
        }
    }

    private void PickGif()
    {
        if (PickFile(L10n.L("GIF files") + "|*.gif") is { } file)
        {
            UseAnimation(file);
        }
    }

    private static string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// Shows the controls of one kind, and the ink row only where the kind has ink (a symbol or letters).
    private void ShowKind(Kind kind)
    {
        SymbolPanel.Visibility = VisibleIf(kind == Kind.Symbol);
        TextPanel.Visibility = VisibleIf(kind == Kind.Text);
        PicturePanel.Visibility = VisibleIf(kind == Kind.Picture);
        AnimationPanel.Visibility = VisibleIf(kind == Kind.Animation);
        InkRow.Visibility = VisibleIf(kind is Kind.Symbol or Kind.Text);
        InkLabel.Text = L10n.L(kind == Kind.Text ? "Text color" : "Symbol color");
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = VisibleIf(message is not null);
    }

    /// The kind a badge has: an animation, else a picture, else letters, else a symbol (the Mac's NotchBadge order).
    private static Kind KindOf(JsonObject badge)
    {
        if (BadgeEdits.IsAnimated(badge))
        {
            return Kind.Animation;
        }
        if (BadgeEdits.ImageOf(badge) is not null)
        {
            return Kind.Picture;
        }
        return string.IsNullOrEmpty(StringOf(badge, "text")) ? Kind.Symbol : Kind.Text;
    }

    private static string ValueOf(Kind kind) => kind switch
    {
        Kind.Text => "text",
        Kind.Picture => "picture",
        Kind.Animation => "animation",
        _ => "symbol",
    };

    private static Kind? KindOfValue(string value) => value switch
    {
        "symbol" => Kind.Symbol,
        "text" => Kind.Text,
        "picture" => Kind.Picture,
        "animation" => Kind.Animation,
        _ => null,
    };

    private static Visibility VisibleIf(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

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
