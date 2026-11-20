using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NotchTasks.Core;
using NotchTasks.Services;

namespace NotchTasks.Pill.Sections;

/// The camera mirror's card in the open pill (Mac MirrorCard): a quick look at yourself, flipped like a mirror, from the camera.
/// The card is one element kept for the pill's life. Update shows the frames while the mirror is on, or the message for why there
/// is no picture (the camera is denied, missing or failed). The camera itself runs in AppState, only while the mirror is open.
public sealed class MirrorCard : Border
{
    private const double FrameHeight = 220;
    private const double MessageHeight = 150;
    private const double Radius = 14;
    private const string CloseGlyph = "";  // Segoe Fluent cancel: the × of the card (Mac xmark)
    private const string PrivacyUri = "ms-settings:privacy-webcam";
    private const string DeniedGlyph = "video.slash";  // a drawn camera with a slash (ModuleGlyphs.Vector): the font has none

    private static readonly Brush CardFill = Frozen(new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CloseFill = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)));
    private static readonly Brush FrameFill = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Brush GreyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly FontFamily GlyphFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFamily = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Lazy<ResourceDictionary> AskStyles = new(() => new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/NotchTasks;component/Pill/AskStyles.xaml", UriKind.Absolute),
    });

    private readonly Grid _frameHost;
    private readonly Image _image;
    private readonly StackPanel _message;
    private readonly Path _messageGlyph;
    private readonly TextBlock _messageText;
    private readonly Button _settings;
    private readonly Button _close;
    private AppState? _state;
    private WriteableBitmap? _bitmap;  // one bitmap, made again only when the frame size changes

    public MirrorCard()
    {
        Focusable = false;
        Background = CardFill;
        CornerRadius = new CornerRadius(Radius);

        _image = new Image
        {
            Stretch = Stretch.UniformToFill,
            // flipped horizontally, as a mirror shows you
            RenderTransform = new ScaleTransform(-1, 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Focusable = false,
            IsHitTestVisible = false,
        };
        _frameHost = new Grid { Height = FrameHeight, Background = FrameFill, Focusable = false };
        // the frame's corners are rounded: the clip follows the size the host gets
        _frameHost.SizeChanged += (_, e) => _frameHost.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), Radius, Radius);
        _frameHost.Children.Add(_image);

        _messageGlyph = new Path
        {
            Data = Geometry.Parse(ModuleGlyphs.Vector(DeniedGlyph) ?? string.Empty),
            Stroke = GreyBrush,
            StrokeThickness = 1.5,
            Stretch = Stretch.Uniform,
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            Focusable = false,
        };
        _messageText = new TextBlock
        {
            FontFamily = TextFamily,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 8, 16, 0),
            Focusable = false,
        };
        _settings = new Button
        {
            Style = (Style)AskStyles.Value["ChoiceButton"],
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Focusable = false,
        };
        _settings.Click += (_, _) => SystemActions.OpenSettings(PrivacyUri);
        _message = new StackPanel
        {
            Height = MessageHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        _message.Children.Add(_messageGlyph);
        _message.Children.Add(_messageText);
        _message.Children.Add(_settings);

        var body = new Grid { Focusable = false };
        body.Children.Add(_frameHost);
        body.Children.Add(_message);

        // The toast's 22 px circle × with the card's darker fill: the style's own fill is replaced, its hover stays.
        var closeStyle = new Style(typeof(Button), (Style)AskStyles.Value["CloseButton"]);
        closeStyle.Setters.Add(new Setter(Control.BackgroundProperty, CloseFill));
        _close = new Button
        {
            Style = closeStyle,
            Content = new TextBlock
            {
                Text = CloseGlyph,
                FontFamily = GlyphFamily,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Focusable = false,  // a click closes the mirror; the button never takes the keyboard focus from the app the user works in
        };
        _close.Click += (_, _) => _state?.CloseMirror();
        body.Children.Add(_close);

        Child = body;
    }

    /// The message the card shows instead of the picture, or null while the picture shows.
    public string? ShownMessage => _message.Visibility == Visibility.Visible ? _messageText.Text : null;

    /// True while the message offers the Windows camera settings.
    public bool SettingsButtonShown => _message.Visibility == Visibility.Visible && _settings.Visibility == Visibility.Visible;

    /// Refreshes the card from the mirror and returns true. Returns false, and detaches from the frames, when the mirror is closed.
    public bool Update(AppState state)
    {
        if (!state.MirrorOn)
        {
            Detach();
            return false;
        }
        Attach(state);
        _close.ToolTip = L10n.L("Close mirror");
        switch (state.MirrorStatus)
        {
            case CameraStart.Denied:
                ShowMessage(L10n.L("NotchTasks can't use the camera yet"), settings: true);
                break;
            case CameraStart.NoCamera:
            case CameraStart.Failed:
                ShowMessage(L10n.L("No camera found"), settings: false);
                break;
            default:  // still starting, or running: the picture shows (dark until the first frame)
                _frameHost.Visibility = Visibility.Visible;
                _message.Visibility = Visibility.Collapsed;
                break;
        }
        return true;
    }

    private void ShowMessage(string text, bool settings)
    {
        _frameHost.Visibility = Visibility.Collapsed;
        _message.Visibility = Visibility.Visible;
        _messageText.Text = text;
        _settings.Content = L10n.L("Open Camera settings");
        _settings.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Subscribes to the mirror's frames while it is shown.
    private void Attach(AppState state)
    {
        if (ReferenceEquals(_state, state))
        {
            return;
        }
        Detach();
        _state = state;
        state.MirrorFrameReady += OnFrame;
    }

    private void Detach()
    {
        if (_state is not { } state)
        {
            return;
        }
        state.MirrorFrameReady -= OnFrame;
        _state = null;
        _image.Source = null;
        _bitmap = null;
    }

    /// Copies one frame into the bitmap (made again only when the size changes). Runs on the UI thread.
    private void OnFrame(MirrorPixels pixels)
    {
        if (_bitmap is null || _bitmap.PixelWidth != pixels.Width || _bitmap.PixelHeight != pixels.Height)
        {
            _bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Bgra, pixels.Width * 4, 0);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
