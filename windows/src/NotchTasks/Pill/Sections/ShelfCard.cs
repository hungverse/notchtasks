using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using NotchTasks.Core;
using NotchTasks.Core.Shelf;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.Pill.Sections;

/// The File shelf section of the open pill (Mac ShelfCard): the files and folders dropped on the pill, as a row of tiles with
/// their thumbnails. A click selects a tile, a double-click opens it, a right-click opens its menu, and a drag takes the selected
/// files (or the tile's own file) out to another app. The header holds the selection's count and the actions for the selection
/// (or for all the shelf when nothing is selected). The card is one element kept for the pill's life: Update refreshes it, and
/// returns false (and shows nothing) while the shelf is off or empty and no file is dragged over the pill.
public sealed class ShelfCard : Border
{
    private const string TrayGlyph = "";    // folder: the shelf's glyph (ModuleCatalog)
    private const string OpenGlyph = "";    // open in a new window
    private const string RevealGlyph = "";  // folder open: show the file in its folder
    private const string ShareGlyph = "";   // share
    private const string TrashGlyph = "";   // delete: remove all
    private const string MinusGlyph = "";   // remove the selection
    private const string AddGlyph = "";     // add files
    private const string HintText = "Click to select · double-click to open · drag out to drop into another app";
    private const string DropHere = "Drop here";
    private const string EmptyHint = "Drag files onto the notch, or click + to choose";
    private const double DragThreshold = 3;
    private const double DashedHeight = 44;

