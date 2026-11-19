using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Services;

namespace NotchTasks.Pill.Sections;

/// One agent's card in the open pill (Mac ItemCard). The card is kept per item across publishes, and Update changes only what
/// differs: the icon when its image or tint changes, the badges when the windows, tint or width change, and each row's text and
/// lead when they change. Clicking the header opens an app item; clicking a job with a web link, or its log glyph, opens the link.
public partial class ItemCard : UserControl
{
    /// The most job rows a card shows; the rest are counted in "+n more tasks".
    public const int MaxRows = 4;

    private const double IconSize = 20;
    private static readonly Brush GreyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));  // the Mac's .gray
    private static readonly Brush TitleBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));  // white at 90 %
    private static readonly FontFamily SegoeFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly JobRow[] _rows = new JobRow[MaxRows];
    private ItemState? _state;
    private IReadOnlyList<LimitWindow> _limits = Array.Empty<LimitWindow>();
    private DateTimeOffset _now;
    private string? _badgeKey;
    private bool _iconSet;
    private string? _iconPath;
    private Color _iconTint;

    public ItemCard()
    {
        InitializeComponent();
        for (var i = 0; i < MaxRows; i++)
        {
            _rows[i] = new JobRow(i, OpenUrl);
            Rows.Children.Add(_rows[i].Root);
        }
        Header.MouseLeftButtonUp += OnHeaderClick;
        Header.SizeChanged += OnHeaderSized;
    }

    /// The colour the card was last updated with (the same one its chip uses: ItemTint.Of).
    public Color Tint { get; private set; }

    /// The job rows showing now (at most MaxRows).
    public int ShownRows => _rows.Count(r => r.Root.Visibility == Visibility.Visible);

    /// The limit badges in the header now.
    public int LimitBadgeCount => Badges.Children.OfType<FrameworkElement>().Count(e => e.Tag is string tag && tag == LimitBadge.Tag);

    /// The header's badge elements, in order (for tests: an unchanged publish keeps them).
    public IReadOnlyList<object> BadgeChildren => Badges.Children.Cast<object>().ToList();

    /// The icon element in the header (for tests: an unchanged publish keeps it).
    public object? IconContent => IconHost.Content;

    /// Each shown row's lead element, in order (for tests: an unchanged publish keeps them).
    public IReadOnlyList<object?> RowLeads => _rows.Select(r => r.Lead).ToList();

    /// The windows an item's card shows (Mac: limits(st)): the provider of its kind (Claude or Codex), or for a process or app item
    /// the process provider of its bundle (Antigravity). Empty when there is none.
    public static IReadOnlyList<LimitWindow> LimitsFor(WatchItem item, IReadOnlyList<ProviderLimits> providers)
    {
        var kind = item.Kind switch
        {
            WatchKind.Claude => "claude",
            WatchKind.Codex => "codex",
            _ => null,
        };
        var provider = kind is not null
            ? providers.FirstOrDefault(p => p.Kind == kind)
            : item.BundleID is { Length: > 0 } bundle
                ? providers.FirstOrDefault(p => p.Kind == "process" && p.BundleID == bundle)
                : null;
        return provider?.Windows ?? Array.Empty<LimitWindow>();
    }

    /// The program an app item opens: its exe name or path (WatchItems.AppExe), or null when none is known.
    public static string? LaunchTarget(WatchItem item) => WatchItems.AppExe(item);

    /// Refreshes the card from the item. processes reads an app item's icon (null: the sparkles glyph).
    public void Update(ItemState state, IReadOnlyList<LimitWindow> limits, Color tint, IProcessReader? processes, DateTimeOffset now)
    {
        _state = state;
        _limits = limits;
        _now = now;
        Tint = tint;
        SetText(NameText, state.Item.Name);

        var path = ItemIcon.PathOf(state, processes);
        if (!_iconSet || path != _iconPath || tint != _iconTint)
        {
            IconHost.Content = ItemIcon.Element(state, IconSize, tint, processes);
            _iconSet = true;
            _iconPath = path;
            _iconTint = tint;
        }

        var count = state.Jobs.Count.ToString(CultureInfo.InvariantCulture);
        CountPill.Background = TintBrushes.Of(tint);
        SetText(CountText, count);
        SetToolTip(CountPill, L10n.L("{0} tasks running", state.Jobs.Count));
        if (BadgeKey() != _badgeKey)
        {
            FitBadges();
        }

        for (var i = 0; i < MaxRows; i++)
        {
            if (i < state.Jobs.Count)
            {
                _rows[i].Set(state.Jobs[i], tint);
            }
            else
            {
                _rows[i].Root.Visibility = Visibility.Collapsed;
            }
        }
        var more = state.Jobs.Count - MaxRows;
        MoreText.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (more > 0)
        {
            SetText(MoreText, L10n.L("+{0} more tasks", more));
        }
    }

    /// What the badges depend on: the windows (label, used, reset), the tint, the name and the header's width. A publish that changes
    /// none of them keeps the badges and their tooltips.
    private string BadgeKey()
    {
        var windows = string.Join(";", _limits.Select(w =>
            string.Create(CultureInfo.InvariantCulture, $"{w.Label}|{w.Used}|{w.Resets.UtcTicks}")));
        return string.Create(CultureInfo.InvariantCulture, $"{windows}#{Tint}#{Header.ActualWidth}#{NameText.Text}");
    }

    /// The widest set of badges that fits beside the name (Mac ViewThatFits): the full labels first, then the narrow ones, then
    /// none. Before the card is laid out, the full set is used.
    private void FitBadges()
    {
        _badgeKey = BadgeKey();
        Badges.Children.Clear();
        Badges.Visibility = Visibility.Visible;
        if (_limits.Count == 0)
        {
            Badges.Visibility = Visibility.Collapsed;
            return;
        }
        var room = RoomForBadges();
        var fits = false;
        for (var narrow = 0; narrow <= 2 && !fits; narrow++)
        {
            Badges.Children.Clear();
            foreach (var element in BadgeElements(narrow))
            {
                Badges.Children.Add(element);
            }
            Badges.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            fits = double.IsPositiveInfinity(room) || Badges.DesiredSize.Width <= room;
        }
        if (!fits)
        {
            Badges.Children.Clear();
        }
        Badges.Visibility = Badges.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// The width the badges may take: the header less the icon, the name and the count. Infinite before the header is laid out.
    private double RoomForBadges()
    {
        if (Header.ActualWidth <= 0)
        {
            return double.PositiveInfinity;
        }
        NameText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        CountPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // the margins: the name 8, the badges 6, the count 8
        var others = IconHost.Width + 8 + NameText.DesiredSize.Width + 6 + 8 + CountPill.DesiredSize.Width;
        return Math.Max(0, Header.ActualWidth - others);
    }

    /// The badges at one narrowness: each window's badge, then "+n" for the windows left out, with their tooltips.
    private IEnumerable<FrameworkElement> BadgeElements(int narrow)
    {
        var (shown, hidden) = Limit.Badges(_limits, narrow, _now);
        var index = 0;
        foreach (var badge in shown)
        {
            var element = LimitBadge.Build(badge, _now);
            element.Margin = new Thickness(index++ == 0 ? 0 : 6, 0, 0, 0);
            yield return element;
        }
        if (hidden.Count > 0)
        {
            yield return new TextBlock
            {
                Text = $"+{hidden.Count}",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = GreyBrush,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = string.Join("\n", hidden.Select(w => LimitBadge.Tooltip(w, _now))),
                Focusable = false,
            };
        }
    }

    private void OnHeaderSized(object sender, SizeChangedEventArgs e)
    {
        if (_state is not null && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
        {
            FitBadges();
        }
    }

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenItem();
    }

    /// Opens an app item. A running one (its first job's pid) has its main window brought forward, restored when minimised. One
    /// that is not running is started by its program name (Windows resolves it through App Paths). Other items do nothing.
    private void OpenItem()
    {
        if (_state is not { } state || state.Item.Kind != WatchKind.App)
        {
            return;
        }
        if (ItemIcon.Pid(state) is { } pid && TryMainWindow(pid, out var window))
        {
            if (window != 0)
            {
                if (Win32.IsIconic(window))
                {
                    Win32.ShowWindow(window, Win32.SW_RESTORE);
                }
                Win32.SetForegroundWindow(window);
            }
            return;
        }
        if (LaunchTarget(state.Item) is { } target)
        {
            Launch(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    /// True when the process runs; window is its main window (0 when it has none).
    private static bool TryMainWindow(int pid, out nint window)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            window = process.MainWindowHandle;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            window = 0;
            return false;
        }
    }

    /// Opens a job's link in its program. Only web links open: a job's text must not start a program or a file.
    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Launch(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private static void Launch(ProcessStartInfo start)
    {
        try
        {
            using var started = Process.Start(start);
        }
        catch (Win32Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// Sets a text only when it differs, so an unchanged publish touches nothing.
    private static void SetText(TextBlock text, string value)
    {
        if (text.Text != value)
        {
            text.Text = value;
        }
    }

    /// Sets a tooltip only when it differs, so an unchanged publish keeps the tooltip element.
    private static void SetToolTip(FrameworkElement element, string tip)
    {
        if (element.ToolTip as string != tip)
        {
            element.ToolTip = tip;
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// The glyph or vector name of a job's symbol (the Mac's symbol names), or null for a plain status dot.
    internal static string? MarkOf(string? symbol)
    {
        if (symbol is null)
        {
            return null;
        }
        if (symbol.Contains("checkmark", StringComparison.OrdinalIgnoreCase))
        {
            return "check";
        }
        if (symbol.Contains("sparkles", StringComparison.OrdinalIgnoreCase))
        {
            return "sparkles";
        }
        if (symbol.StartsWith("gauge", StringComparison.OrdinalIgnoreCase))
        {
            return "gauge";
        }
        return symbol.Equals("hourglass", StringComparison.OrdinalIgnoreCase) ? "hourglass" : null;
    }

    /// One job row: its lead (a status dot, or the job's symbol), its title, its detail and, with a link, the log glyph. The lead
    /// element is kept while its kind stays the same; only its colour is set again.
    private sealed class JobRow
    {
        private readonly Action<string> _open;
        private readonly ContentControl _lead = new() { Width = 20, Height = 20, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title = new()
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = TitleBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        private readonly TextBlock _detail = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Focusable = false };
        private readonly TextBlock _log = new()
        {
            Text = ModuleGlyphs.Get("log"),
            FontFamily = SegoeFamily,
            FontSize = 10,
            Foreground = GreyBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = L10n.L("Open log"),
            Focusable = false,
        };
        private string? _url;
        private string? _leadKind;

        public JobRow(int index, Action<string> open)
        {
            _open = open;
            var grid = new Grid { Focusable = false, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });  // the lead, indented as the Mac's 28 pt
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_lead, 0);
            Grid.SetColumn(_title, 1);
            Grid.SetColumn(_detail, 2);
            Grid.SetColumn(_log, 3);
            grid.Children.Add(_lead);
            grid.Children.Add(_title);
            grid.Children.Add(_detail);
            grid.Children.Add(_log);
            Root = grid;
            Root.Margin = new Thickness(0, index == 0 ? 0 : 7, 0, 0);
            Root.Visibility = Visibility.Collapsed;
            Root.MouseLeftButtonUp += OnRowClick;
            _log.MouseLeftButtonUp += OnLogClick;
        }

        public Grid Root { get; }

        /// The lead element now (null before the row is first set).
        public object? Lead => _lead.Content;

        /// Shows a job in this row, in the tint while it works and grey when it has stopped.
        public void Set(Job job, Color tint)
        {
            var brush = job.Working ? TintBrushes.Of(tint) : GreyBrush;
            Root.Visibility = Visibility.Visible;
            var tip = job.Title + " — " + job.Detail;
            if (Root.ToolTip as string != tip)
            {
                Root.ToolTip = tip;
            }
            SetText(_title, job.Title);
            SetText(_detail, job.Detail);
            _detail.Foreground = brush;
            var kind = LeadKind(job.Symbol);
            if (kind != _leadKind || _lead.Content is null)
            {
                _lead.Content = BuildLead(job.Symbol, brush);
                _leadKind = kind;
            }
            else
            {
                Recolour(_lead.Content, brush);
            }
            _url = job.Url;
            _log.Visibility = job.Url is null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// The kind of lead a symbol gives: a dot, a font glyph, or a vector.
        private static string LeadKind(string? symbol) => MarkOf(symbol) is { } mark
            ? (ModuleGlyphs.Vector(mark) is not null ? "vector:" : "glyph:") + mark
            : "dot";

        private static FrameworkElement BuildLead(string? symbol, Brush brush)
        {
            var mark = MarkOf(symbol);
            if (mark is not null && ModuleGlyphs.Vector(mark) is { } path)
            {
                return new Path
                {
                    Data = Geometry.Parse(path),
                    Stroke = brush,
                    StrokeThickness = 1.4,
                    Stretch = Stretch.Uniform,
                    Width = 12,
                    Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Focusable = false,
                };
            }
            if (mark is not null)
            {
                return new TextBlock
                {
                    Text = ModuleGlyphs.Get(mark),
                    FontFamily = SegoeFamily,
                    FontSize = 12,
                    Foreground = brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Focusable = false,
                };
            }
            return new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
        }

        /// Sets the colour of a lead element of the same kind.
        private static void Recolour(object content, Brush brush)
        {
            switch (content)
            {
                case Ellipse dot:
                    dot.Fill = brush;
                    break;
                case TextBlock glyph:
                    glyph.Foreground = brush;
                    break;
                case Path vector:
                    vector.Stroke = brush;
                    break;
            }
        }

        private static void SetText(TextBlock text, string value)
        {
            if (text.Text != value)
            {
                text.Text = value;
            }
        }

        private void OnRowClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_url is { } url)
            {
                _open(url);
            }
        }

        private void OnLogClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_url is { } url)
            {
                _open(url);
            }
        }
    }
}
