using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel.Panes;

/// The Icon Library pane (BadgesPane.swift IconStore): the animated icons by category. A click adds the icon to the notch
/// (the left side, or the right when the left is full), or sets it on the icon selected on the Icons & lock pane. Each
/// icon animates only while it is on screen: it is shown when its tile is inside the Control Panel's scrolled area.
public partial class IconLibraryPane : SettingsPane
{
    private static readonly Color TileFill = Color.FromRgb(0x14, 0x14, 0x16);

    private readonly List<LibraryTile> _tiles = new();
    private readonly List<(string Category, TextBlock Header)> _headers = new();
    private ScrollViewer? _scroller;
    private Window? _window;
    private string _message = string.Empty;

    private sealed record LibraryTile(SpriteEntry Entry, Button Button, SpriteView Sprite, Ellipse InUse, TextBlock Name);

    public IconLibraryPane(SettingsStore store) : base(store)
    {
        InitializeComponent();
        BuildGroups();

        ClearButton.Click += (_, _) =>
        {
            IconSelection.Clear();
            _message = string.Empty;
            ApplyStored();
        };
        SyncButton.Click += (_, _) => MotionSync.Now();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    protected override void Apply()
    {
        var badges = Store.Badges.OfType<JsonObject>().ToList();
        var selection = IconSelection.BadgeId;
        var selected = selection is not null && badges.Any(b => BadgeEdits.IdOf(b) == selection);
        if (selection is not null && !selected)
        {
            IconSelection.Clear();
        }
        var inUse = badges.Select(b => StringOf(b, "sprite")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var accent = new SolidColorBrush(BadgeGlyph.AccentOf(Store));
        accent.Freeze();

        CountNote.Text = L10n.L("{0} animated icons · Free", SpriteLibrary.All.Count);
        foreach (var (category, header) in _headers)
        {
            header.Text = L10n.L(category);
        }
        foreach (var tile in _tiles)
        {
            tile.Name.Text = tile.Entry.Name;
            tile.InUse.Visibility = inUse.Contains(tile.Entry.Id) ? Visibility.Visible : Visibility.Collapsed;
            tile.InUse.Fill = accent;
        }

        StatusText.Text = _message.Length > 0
            ? _message
            : L10n.L(selected ? "Click an animation to use it for the selected icon." : "Click an animation to add it to the notch.");
        ClearButton.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// One group per category, in the Mac's order. A category with no sprite in the catalog is left out.
    private void BuildGroups()
    {
        foreach (var category in SpriteLibrary.Categories)
        {
            var entries = SpriteLibrary.In(category);
            var tiles = entries.Select(Tile).OfType<LibraryTile>().ToList();
            if (tiles.Count == 0)
            {
                continue;
            }

            var header = new TextBlock { Style = (Style)FindResource("SectionHeader") };
            _headers.Add((category, header));
            Groups.Children.Add(header);

            var wrap = new WrapPanel();
            foreach (var tile in tiles)
            {
                wrap.Children.Add(tile.Button);
                _tiles.Add(tile);
            }
            Groups.Children.Add(wrap);
        }
    }

    /// A tile: the animation at 36 DIP, a dot when it is in use, and its name. Null when the sprite's picture is missing.
    private LibraryTile? Tile(SpriteEntry entry)
    {
        var sheet = SpriteCache.Get(entry.Id);
        if (sheet is null)
        {
            return null;
        }

        var sprite = new SpriteView(sheet, 36)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var inUse = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 6, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        var art = new Grid { Width = 92, Height = 56 };
        art.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(TileFill) });
        art.Children.Add(sprite);
        art.Children.Add(inUse);

        var name = new TextBlock
        {
            Width = 92,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            Text = entry.Name,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var button = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Content = new StackPanel { Children = { art, name } },
            Margin = new Thickness(0, 0, 8, 10),
            ToolTip = entry.Name,
        };
        var tile = new LibraryTile(entry, button, sprite, inUse, name);
        button.Click += (_, _) => OnTileClick(entry);
        return tile;
    }

    /// With an icon selected, the animation is set on it. Else it is added to the left side, or the right when the left is full.
    private void OnTileClick(SpriteEntry entry)
    {
        var badges = Store.Badges;
        if (IconSelection.BadgeId is { } id)
        {
            if (badges.OfType<JsonObject>().Any(b => BadgeEdits.IdOf(b) == id))
            {
                _message = L10n.L("Set {0} for the selected icon.", entry.Name);
                Write("badges", BadgeEdits.Update(badges, id, badge => BadgeEdits.ApplySprite(badge, entry.Id)));
                return;
            }
            IconSelection.Clear();
        }

        var right = BadgeEdits.CountOnSide(badges, false) >= BadgeEdits.PerSide;
        if (BadgeEdits.CountOnSide(badges, right) >= BadgeEdits.PerSide)
        {
            _message = L10n.L("Both sides are full. Remove an icon first.");
            ApplyStored();
            return;
        }
        _message = right
            ? L10n.L("Added {0} on the right.", entry.Name)
            : L10n.L("Added {0} on the left.", entry.Name);
        Write("badges", BadgeEdits.Add(badges, right, entry.Id));
    }

    /// Shows the tiles in view: the others are hidden, which stops their animation (SpriteClock steps visible sprites only).
    private void UpdateVisibleTiles()
    {
        if (_scroller is null || !IsLoaded)
        {
            return;
        }
        // A minimized or hidden Control Panel shows nothing, so every tile stops.
        var shown = _window is { IsVisible: true, WindowState: not WindowState.Minimized };
        var viewport = new Rect(0, 0, _scroller.ViewportWidth, _scroller.ViewportHeight);
        foreach (var tile in _tiles)
        {
            var inView = false;
            if (shown && tile.Button.IsLoaded)
            {
                try
                {
                    var bounds = tile.Button.TransformToAncestor(_scroller)
                        .TransformBounds(new Rect(new Point(0, 0), tile.Button.RenderSize));
                    inView = viewport.IntersectsWith(bounds);
                }
                catch (InvalidOperationException)
                {
                    // The tile left the visual tree between the check and the transform: it is treated as out of view.
                }
            }
            tile.Sprite.Visibility = inView ? Visibility.Visible : Visibility.Hidden;
        }
    }

    /// Finds the scroller that holds the pane (the Control Panel's), and follows its scrolling. The pane is shown.
    private void Attach()
    {
        _scroller = Ancestor<ScrollViewer>(this);
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrolled;
        }
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
            _window.IsVisibleChanged += OnWindowVisibleChanged;
        }
        // After the layout pass, so the tiles have their places.
        Dispatcher.BeginInvoke(new Action(UpdateVisibleTiles), DispatcherPriority.Loaded);
    }

    /// The pane is left: the scroller is no longer followed, and the message is forgotten for the next visit.
    private void Detach()
    {
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrolled;
            _scroller = null;
        }
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.IsVisibleChanged -= OnWindowVisibleChanged;
            _window = null;
        }
        _message = string.Empty;
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e) => UpdateVisibleTiles();

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateVisibleTiles();

    private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateVisibleTiles();

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T found)
            {
                return found;
            }
        }
        return null;
    }

    private static string? StringOf(JsonObject badge, string key) =>
        badge[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