    private static readonly Color FillColor = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Color TargetedFill = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color GreyColor = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Brush FillBrush = Frozen(new SolidColorBrush(FillColor));
    private static readonly Brush TargetedBrush = Frozen(new SolidColorBrush(TargetedFill));
    private static readonly Brush MenuBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private static readonly Brush MenuBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static readonly FontFamily TextFamily = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Lazy<ResourceDictionary> PillStyles = new(() => new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/NotchTasks;component/Pill/PillStyles.xaml", UriKind.Absolute),
    });
    private static readonly Lazy<ResourceDictionary> AskStyles = new(() => new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/NotchTasks;component/Pill/AskStyles.xaml", UriKind.Absolute),
    });

    private readonly Dictionary<string, ShelfTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    // Whether each shelf path exists, as the last background check found it. A path not checked yet shows as present.
    private readonly Dictionary<string, bool> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly FluentGlyph _titleGlyph;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly Button _selectAll;
    private readonly Button _open;
    private readonly Button _reveal;
    private readonly Button _share;
    private readonly Button _remove;
    private readonly Grid _emptyBox;
    private readonly TextBlock _emptyText;
    private readonly RowScroller _tileScroll;
    private readonly StackPanel _tileRow;
    private readonly TextBlock _hint;
    private AppState? _state;
    private IReadOnlyList<string> _items = Array.Empty<string>();
    private IReadOnlyList<string>? _checkedFor;  // the list the existence check last ran for (by reference: a change makes a new list)
    private string _removeGlyph = TrashGlyph;    // the glyph the remove button shows now, so it is made again only on a change
    private Color _accent = Color.FromRgb(0x32, 0xD2, 0xFF);
    private bool _targeted;
    private bool _dragging;  // a drag out is in progress: the pill stays open
    private bool _menuOpen;  // a tile's menu is open: the pill stays open
    // The press of each tile: where it went down, and whether it was dragged, or opened by a double-click.
    private readonly Dictionary<ShelfTile, Point> _downAt = new();
    private readonly HashSet<ShelfTile> _pressed = new();
    private readonly HashSet<ShelfTile> _dragged = new();
    private readonly HashSet<ShelfTile> _opened = new();

    public ShelfCard()
    {
        Focusable = false;
        Background = FillBrush;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(10);

        _titleGlyph = new FluentGlyph { Code = TrayGlyph, Size = 12, Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0) };
        _title = Text(string.Empty, 12, TextFamily, Brushes.White, FontWeights.SemiBold);
        _count = Text(string.Empty, 11, TextFamily, Brushes.Gray);
        _count.Margin = new Thickness(6, 0, 0, 0);
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        titles.Children.Add(_titleGlyph);
        titles.Children.Add(_title);
        titles.Children.Add(_count);

        _selectAll = Wire(new TextBlock { Text = string.Empty, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
            ChoiceStyle(), OnSelectAll);
        _open = Wire(Glyph(OpenGlyph), FooterStyle(), OnOpen);
        _reveal = Wire(Glyph(RevealGlyph), FooterStyle(), OnReveal);
        _share = Wire(Glyph(ShareGlyph), FooterStyle(), OnShare);
        _remove = Wire(Glyph(TrashGlyph), FooterStyle(), OnRemove);
        var add = Wire(Glyph(AddGlyph), FooterStyle(), (_, _) => _state?.PickShelfFiles());
        add.ToolTip = L10n.L("Add files…");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Focusable = false };
        foreach (var button in new[] { _selectAll, _open, _reveal, _share, _remove, add })
        {
            button.Margin = new Thickness(6, 0, 0, 0);
            actions.Children.Add(button);
        }

        var header = new Grid { Focusable = false };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(actions, 1);
        header.Children.Add(titles);
        header.Children.Add(actions);

        _emptyText = Text(string.Empty, 11, TextFamily, Brushes.Gray);
        _emptyText.TextAlignment = TextAlignment.Center;
        _emptyText.VerticalAlignment = VerticalAlignment.Center;
        _emptyBox = new Grid { Height = DashedHeight, Margin = new Thickness(0, 8, 0, 0), Focusable = false };
        _emptyBox.Children.Add(new Rectangle
        {
            Stroke = Frozen(new SolidColorBrush(GreyColor)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 }),
            RadiusX = 10,
            RadiusY = 10,
        });
        _emptyBox.Children.Add(_emptyText);

        _tileRow = new StackPanel { Orientation = Orientation.Horizontal, Focusable = false };
        _tileScroll = new RowScroller
        {
            Margin = new Thickness(0, 8, 0, 0),
            Content = _tileRow,
        };
        _hint = Text(HintText, 9, TextFamily, Brushes.Gray);
        _hint.Margin = new Thickness(0, 8, 0, 0);

        var stack = new StackPanel { Focusable = false };
        stack.Children.Add(header);
        stack.Children.Add(_emptyBox);
        stack.Children.Add(_tileScroll);
        stack.Children.Add(_hint);
        Child = stack;
    }

    /// The header's title: "File shelf", or "{0}/{1} selected" while some tiles are selected.
    public string HeaderTitle => _title.Text;

    /// The header's count beside the title: the number of files, or empty while some are selected.
    public string HeaderCount => _count.Text;

    /// The hint under the tiles.
    public string Hint => _hint.Text;

    /// The empty box's prompt: "Drop here" while a file is dragged over the pill.
    public string EmptyPrompt => _emptyText.Text;

    /// Whether the empty box shows (the shelf has no file).
    public bool EmptyVisible => _emptyBox.Visibility == Visibility.Visible;

    /// The tiles now shown, in the shelf's order.
    public IReadOnlyList<ShelfTile> Tiles => _tileRow.Children.OfType<ShelfTile>().ToList();

    /// The selected paths (the tiles the user picked, in no particular order).
    public IReadOnlyCollection<string> Selected => _selected;

    /// True while a drag out or a tile's menu is open: the pill stays open then, and no drop lands on the card's own drag.
    public bool Busy => _dragging || _menuOpen;

    /// Whether a drag that carries files may drop on the pill: the shelf module is on, the drag has files, and the card is not dragging
    /// files out (its own drag never drops on the pill).
    public static bool AcceptsDrop(bool shelfOn, bool hasFiles, bool busy) => shelfOn && hasFiles && !busy;

    /// Whether the drop prompt clears on a cursor poll: it is up, the pointer is off the pill, and no mouse button is down. A DragLeave
    /// that never came is then not left to keep the pill open.
    public static bool ClearsTarget(bool targeted, bool pointerOverPill, bool buttonDown) => targeted && !pointerOverPill && !buttonDown;

    /// Brings the card in line with the app state and returns true, or returns false (and shows nothing) while the shelf module is
    /// off, or the shelf is empty and no file is dragged over the pill. Selected paths that left the shelf are dropped. The files'
    /// existence is checked on the background when the shelf changes; the tiles show it when the check lands.
    public bool Update(AppState state, Color accent)
    {
        _state = state;
        _accent = accent;
        _targeted = state.ShelfTargeted;
        var shown = state.Snapshot.ActiveModules.Contains("shelf") && (state.Shelf.Items.Count > 0 || _targeted);
        _items = shown ? state.Shelf.Items : Array.Empty<string>();
        if (shown && _items.Count > 0 && !ReferenceEquals(_checkedFor, _items))
        {
            CheckExistenceAsync(_items);
        }
        Render();
        return shown;
    }

    /// Checks the files again (the pill calls it when it opens: a file may have gone while it was closed).
    public void InvalidateExistence()
    {
        _checkedFor = null;
        Refresh();
    }

    /// Selects a tile, or deselects it when it is selected (a click on the tile).
    public void Toggle(string path)
    {
        if (!_selected.Remove(path))
        {
            _selected.Add(path);
        }
        Render();
    }

    /// Selects every tile, or clears the selection when all are selected (the header's pill).
    public void SelectAll()
    {
        if (_selected.Count == _items.Count)
        {
            _selected.Clear();
        }
        else
        {
            _selected.Clear();
            _selected.UnionWith(_items);
        }
        Render();
    }

    /// Removes the selected files from the shelf, or every file when none is selected. The originals stay. A save that fails leaves the
    /// shelf and the selection as they were.
    public void RemoveTargets()
    {
        var targets = Targets();
        if (!ShelfSave.TrySave(() => _state?.Shelf.Remove(targets)))
        {
            return;
        }
        _selected.ExceptWith(targets);
        Refresh();
    }

    /// Reads the shelf again and shows it (after a removal, the card's own list of items is out of date).
    private void Refresh()
    {
        if (_state is { } state)
        {
            Update(state, _accent);
        }
        else
        {
            Render();
        }
    }

    /// The files a drag from a tile carries: the selected existing files when the tile is selected, else the tile's own file when it
    /// exists. A missing file is skipped. The result keeps the shelf's order.
    public static IReadOnlyList<string> DragFiles(string tile, IReadOnlyList<string> items, IReadOnlySet<string> selected,
        Func<string, bool> exists)
    {
        var wanted = selected.Contains(tile) ? items.Where(selected.Contains) : new[] { tile };
        return wanted.Where(exists).ToList();
    }

    /// The files the header and the tiles' actions act on: the selection when there is one, else all of the shelf.
    private List<string> Targets() => _selected.Count > 0 ? _items.Where(_selected.Contains).ToList() : _items.ToList();

    /// The existence of the shelf's files, checked on a thread-pool thread. The result is applied on the UI thread, and only when the
    /// list is still the one checked (a newer list, or an invalidation, makes this result stale).
    private async void CheckExistenceAsync(IReadOnlyList<string> items)
    {
        _checkedFor = items;
        var paths = items.ToArray();
        var found = await Task.Run(() => paths.Select(p => File.Exists(p) || Directory.Exists(p)).ToArray());
        if (!ReferenceEquals(_checkedFor, items))
        {
            return;
        }
        _present.Clear();
        for (var i = 0; i < paths.Length; i++)
        {
            _present[paths[i]] = found[i];
        }
        Render();
    }

    private bool Present(string path) => !_present.TryGetValue(path, out var present) || present;

    /// Shows the items, the selection and the header from the current state. Each element is changed only when its value changed, so a
    /// publish that changes nothing makes no new brush, glyph or tooltip.
    private void Render()
    {
        var picked = _items.Where(_selected.Contains).ToList();
        var empty = _items.Count == 0;
        var noSelection = picked.Count == 0;
        if (_selected.Count != picked.Count)
        {
            _selected.IntersectWith(_items);
        }

        _titleGlyph.Visibility = noSelection ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = noSelection ? L10n.L("File shelf") : L10n.L("{0}/{1} selected", picked.Count, _items.Count);
        _count.Text = noSelection && !empty ? _items.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;

        var hasItems = !empty;
        _selectAll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        var selectAllText = picked.Count == _items.Count && hasItems ? L10n.L("Deselect") : L10n.L("Select all");
        var selectAllLabel = (TextBlock)_selectAll.Content;
        if (selectAllLabel.Text != selectAllText)
        {
            selectAllLabel.Text = selectAllText;
        }
        _open.Visibility = picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _reveal.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        _share.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        SetTip(_share, noSelection ? L10n.L("Share all") : L10n.L("Share"));
        _remove.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        var removeGlyph = noSelection ? TrashGlyph : MinusGlyph;
        if (removeGlyph != _removeGlyph)
        {
            _remove.Content = Glyph(removeGlyph);
            _removeGlyph = removeGlyph;
        }
        SetTip(_remove, noSelection ? L10n.L("Remove all from the shelf (originals stay)") : L10n.L("Remove from the shelf (original stays)"));

        _emptyBox.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _emptyText.Text = _targeted ? L10n.L(DropHere) : L10n.L(EmptyHint);
        _emptyText.Foreground = _targeted ? Brushes.White : Brushes.Gray;
        _tileScroll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = noSelection ? L10n.L(HintText) : L10n.L("Drag one selected file to bring all {0} files", picked.Count);

        SyncTiles();
        Background = _targeted ? TargetedBrush : FillBrush;
    }

    /// Sets a button's tooltip only when its text changed.
    private static void SetTip(Button button, string text)
    {
        if (button.ToolTip as string != text)
        {
            button.ToolTip = text;
        }
    }

    /// The tiles, one per item, in the shelf's order. A tile is made once per path; the row is set again only when its order changes.
    private void SyncTiles()
    {
        var wanted = new List<ShelfTile>();
        foreach (var path in _items)
        {
            if (!_tiles.TryGetValue(path, out var tile))
            {
                tile = MakeTile(path);
                _tiles[path] = tile;
            }
            tile.Show(Present(path), _selected.Contains(path), _accent);
            wanted.Add(tile);
        }
        foreach (var path in _tiles.Keys.Where(p => !_items.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _tiles.Remove(path);
        }
        if (_tileRow.Children.Count != wanted.Count || !_tileRow.Children.OfType<ShelfTile>().SequenceEqual(wanted))
        {
            _tileRow.Children.Clear();
            foreach (var tile in wanted)
            {
                _tileRow.Children.Add(tile);
            }
        }
    }

    /// One tile: its thumbnail (the large icon at once, the WinRT thumbnail when it arrives), its name, and its input.
    private ShelfTile MakeTile(string path)
    {
        var tile = new ShelfTile(path);
        tile.Thumb.Source = FileThumbs.Get(path, image => tile.SetThumb(image));
        tile.MouseLeftButtonDown += (_, e) => OnTileDown(tile, e);
        tile.MouseMove += (_, e) => OnTileMove(tile, e);
        tile.MouseLeftButtonUp += (_, e) => OnTileUp(tile, e);
        var menu = new ContextMenu { Background = MenuBackground, Foreground = Brushes.White, BorderBrush = MenuBorder };
        menu.Items.Add(Entry(L10n.L("Open"), () => OpenPath(path)));
        menu.Items.Add(Entry(L10n.L("Show in folder"), () => RevealPaths(new[] { path })));
        menu.Items.Add(Entry(L10n.L("Share"), () => ShareFiles(new[] { path })));
        menu.Items.Add(Entry(L10n.L("Remove from shelf"), () =>
        {
            if (ShelfSave.TrySave(() => _state?.Shelf.Remove(new[] { path })))
            {
                _selected.Remove(path);
                Refresh();
            }
        }));
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        tile.ContextMenu = menu;
        return tile;
    }

    private void OnTileDown(ShelfTile tile, MouseButtonEventArgs e)
    {
        _pressed.Add(tile);
        _downAt[tile] = e.GetPosition(null);
        tile.CaptureMouse();
        if (e.ClickCount >= 2)
        {
            _opened.Add(tile);
            OpenPath(tile.FilePath);
        }
        e.Handled = true;
    }

    /// A press that moved more than DragThreshold starts a drag out: the files go to the drop target, and the pill stays open until it returns.
    private void OnTileMove(ShelfTile tile, MouseEventArgs e)
    {
        if (!_pressed.Contains(tile) || _dragged.Contains(tile) || e.LeftButton != MouseButtonState.Pressed
            || !_downAt.TryGetValue(tile, out var start))
        {
            return;
        }
        var now = e.GetPosition(null);
        if (Math.Abs(now.X - start.X) <= DragThreshold && Math.Abs(now.Y - start.Y) <= DragThreshold)
        {
            return;
        }
        _dragged.Add(tile);
        // A drag starts from a user's press, so the files are checked now (the only check off the background, and only on a drag).
        var files = DragFiles(tile.FilePath, _items, _selected, path => File.Exists(path) || Directory.Exists(path));
        tile.ReleaseMouseCapture();
        if (files.Count > 0)
        {
            var data = new DataObject(DataFormats.FileDrop, files.ToArray());
            _dragging = true;
            try
            {
                DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
            }
            finally
            {
                _dragging = false;
            }
        }
        ForgetPress(tile);
    }

    /// A click without a drag toggles the tile's selection, unless the press was the second of a double-click (it opened the file).
    private void OnTileUp(ShelfTile tile, MouseButtonEventArgs e)
    {
        tile.ReleaseMouseCapture();
        var click = _pressed.Contains(tile) && !_dragged.Contains(tile) && !_opened.Contains(tile);
        ForgetPress(tile);
        if (click)
        {
            Toggle(tile.FilePath);
        }
        e.Handled = true;
    }

    private void ForgetPress(ShelfTile tile)
    {
        _pressed.Remove(tile);
        _downAt.Remove(tile);
        _dragged.Remove(tile);
        _opened.Remove(tile);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SelectAll();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        foreach (var path in Targets().Where(_selected.Contains))
        {
            OpenPath(path);
        }
    }

    private void OnReveal(object sender, RoutedEventArgs e) => RevealPaths(Targets());

    private void OnShare(object sender, RoutedEventArgs e) => ShareFiles(Targets());

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveTargets();

    /// The share sheet for the existing files, shown for the pill's window. When the sheet does not open (no window, or Windows refuses),
    /// the files are shown in their folders instead, so the button never does nothing.
    private void ShareFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (files.Count == 0)
        {
            return;
        }
        var window = Window.GetWindow(this);
        var shared = window is not null && ShareSheet.Share(new WindowInteropHelper(window).Handle, files);
        if (!shared)
        {
            RevealPaths(files);
        }
    }

    /// Opens a file or folder in its own program (shell execute). A path that is gone does nothing.
    private static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// Explorer's window with each file selected: one window per distinct folder, on its first file.
    private static void RevealPaths(IEnumerable<string> paths)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(p => File.Exists(p) || Directory.Exists(p)))
        {
            var folder = Path.GetDirectoryName(path) ?? path;
            if (!folders.Add(folder))
            {
                continue;
            }
            try
            {
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                ErrorLog.Write(e);
            }
        }
    }

    private static MenuItem Entry(string header, Action action)
    {
        var item = new MenuItem { Header = header, Foreground = Brushes.White };
        item.Click += (_, _) => action();
        return item;
    }

    private static Button Wire(object content, Style style, RoutedEventHandler click)
    {
        var button = new Button { Content = content, Style = style, Focusable = false };
        button.Click += click;
        return button;
    }

    private static Style FooterStyle() => (Style)PillStyles.Value["FooterButton"];

    private static Style ChoiceStyle() => (Style)AskStyles.Value["ChoiceButton"];

    private static FluentGlyph Glyph(string glyph) => new()
    {
        Code = glyph,
        Size = 12,
        Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Text(string text, double size, FontFamily family, Brush brush, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = family,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        Focusable = false,
    };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// One file's tile on the shelf: its thumbnail (40 px), its name in the middle-truncated form, and a check badge when selected. A
/// file that is gone shows dimmed. The tile is kept per path by the card.
public sealed class ShelfTile : Border
{
    private const string CheckGlyph = "";  // a selected tile's badge
    private static readonly Brush NameBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush MissingNameBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush PlainFill = Frozen(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)));

    private readonly TextBlock _name;
    private readonly FluentGlyph _check;
    private (bool Exists, bool Selected, Color Accent)? _shown;  // what the tile shows now: Show changes nothing when it is the same

    public ShelfTile(string filePath)
    {
        FilePath = filePath;
        Focusable = false;
        Cursor = System.Windows.Input.Cursors.Hand;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(4, 6, 4, 6);
        Margin = new Thickness(0, 0, 8, 0);
        Background = PlainFill;
        BorderThickness = new Thickness(1.5);
        BorderBrush = Brushes.Transparent;

        Thumb = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true,
            Focusable = false,
        };
        var name = Path.GetFileName(filePath);
        _name = new TextBlock
        {
            Text = ShelfText.Middle(name.Length > 0 ? name : filePath),
            FontSize = 9,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Foreground = NameBrush,
            Width = 62,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            Focusable = false,
        };
        var content = new StackPanel { Focusable = false };
        content.Children.Add(Thumb);
        content.Children.Add(_name);

        _check = new FluentGlyph
        {
            Code = CheckGlyph,
            Size = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Visibility = Visibility.Collapsed,
            Focusable = false,
        };
        var layers = new Grid { Focusable = false };
        layers.Children.Add(content);
        layers.Children.Add(_check);
        Child = layers;
    }

    /// The file this tile shows.
    public string FilePath { get; }

    /// The thumbnail (its opacity drops for a missing file).
    public Image Thumb { get; }

    /// Whether the tile is selected.
    public bool IsSelected { get; private set; }

    /// Sets the tile's thumbnail to the WinRT thumbnail when it arrives.
    public void SetThumb(ImageSource image) => Thumb.Source = image;

    /// Shows the file's state: whether it exists (dimmed and titled "File not found anymore" when not), and whether it is selected (accent
    /// fill, accent stroke and check badge). A call with the state the tile already shows changes nothing.
    public void Show(bool exists, bool selected, Color accent)
    {
        IsSelected = selected;
        if (_shown is { } shown && shown.Exists == exists && shown.Selected == selected && shown.Accent == accent)
        {
            return;
        }
        _shown = (exists, selected, accent);
        _name.Foreground = exists ? NameBrush : MissingNameBrush;
        Thumb.Opacity = exists ? 1 : 0.35;
        ToolTip = exists ? FilePath : L10n.L("File not found anymore: {0}", FilePath);
        if (selected)
        {
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0x47, accent.R, accent.G, accent.B)));
            BorderBrush = TintBrushes.Of(accent);
            _check.Foreground = TintBrushes.Of(accent);
        }
        else
        {
            Background = PlainFill;
            BorderBrush = Brushes.Transparent;
        }
        _check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
