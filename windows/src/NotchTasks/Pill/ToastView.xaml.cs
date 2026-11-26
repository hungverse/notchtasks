using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
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

    /// The compact row's middle: the notch width of a screen without a notch, which the island always is (Mac AppDelegate.swift:402
    /// sets 12 there). The text sits in the right column, so the middle is only a spacer.
    private const double IslandNotchWidth = 12;
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

    /// The width of the toast in the pill: a compact row is two side columns around the notch width (IslandNotchWidth), with padding
    /// outside them, as the Mac's compact row; a card is 380; a banner, and an island with an ask, 520; any other island 340.
    public static double WidthOf(Toast t, string style, AgentAsk? ask) => LookOf(t, style) switch
    {
        ToastLook.Compact => 2 * SideOf(t) + IslandNotchWidth + 2 * CompactPadding,
        ToastLook.Card => CardWidth,
        ToastLook.Banner => PillGeometry.OpenWidth,
        _ => ask is null ? IslandWidth : PillGeometry.OpenWidth,
    };

    /// A toast's glyph in its tint and size: the drawn path for a Mac symbol the font does not have (the CI seal and octagon, the build marks), else the font glyph.
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
            : new TextBlock { Text = ModuleGlyphs.Get(name).Glyph, FontFamily = ModuleGlyphs.Get(name).Family, Foreground = tint };

    /// The icon a toast draws in its glyph slot for its agent or app (Mac toastIcon): the item's own icon in the tint, so the agent
    /// shows where a glyph would. Null for a toast with no item, which keeps its glyph.
    public static FrameworkElement? IconOf(Toast t, Color tint, double size, IProcessReader? processes) =>
        t.Icon is { } item ? ItemIcon.Element(new ItemState(item, Array.Empty<Job>()), size, tint, processes) : null;

    /// The tint symbol drawn on the right of a toast that has an item (Mac: the symbol beside the icon, which tells the toast's kind).
    /// Null for a toast with no item.
    public static FrameworkElement? TrailOf(Toast t, Brush tint, double size) =>
        t.Icon is null ? null : GlyphOf(t.Glyph, tint, size);

    /// Shows the toast in a style: its glyph, title, subtitle and the buttons it needs. `ask` is the agent's ask the toast
    /// stands for, or null. `motion` is the Motion setting, which a battery toast's art springs by. `processes` reads the program
    /// icon of the toast's item (an app's icon, or the agent's process); null reads none.
    public void Show(Toast t, string style, AgentAsk? ask, string motion, IProcessReader? processes = null)
    {
        // A toast that replaces one on screen cross-fades: the old content is kept as a picture, over the new, and fades out.
        var previous = _toast is null ? null : Snapshot();
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
            CompactMiddle.Width = new GridLength(IslandNotchWidth);
            CompactRight.Width = new GridLength(side);
            CompactGlyph.Content = IconOf(t, color, CompactGlyph.FontSize, processes) ?? GlyphOf(t.Glyph, tint, CompactGlyph.FontSize);
            var art = t.Art as ChargeArt;
            var device = t.Art as DeviceArt;
            CompactArt.Content = art is not null ? new ChargeArtView(art, motion)
                : device is not null ? new DeviceArtView(device, tint, motion, CompactGlyph.FontSize)
                : null;
            CompactArt.Visibility = VisibleIf(art is not null || device is not null);
            CompactGlyph.Visibility = VisibleIf(art is null && device is null);
            CompactText.Text = t.Compact ? t.Subtitle : t.Title;
            CompactText.Foreground = t.Compact ? tint : Brushes.White;
        }

        // A device's art takes the lead glyph's slot in each look, at that slot's size (the top row does not grow).
        var deviceArt = t.Art as DeviceArt;
        TopRow.Visibility = VisibleIf(look is ToastLook.Island or ToastLook.Banner);
        TopGlyph.Content = deviceArt is not null && look is ToastLook.Island or ToastLook.Banner
            ? new DeviceArtView(deviceArt, tint, motion, TopGlyph.FontSize)
            : IconOf(t, color, TopGlyph.FontSize, processes) ?? GlyphOf(t.Glyph, tint, TopGlyph.FontSize);
        var topTrail = TrailOf(t, tint, 14);
        TopTrail.Content = topTrail;
        TopTrail.Visibility = VisibleIf(topTrail is not null);
        BannerBar.Visibility = VisibleIf(banner);
        BannerBar.Background = tint;
        CardGlyph.Visibility = VisibleIf(look == ToastLook.Card);
        CardGlyph.Content = deviceArt is not null && look == ToastLook.Card
            ? new DeviceArtView(deviceArt, tint, motion, CardGlyph.FontSize)
            : IconOf(t, color, CardGlyph.FontSize, processes) ?? GlyphOf(t.Glyph, tint, CardGlyph.FontSize);
        var cardTrail = TrailOf(t, tint, 15);
        CardTrail.Content = cardTrail;
        CardTrail.Visibility = VisibleIf(cardTrail is not null && look == ToastLook.Card);
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
        if (previous is not null)
        {
            CrossFade(previous, motion);
        }
    }

    /// The toast's content as it is on screen now, as a picture. Null when the toast is not shown (there is nothing to fade from).
    private RenderTargetBitmap? Snapshot()
    {
        if (!IsVisible || Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            return null;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(Root);
        bitmap.Freeze();
        return bitmap;
    }

    /// The old content fades out over the new, which is already in place: a cross-fade over PillMotion.Fade. Composited, as a picture.
    private void CrossFade(RenderTargetBitmap from, string motion)
    {
        var overlay = new Image { Source = from, Stretch = Stretch.Fill, IsHitTestVisible = false, Focusable = false };
        Root.Children.Add(overlay);
        var duration = PillMotion.Fade(motion, SystemParameters.ClientAreaAnimation);
        var animation = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = PillMotion.Ease(duration),
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) => Root.Children.Remove(overlay);
        overlay.BeginAnimation(OpacityProperty, animation);
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
