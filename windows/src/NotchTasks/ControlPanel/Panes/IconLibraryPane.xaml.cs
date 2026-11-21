using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel.Panes;

/// The Icon Library pane (BadgesPane.swift IconStore): the animated icons by category. A click sets the icon selected on the Icons
/// & lock pane; else a menu at the tile puts the animation on a fixed spot of the notch, a watched app, or a build tool (useMenu).
/// Each icon animates only while it is on screen: it is shown when its tile is inside the Control Panel's scrolled area.
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
        button.Click += (_, _) => OnTileClick(entry, button);
        return tile;
    }

    /// With an icon selected on the Icons & lock pane, the animation is set on it. Else a menu at the tile says where it goes.
    private void OnTileClick(SpriteEntry entry, Button tile)
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

        var menu = UseMenu(entry);
        menu.PlacementTarget = tile;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// Where an animation can go (the Mac's useMenu): a fixed spot on the notch (a full side is disabled), a watched app's icon, or a
    /// build tool's icon. The apps and tools are listed only when there are some.
    internal ContextMenu UseMenu(SpriteEntry entry)
    {
        var badges = Store.Badges;
        var menu = new ContextMenu();
        menu.Items.Add(Heading(L10n.L("Pinned icon on the notch")));
        menu.Items.Add(UseEntry(L10n.L("Add on the left"), BadgeEdits.CountOnSide(badges, false) < BadgeEdits.PerSide,
            () => AddToSide(entry, right: false)));
        menu.Items.Add(UseEntry(L10n.L("Add on the right"), BadgeEdits.CountOnSide(badges, true) < BadgeEdits.PerSide,
            () => AddToSide(entry, right: true)));

        if (Store.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Heading(L10n.L("Set for a watched app")));
            foreach (var item in Store.Items)
            {
                var id = item.Id;
                menu.Items.Add(UseEntry(item.Name, true, () => SetItemIcon(id, entry)));
            }
        }

        if (Store.SeenTools.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Heading(L10n.L("Set for a build tool")));
            foreach (var tool in Store.SeenTools)
            {
                menu.Items.Add(UseEntry(tool, true, () => SetToolIcon(tool, entry)));
            }
        }
        return menu;
    }

    /// A disabled menu header. A TextBlock, not a string: a string header reads an "_" in a name as an access key.
    private static MenuItem Heading(string text) => new() { Header = new TextBlock { Text = text }, IsEnabled = false };

    private static MenuItem UseEntry(string text, bool enabled, Action choose)
    {
        var entry = new MenuItem { Header = new TextBlock { Text = text }, IsEnabled = enabled };
        entry.Click += (_, _) => choose();
        return entry;
    }

    /// Adds the animation to a fixed spot on the notch, unless that side is full.
    private void AddToSide(SpriteEntry entry, bool right)
    {
        var badges = Store.Badges;
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

    /// Makes the animation the icon of a watched app: its Icon is the plain badge of the animation (the Mac's item.icon).
    private void SetItemIcon(string id, SpriteEntry entry)
    {
        var item = Store.Items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            return;
        }
        var refused = Store.SetItems(Store.Items.Select(i => i.Id == id ? i with { Icon = SpriteBadge(entry).ToJsonString() } : i).ToList());
        _message = refused ?? L10n.L("Set {0} for {1}.", entry.Name, item.Name);
        ApplyStored();
    }

    /// Makes the animation the icon of a build tool (the Mac's toolIcons).
    private void SetToolIcon(string tool, SpriteEntry entry)
    {
        var refused = Store.SetToolIcon(tool, SpriteBadge(entry));
        _message = refused ?? L10n.L("Set {0} for {1}.", entry.Name, tool);
        ApplyStored();
    }

    /// The plain badge of an animation: what the Mac's Badge(plain: true, sprite:) is.
    private static JsonObject SpriteBadge(SpriteEntry entry) => new()
    {
        ["plain"] = JsonValue.Create(true),
        ["sprite"] = JsonValue.Create(entry.Id),
    };

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
