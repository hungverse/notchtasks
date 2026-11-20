using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill.Sections;

/// The Now Playing card in the open pill (Mac NowPlayingCard): the artwork (a click brings the playing app's window to the front), the
/// title (or "Playing audio"), the artist and app line with the app's icon, the seek bar, and the previous, play or pause and next
/// buttons. A button whose control the session lacks is disabled at 35 % opacity. The card is one element kept for the pill's life, so
/// a click on its buttons is not lost when the pill rebuilds its sections: Update refreshes it from the state, and returns false (and
/// leaves the card alone) when no session is shown.
public sealed class NowPlayingCard : Border
{
    private const double ArtSize = 46;
    private const double ArtCorner = 0.22;   // the art's corner radius, as a share of its size (Mac)
    private const double AppIconSize = 12;
    private const double ControlSize = 30;
    private const double PlayPauseGlyphSize = 18;
    private const double SkipGlyphSize = 12;
    private const double DisabledOpacity = 0.35;

    private static readonly Color DefaultTint = Color.FromRgb(0x32, 0xD2, 0xFF);  // the media chip's default: a cyan, as the chip's
    private static readonly Brush CardFill = Freeze(new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ArtFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CaptionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly FontFamily SegoeFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFamily = new("Segoe UI Variable Text, Segoe UI");

    private readonly Border _artFrame;
    private readonly Image _artImage;
    private readonly TextBlock _artGlyph;
    private readonly TextBlock _title;
    private readonly Image _appIcon;
    private readonly TextBlock _caption;
    private AppState? _state;
    private NowPlaying? _shown;

    public NowPlayingCard()
    {
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(14);
        Background = CardFill;

        _artImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            Width = ArtSize,
            Height = ArtSize,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
            Clip = new RectangleGeometry(new Rect(0, 0, ArtSize, ArtSize), ArtSize * ArtCorner, ArtSize * ArtCorner),
            Focusable = false,
        };
        _artGlyph = new TextBlock
        {
            Text = ModuleGlyphs.Get("media"),
            FontFamily = SegoeFamily,
            FontSize = 18,
            Foreground = CaptionBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        var artContent = new Grid { Width = ArtSize, Height = ArtSize, Focusable = false };
        artContent.Children.Add(_artGlyph);
        artContent.Children.Add(_artImage);
        _artFrame = new Border
        {
            Width = ArtSize,
            Height = ArtSize,
            CornerRadius = new CornerRadius(ArtSize * ArtCorner),
            Background = ArtFill,
            Child = artContent,
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        _artFrame.MouseLeftButtonUp += OnArtClick;

        _title = new TextBlock
        {
            FontFamily = TextFamily,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Focusable = false,
        };
        _appIcon = new Image
        {
            Width = AppIconSize,
            Height = AppIconSize,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        _caption = new TextBlock
        {
            FontFamily = TextFamily,
            FontSize = 11,
            Foreground = CaptionBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Focusable = false };
        line.Children.Add(_appIcon);
        line.Children.Add(_caption);
        // the Mac's VStack(title, subline, seek bar): 12 DIP after the art, and a 4 DIP minimum gap before the buttons
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), Focusable = false };
        text.Children.Add(_title);
        text.Children.Add(line);
        Seek = new SeekBar(seconds => _state?.MediaSeek(seconds)) { Margin = new Thickness(0, 6, 0, 0), Focusable = false };
        text.Children.Add(Seek);

        // one row, as the Mac: art | text | previous, play or pause, next (6 DIP apart), centred vertically
        var top = new Grid { Focusable = false };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_artFrame);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        PreviousButton = ControlButton("previous", SkipGlyphSize, new Thickness(0, 0, 4, 0));
        PreviousButton.Click += (_, _) => _state?.MediaControl(MediaCommand.Previous);
        PlayPauseButton = ControlButton("play", PlayPauseGlyphSize, new Thickness(0, 0, 4, 0));
        PlayPauseButton.Click += (_, _) => _state?.MediaControl(MediaCommand.Toggle);
        NextButton = ControlButton("next", SkipGlyphSize, new Thickness(0));
        NextButton.Click += (_, _) => _state?.MediaControl(MediaCommand.Next);
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        controls.Children.Add(PreviousButton);
        controls.Children.Add(PlayPauseButton);
        controls.Children.Add(NextButton);
        Grid.SetColumn(controls, 2);
        top.Children.Add(controls);
        Child = top;
    }

    /// The seek bar (its Visibility is set by Update: it shows when the session has a duration and a position).
    public SeekBar Seek { get; }

    /// The card's title text (the title, or "Playing audio" when there is none).
    public TextBlock TitleText => _title;

    /// The previous, play or pause, and next buttons.
    public Button PreviousButton { get; }

    public Button PlayPauseButton { get; }

    public Button NextButton { get; }

