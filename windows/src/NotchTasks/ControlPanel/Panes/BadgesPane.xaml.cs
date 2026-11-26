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
    private readonly AppState _state;

    public BadgesPane(SettingsStore store, AppState state) : base(store)
    {
        _state = state;
        InitializeComponent();

        // The editor body is shared with the tool dialog (BadgeEditorBody): its edits come back here to be saved.
        Body.Edit += Edit;
        Body.LibraryRequested += (_, _) => ControlPanelWindow.Show(_state, PaneId.Store);

        LibraryButton.Click += (_, _) => ControlPanelWindow.Show(_state, PaneId.Store);
        SyncButton.Click += (_, _) => MotionSync.Now();
        MoveEarlierButton.Click += (_, _) => Move(-1);
        MoveLaterButton.Click += (_, _) => Move(1);
        RemoveButton.Click += (_, _) => Remove();
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
        var glyph = BadgeGlyph.Create(badge, 22, accent, Animate);
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

        var preview = BadgeGlyph.Create(badge, 40, accent, Animate);
        preview.HorizontalAlignment = HorizontalAlignment.Center;
        preview.VerticalAlignment = VerticalAlignment.Center;
        EditorPreview.Child = preview;
        EditorTitle.Text = L10n.L("Pinned icon");
        EditorSide.Text = right ? L10n.L("Right of the notch") : L10n.L("Left of the notch");
        MoveEarlierButton.IsEnabled = index > 0;
        MoveLaterButton.IsEnabled = index < side.Count - 1;

        Body.Show(badge, accent);
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
        var picture = ImageOf(id);
        Write("badges", BadgeEdits.Remove(Store.Badges, id));
        BadgeImages.Discard(picture, Store);  // a picture copy no icon uses any more goes
    }

    /// Changes the selected icon with `change`, and saves the badges. A picture the icon no longer shows is deleted, unless another
    /// icon or a tool still uses it.
    private void Edit(Action<JsonObject> change)
    {
        if (Applying || IconSelection.BadgeId is not { } id)
        {
            return;
        }
        var before = ImageOf(id);
        Write("badges", BadgeEdits.Update(Store.Badges, id, change));
        if (ImageOf(id) != before)
        {
            BadgeImages.Discard(before, Store);
        }
    }

    /// The picture of the icon with this id, or null when it has none.
    private string? ImageOf(string id) =>
        Store.Badges.OfType<JsonObject>().FirstOrDefault(b => BadgeEdits.IdOf(b) == id) is { } badge ? BadgeEdits.ImageOf(badge) : null;

    /// Whether badges animate: Windows animations are on and the motion is not "Minimal" (reduced). A GIF or a sprite stands still otherwise.
    private bool Animate => SystemParameters.ClientAreaAnimation && Store.Motion != "reduced";

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
