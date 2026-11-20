using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Ci;

namespace NotchTasks.Pill.Sections;

/// One failed CI job's alert in the open pill (Mac AlertCard, Electron alertCard): a red octagon, "CI failed: {name}", "<project> · <reason>",
/// a "View log" pill when the job has a log, and an × that hides the alert. A click on the card opens the log. The card is kept per alert id
/// and Update sets only the texts that differ. Focusable is off throughout: the pill never takes focus.
public sealed class AlertCard : Border
{
    private const double IconSize = 16;
    private const double CloseSize = 9;
    private static readonly Brush RedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));          // the Mac's red
    private static readonly Brush GreyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));        // the Mac's .gray
    private static readonly Brush CardBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0x45, 0x3A)));  // red at 14 % (Mac Views.swift:358)
    private static readonly Brush PillBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x45, 0x3A)));  // red at 18 %
    private static readonly Brush PillHoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0x45, 0x3A)));  // red at 30 %
    private static readonly Brush CloseBackdrop = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));  // white at 10 % (Electron .xbtn)

    private readonly Action<string> _open;
    private readonly Action<string> _dismiss;
    private readonly TextBlock _title = new()
    {
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        Focusable = false,
    };
    private readonly TextBlock _detail = new()
    {
        FontSize = 11,
        Foreground = GreyBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 1, 0, 0),
        Focusable = false,
    };
    private readonly TextBlock _viewLogText = new()
    {
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = RedBrush,
        Focusable = false,
    };
    private readonly Border _viewLog;
    private string? _url;

    public AlertCard(Action<string> open, Action<string> dismiss)
    {
        _open = open;
        _dismiss = dismiss;

        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(10);
        Background = CardBrush;
        Focusable = false;

        _viewLog = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Background = PillBrush,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Focusable = false,
            Child = _viewLogText,
        };

        HideButton = new Button
        {
            Template = FlatCloseTemplate(),
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            Content = ToastView.GlyphOf("xmark", GreyBrush, CloseSize),
        };
        _viewLog.MouseEnter += (_, _) => ViewLogHover(true);
        _viewLog.MouseLeave += (_, _) => ViewLogHover(false);

        var grid = new Grid { Focusable = false };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new ContentControl
        {
            Width = IconSize,
            Height = IconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            Content = ToastView.GlyphOf("octagon", RedBrush, IconSize),
        };
        var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        texts.Children.Add(_title);
        texts.Children.Add(_detail);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(_viewLog, 2);
        Grid.SetColumn(HideButton, 3);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(_viewLog);
        grid.Children.Add(HideButton);
        Child = grid;

        HideButton.Click += OnHideClick;
        MouseLeftButtonUp += OnCardClick;
    }

    /// The alert's id (the key of the dismissal); null until the card is first updated.
    public string? Id { get; private set; }

    /// The × button that hides the alert.
    public Button HideButton { get; }

    /// True when the "View log" pill shows (the alert has a log).
    public bool ViewLogShown => _viewLog.Visibility == Visibility.Visible;

    /// The "View log" pill's background: red at 18 %, or 30 % while the pointer is over it.
    public Brush ViewLogBackground => _viewLog.Background;

    /// Sets the pill's background for the pointer over it (true) or not (false).
    public void ViewLogHover(bool over) => _viewLog.Background = over ? PillHoverBrush : PillBrush;

    /// Opens the alert's log, when it has one.
    public void OpenLog()
    {
        if (_url is { Length: > 0 } url)
        {
            _open(url);
        }
    }

    /// Sets the card to the alert, changing only the texts and the pill that differ.
    public void Update(JobAlert alert)
    {
        Id = alert.Id;
        _url = alert.Url;
        SetText(_title, L10n.L("CI failed: {0}", alert.Name));
        SetText(_detail, alert.Project + " · " + alert.Reason);
        SetText(_viewLogText, L10n.L("View log"));
        var visibility = string.IsNullOrEmpty(alert.Url) ? Visibility.Collapsed : Visibility.Visible;
        if (_viewLog.Visibility != visibility)
        {
            _viewLog.Visibility = visibility;
        }
        var tip = L10n.L("Hide alert");
        if (HideButton.ToolTip as string != tip)
        {
            HideButton.ToolTip = tip;
        }
        // the card's tooltip is the log's address, when it has one (Electron title = a.url)
        var cardTip = string.IsNullOrEmpty(alert.Url) ? null : alert.Url;
        if (ToolTip as string != cardTip)
        {
            ToolTip = cardTip;
        }
    }

    /// The round, flat × of the Electron .xbtn: a white 10 % backdrop with the glyph on it, and no system hover chrome.
    private static ControlTemplate FlatCloseTemplate()
    {
        var backdrop = new FrameworkElementFactory(typeof(Border));
        backdrop.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        backdrop.SetValue(Border.BackgroundProperty, CloseBackdrop);
        backdrop.SetValue(FrameworkElement.WidthProperty, 18.0);
        backdrop.SetValue(FrameworkElement.HeightProperty, 18.0);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        backdrop.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = backdrop };
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Id is { } id)
        {
            _dismiss(id);
        }
    }

    /// A click on the card opens the log, off its buttons; a click on the × is left to the button.
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsInsideButton(e.OriginalSource as DependencyObject))
        {
            OpenLog();
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

    private static void SetText(TextBlock text, string value)
    {
        if (text.Text != value)
        {
            text.Text = value;
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
