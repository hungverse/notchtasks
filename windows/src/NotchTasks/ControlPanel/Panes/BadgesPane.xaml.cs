using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel.Panes;

/// The Icons & lock pane (BadgesPane.swift, the main screen): the icons on the two sides of the closed notch, drawn as the
/// pill draws them, and the editor of the icon selected. A click on an icon selects it; + adds one; an animated icon comes
/// from the Icon Library. The lock screen has no notch on Windows, so it is not here.
public partial class BadgesPane : SettingsPane
{
    private const string SegoeFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly AppState _state;
    private readonly IReadOnlyList<Choice> _shapes = [Choice.Key("circle", "Circle"), Choice.Key("rounded", "Square")];

    public BadgesPane(SettingsStore store, AppState state) : base(store)
    {
        _state = state;
        InitializeComponent();

        ShapeBox.ItemsSource = _shapes;
        ShapeBox.SelectionChanged += (_, _) =>
        {
            if (!Applying && ShapeBox.SelectedItem is Choice choice)
            {
                Edit(badge => badge["shape"] = JsonValue.Create(choice.Value));
            }
        };
        BorderBox.Click += (_, _) => Edit(badge => badge["border"] = JsonValue.Create(BorderBox.IsChecked == true));
        NoFillBox.Click += (_, _) => Edit(badge => badge["plain"] = NoFillBox.IsChecked == true ? JsonValue.Create(true) : null);
        FillColor.ValueChanged += (_, _) => Edit(badge =>
        {
            // Picking a colour turns the fill back on, as the Mac does.
            badge["color"] = JsonValue.Create(FillColor.Hex);
            badge["plain"] = null;
        });
        SymbolColor.ValueChanged += (_, _) => Edit(badge => badge["fg"] = JsonValue.Create(SymbolColor.Hex));

        LibraryButton.Click += (_, _) => ControlPanelWindow.Show(_state, PaneId.Store);
        LibraryEditButton.Click += (_, _) => ControlPanelWindow.Show(_state, PaneId.Store);
        SyncButton.Click += (_, _) => MotionSync.Now();
        MoveEarlierButton.Click += (_, _) => Move(-1);
        MoveLaterButton.Click += (_, _) => Move(1);
        RemoveButton.Click += (_, _) => Remove();

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
                    Text = char.ConvertFromUtf32(Convert.ToInt32(code, 16)),
                    FontFamily = new FontFamily(SegoeFont),
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
            button.Click += (_, _) => Edit(badge => BadgeEdits.ApplySymbol(badge, BadgeEdits.SymbolFor(code)));
            SymbolGrid.Children.Add(button);
        }
    }

    protected override void Apply()
    {
        var badges = Store.Badges;
        RenderBoard(badges);
        SyncButton.Visibility = badges.Any(node => node is JsonObject badge && badge["sprite"] is JsonValue)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RenderEditor(badges);
    }

    /// The left and right icons, with the + of a side that has room: the + sits outside the icons on each side.
    private void RenderBoard(JsonArray badges)
    {
        LeftWing.Children.Clear();
        RightWing.Children.Clear();
        var accent = BadgeGlyph.AccentOf(Store);
        foreach (var right in new[] { false, true })
        {
            var panel = right ? RightWing : LeftWing;
            var side = badges.OfType<JsonObject>().Where(badge => BadgeEdits.RightOf(badge) == right).ToList();
            if (!right && side.Count < BadgeEdits.PerSide)
            {
                panel.Children.Add(AddSlot(right));
            }
            foreach (var badge in side)
            {
                panel.Children.Add(Tile(badge, accent));
            }
            if (right && side.Count < BadgeEdits.PerSide)
            {
                panel.Children.Add(AddSlot(right));
            }
        }
        HintText.Text = L10n.L("Click + to add an icon, click one to edit it. Up to {0} per side.", BadgeEdits.PerSide);
    }

    private Button Tile(JsonObject badge, Color accent)
    {
        var id = BadgeEdits.IdOf(badge) ?? string.Empty;
        var selected = IconSelection.BadgeId == id;
        var glyph = BadgeGlyph.Create(badge, 22, accent);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var face = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = Solid(Color.FromRgb(0x2C, 0x2C, 0x2E)),
            BorderThickness = new Thickness(selected ? 2 : 0),
            BorderBrush = Solid(accent),
            Child = glyph,
        };
        var button = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Content = face,
            Margin = new Thickness(0, 0, 6, 0),
        };
        button.Click += (_, _) => Toggle(id);
        return button;
    }

    private Button AddSlot(bool right)
    {
        var face = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Solid(Color.FromRgb(0x5A, 0x5A, 0x5C)),
            Child = new TextBlock
            {
                Text = "+",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Solid(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var button = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Content = face,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = right ? L10n.L("Add on the right") : L10n.L("Add on the left"),
        };
        button.Click += (_, _) => AddBadge(right);
        return button;
    }

    /// The editor of the selected icon. It is hidden when none is selected (or the selected one is gone).
    private void RenderEditor(JsonArray badges)
    {
        var id = IconSelection.BadgeId;
        var all = badges.OfType<JsonObject>().ToList();
        var badge = id is null ? null : all.FirstOrDefault(b => BadgeEdits.IdOf(b) == id);
        if (badge is null)
        {
            IconSelection.Clear();
            Editor.Visibility = Visibility.Collapsed;
            return;
        }
        Editor.Visibility = Visibility.Visible;

        var accent = BadgeGlyph.AccentOf(Store);
        var right = BadgeEdits.RightOf(badge);
        var side = all.Where(b => BadgeEdits.RightOf(b) == right).ToList();
        var index = side.FindIndex(b => BadgeEdits.IdOf(b) == id);

        var preview = BadgeGlyph.Create(badge, 40, accent);
        preview.HorizontalAlignment = HorizontalAlignment.Center;
        preview.VerticalAlignment = VerticalAlignment.Center;
        EditorPreview.Child = preview;
        EditorTitle.Text = L10n.L("Pinned icon");
        EditorSide.Text = right ? L10n.L("Right of the notch") : L10n.L("Left of the notch");
        MoveEarlierButton.IsEnabled = index > 0;
        MoveLaterButton.IsEnabled = index < side.Count - 1;

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

    /// Selects the icon, or clears the selection when it is the selected one.
    private void Toggle(string id)
    {
        if (IconSelection.BadgeId == id)
        {
            IconSelection.Clear();
        }
        else
        {
            IconSelection.Select(id);
        }
        ApplyStored();
    }

    /// Adds an empty icon to a side with room, and selects it for editing.
    private void AddBadge(bool right)
    {
        var badges = Store.Badges;
        if (BadgeEdits.CountOnSide(badges, right) >= BadgeEdits.PerSide)
        {
            return;
        }
        var before = badges.OfType<JsonObject>().Select(b => BadgeEdits.IdOf(b)).ToHashSet(StringComparer.Ordinal);
        Write("badges", BadgeEdits.Add(badges, right, sprite: null));
        var added = Store.Badges.OfType<JsonObject>().Select(b => BadgeEdits.IdOf(b))
            .FirstOrDefault(id => id is not null && !before.Contains(id));
        if (added is not null)
        {
            IconSelection.Select(added);
        }
        ApplyStored();
    }

    /// Moves the selected icon one place along its side.
    private void Move(int step)
    {
        if (IconSelection.BadgeId is not { } id)
        {
            return;
        }
        var badges = Store.Badges;
        var all = badges.OfType<JsonObject>().ToList();
        var badge = all.FirstOrDefault(b => BadgeEdits.IdOf(b) == id);
        if (badge is null)
        {
            return;
        }
        var right = BadgeEdits.RightOf(badge);
        var index = all.Where(b => BadgeEdits.RightOf(b) == right).ToList().FindIndex(b => BadgeEdits.IdOf(b) == id);
        Write("badges", BadgeEdits.Move(badges, id, index + step));
    }

    /// Removes the selected icon from the notch.
    private void Remove()
    {
        if (IconSelection.BadgeId is not { } id)
        {
            return;
        }
        IconSelection.Clear();
        Write("badges", BadgeEdits.Remove(Store.Badges, id));
    }

    /// Changes the selected icon with `change`, and saves the badges.
    private void Edit(Action<JsonObject> change)
    {
        if (Applying || IconSelection.BadgeId is not { } id)
        {
            return;
        }
        Write("badges", BadgeEdits.Update(Store.Badges, id, change));
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
