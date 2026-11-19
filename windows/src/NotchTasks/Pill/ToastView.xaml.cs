using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Notch;
using NotchTasks.Pill.Sections;

namespace NotchTasks.Pill;

/// The four looks of a toast (settings toastStyle; a compact toast is always compact).
public enum ToastLook
{
    Compact,
    Island,
    Banner,
    Card,
}

/// The toast the pill shows (Mac toastView and cardToast). Its look follows the setting toastStyle at each Show. An agent's
/// ask has Deny and Allow, or Reply for a question; any other toast has its action (when it has one) and ×. A click on the
/// toast, off its buttons, opens it: the pill decides what that does (ToastActions). The window never takes focus.
public partial class ToastView : UserControl
{
    private const double CompactSide = 52;        // the Mac's side column of a compact toast
    private const double CompactTitleSide = 170;  // the side column of a title in the compact style
    private const double CompactPadding = 14;     // the Mac's padding outside the compact row's columns
    private const double CardWidth = 380;
    private const double CardRadius = 18;
    private const double IslandWidth = 340;       // max(200 + 150, 340)

    private Toast? _toast;
    private AgentAsk? _ask;

    public ToastView()
    {
        InitializeComponent();
        Root.MouseLeftButtonUp += OnRootClick;
        AllowButton.Click += (_, _) => Answer(ReplyKinds.Allow);
        DenyButton.Click += (_, _) => Answer(ReplyKinds.Deny);
        ReplyButton.Click += (_, _) => OpenToast();
        ActionButton.Click += (_, _) => OpenToast();
        HideButton.Click += (_, _) => Dismissed?.Invoke();
    }

    /// Allow or Deny was pressed for the ask on the toast.
    public event Action<AgentAsk, AskReply>? Answered;

    /// The toast should open: a click on it, Reply, or its action.
    public event Action<Toast>? Opened;

    /// × was pressed: the toast leaves; an ask stays in the pill's list.
    public event Action? Dismissed;

    /// The corner radius of the shape the toast is drawn in, for the pill to spring to.
    public double ShapeRadius { get; private set; }

    /// The look a toast takes in a style: compact when the toast is compact or the style is.
    public static ToastLook LookOf(Toast t, string style) =>
        t.Compact || style == "compact" ? ToastLook.Compact
        : style switch
        {
            "card" => ToastLook.Card,
            "banner" => ToastLook.Banner,
            _ => ToastLook.Island,
        };

    /// The width of the toast in the pill: a compact row is the pill's 200 px between two side columns; a card is 380; a
    /// banner, and an island with an ask, 520; any other island 340.
    public static double WidthOf(Toast t, string style, AgentAsk? ask) => LookOf(t, style) switch
    {
        ToastLook.Compact => 2 * SideOf(t) + PillGeometry.ClosedWidth + 2 * CompactPadding,
        ToastLook.Card => CardWidth,
        ToastLook.Banner => PillGeometry.OpenWidth,
        _ => ask is null ? IslandWidth : PillGeometry.OpenWidth,
    };

    /// A toast's glyph in its tint and size: the drawn path for a name the font does not have (gauge, hourglass), else the font glyph.
    public static FrameworkElement GlyphOf(string name, Brush tint, double size) =>
        ModuleGlyphs.Vector(name) is { } data
            ? new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = tint,
                StrokeThickness = 1.6,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new TextBlock { Text = ModuleGlyphs.Get(name), Foreground = tint };

    /// Shows the toast in a style: its glyph, title, subtitle and the buttons it needs. `ask` is the agent's ask the toast
    /// stands for, or null. `motion` is the Motion setting, which a battery toast's art springs by.
    public void Show(Toast t, string style, AgentAsk? ask, string motion)
    {
        _toast = t;
        _ask = ask;
        var look = LookOf(t, style);
        var compact = look == ToastLook.Compact;
        var banner = look == ToastLook.Banner;
        var color = HexColor.TryNormalize(t.Tint, out var hex) ? HexColor.ToColor(hex) : Colors.White;
        var tint = Frozen(new SolidColorBrush(color));

        Width = WidthOf(t, style, ask);
        ShapeRadius = look switch
        {
            ToastLook.Compact => PillGeometry.ClosedRadius,
            ToastLook.Card => CardRadius,
            _ => PillGeometry.AskToastRadius,
        };
        CompactRow.Visibility = VisibleIf(compact);
        WideBody.Visibility = VisibleIf(!compact);
        CardFrame.Visibility = VisibleIf(look == ToastLook.Card);
        CardFrame.BorderBrush = Frozen(new SolidColorBrush(color) { Opacity = 0.5 });
        ToolTip = t.Subtitle.Length == 0 ? t.Title : t.Title + " · " + t.Subtitle;

        if (compact)
        {
            var side = SideOf(t);
            CompactLeft.Width = new GridLength(side);
            CompactRight.Width = new GridLength(side);
            CompactGlyph.Content = GlyphOf(t.Glyph, tint, CompactGlyph.FontSize);
            var art = t.Art as ChargeArt;
            CompactArt.Content = art is null ? null : new ChargeArtView(art, motion);
            CompactArt.Visibility = VisibleIf(art is not null);
            CompactGlyph.Visibility = VisibleIf(art is null);
            CompactText.Text = t.Compact ? t.Subtitle : t.Title;
            CompactText.Foreground = t.Compact ? tint : Brushes.White;
        }

        TopRow.Visibility = VisibleIf(look is ToastLook.Island or ToastLook.Banner);
        TopGlyph.Content = GlyphOf(t.Glyph, tint, TopGlyph.FontSize);
        BannerBar.Visibility = VisibleIf(banner);
        BannerBar.Background = tint;
        CardGlyph.Visibility = VisibleIf(look == ToastLook.Card);
        CardGlyph.Content = GlyphOf(t.Glyph, tint, CardGlyph.FontSize);
        TextRow.Margin = look == ToastLook.Card ? new Thickness(12) : new Thickness(18, 2, 18, banner ? 12 : 10);
        TitleText.Text = t.Title;
        TitleText.FontSize = banner ? 15 : 13;
        SubtitleText.Text = t.Subtitle;
        SubtitleText.FontSize = banner ? 12 : 11;
        SubtitleText.Visibility = VisibleIf(t.Subtitle.Length > 0);

        ApprovalButtons.Visibility = VisibleIf(ask is { IsQuestion: false });
        ReplyButton.Visibility = VisibleIf(ask is { IsQuestion: true });
        ReplyButton.Content = L10n.L("Reply");
        DenyButton.Content = L10n.L("Deny");
        AllowButton.Content = L10n.L("Allow");
        var action = ask is null ? t.ActionTitle : null;
        ActionButton.Content = action ?? string.Empty;
        ActionButton.Visibility = VisibleIf(action is not null);
        HideButton.ToolTip = L10n.L("Hide alert");
    }

    /// The side column of a compact toast: 52 for a compact toast, 170 for a title in the compact style.
    private static double SideOf(Toast t) => t.Compact ? CompactSide : CompactTitleSide;

    private void Answer(string kind)
    {
        if (_ask is { } ask)
        {
            Answered?.Invoke(ask, new AskReply(kind, null));
        }
    }

    private void OpenToast()
    {
        if (_toast is { } t)
        {
            Opened?.Invoke(t);
        }
    }

    /// A click on the toast's own surface opens it. A click on a button is left to the button, and is handled here so
    /// that the pill's own click handler does not also act on it.
    private void OnRootClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsInsideButton(e.OriginalSource as DependencyObject))
        {
            OpenToast();
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }
        return false;
    }

    private static Visibility VisibleIf(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