    /// Refreshes the card from the shown session and returns true. Returns false, and leaves the card alone, when no session is shown.
    public bool Update(AppState state, Color? accent)
    {
        if (state.Snapshot.Media is not { } shown)
        {
            return false;
        }
        _state = state;
        _shown = shown;

        var art = shown.ArtworkKey is { } key ? state.MediaArt(key) : null;
        _artImage.Source = art;
        _artImage.Visibility = art is null ? Visibility.Collapsed : Visibility.Visible;
        _artGlyph.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
        _artFrame.Cursor = shown.AppExe is null ? Cursors.Arrow : Cursors.Hand;
        _artFrame.ToolTip = L10n.L("Open {0}", shown.AppName);

        var icon = shown.AppExe is { } exe ? ItemIcon.ExeIcon(exe) : null;
        _appIcon.Source = icon;
        _appIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        _caption.Margin = icon is null ? new Thickness(0) : new Thickness(4, 0, 0, 0);
        _title.Text = string.IsNullOrWhiteSpace(shown.Title) ? L10n.L("Playing audio") : shown.Title;
        _caption.Text = Caption(shown);

        SetControl(PreviousButton, shown.CanPrevious, "previous", SkipGlyphSize);
        SetControl(PlayPauseButton, shown.CanToggle, shown.Playing ? "pause" : "play", PlayPauseGlyphSize);
        SetControl(NextButton, shown.CanNext, "next", SkipGlyphSize);

        Seek.Update(shown, TintOf(state.Settings.ModuleColors, accent));
        Seek.Visibility = shown.Duration is not null && shown.Position is not null ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    /// The media tint, by the chip's rule: the media module's colour, else the chosen accent, else the default cyan.
    public static Color TintOf(IReadOnlyDictionary<string, string> moduleColors, Color? accent) =>
        moduleColors.TryGetValue("media", out var hex) && HexColor.TryNormalize(hex, out var normalized)
            ? HexColor.ToColor(normalized)
            : accent ?? DefaultTint;

    /// "artist · app": the artist (when there is one) and the app's name.
    private static string Caption(NowPlaying shown) =>
        string.Join(" · ", new[] { shown.Artist, shown.AppName }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// The Mac's filled double triangle for previous and next: 16 by 11 DIP, a pair of triangles pointing one way.
    private static readonly Geometry NextSkip = Freeze(Geometry.Parse("M0,0 L8,5.5 L0,11 Z M8,0 L16,5.5 L8,11 Z"));
    private static readonly Geometry PreviousSkip = Freeze(Geometry.Parse("M16,0 L8,5.5 L16,11 Z M8,0 L0,5.5 L8,11 Z"));

    /// A control's content: the filled triangles for previous and next (vector, as the Mac's), the Segoe glyph for play and pause.
    private static object ControlContent(string name) => name switch
    {
        "previous" => new System.Windows.Shapes.Path { Data = PreviousSkip, Fill = Brushes.White },
        "next" => new System.Windows.Shapes.Path { Data = NextSkip, Fill = Brushes.White },
        _ => ModuleGlyphs.Get(name),
    };

    /// The plain template of the control buttons: a transparent border, so the whole square takes the mouse, around the centred
    /// content. WPF's default template paints a grey background when a button is disabled; the Mac's buttons have no chrome.
    private static readonly ControlTemplate PlainControl = PlainControlTemplate();

    private static ControlTemplate PlainControlTemplate()
    {
        var body = new FrameworkElementFactory(typeof(Border), "Body");
        body.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        body.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = body };
        // The Mac dims a pressable control under the pointer to 0.8; a disabled one keeps its 0.35 and does not brighten.
        template.Triggers.Add(new MultiTrigger
        {
            Conditions =
            {
                new Condition(UIElement.IsMouseOverProperty, true),
                new Condition(UIElement.IsEnabledProperty, true),
            },
            Setters = { new Setter(UIElement.OpacityProperty, 0.8, "Body") },
        });
        return template;
    }

    private static Button ControlButton(string glyph, double size, Thickness margin) => new()
    {
        Width = ControlSize,
        Height = ControlSize,
        Padding = new Thickness(0),
        Margin = margin,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        FontFamily = SegoeFamily,
        FontSize = size,
        FontWeight = FontWeights.Bold,
        Content = ControlContent(glyph),
        Template = PlainControl,
        Cursor = Cursors.Hand,
        Focusable = false,
        FocusVisualStyle = null,
    };

    /// A control's glyph and whether it can be pressed: a control the session lacks is disabled and dimmed.
    private static void SetControl(Button button, bool can, string glyph, double size)
    {
        button.IsEnabled = can;
        button.Opacity = can ? 1 : DisabledOpacity;
        button.Content = ControlContent(glyph);
        button.FontSize = size;
    }

    /// The art click brings the app's window to the front, on this click only (the one focus change the card makes).
    private void OnArtClick(object sender, MouseButtonEventArgs e)
    {
        if (_shown?.AppExe is { } exe)
        {
            BringToFront(exe);
        }
        e.Handled = true;
    }

    /// Restores the first process of the exe that has a main window, and brings it to the front. A process with no window, or no
    /// process at all, does nothing.
    private static void BringToFront(string exe)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try
        {
            foreach (var process in processes)
            {
                var window = process.MainWindowHandle;
                if (window == nint.Zero)
                {
                    continue;
                }
                // restore only a minimised window: a maximised one keeps its size
                if (Win32.IsIconic(window))
                {
                    Win32.ShowWindow(window, Win32.SW_RESTORE);
                }
                Win32.SetForegroundWindow(window);
                return;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
