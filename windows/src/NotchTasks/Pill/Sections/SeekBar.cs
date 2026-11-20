using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill.Sections;

/// The seek bar of the Now Playing card (Mac SeekBar): 12 px high, a 4 px track with the fill in the tint. While the session can seek and
/// the pointer is over the bar, it thickens to 6 px, shows a 10 px white knob, and shows the elapsed and the remaining time under it
/// (m:ss and -m:ss, refreshed each second while the pointer is over it). The fill moves by a linear ScaleX animation (and the knob by a
/// matching X animation) from the predicted position to the end over the remaining time, at 30 fps. It restarts only when SeekClock
/// reports a rebase or the play state changed, and it stops while paused and when it leaves the screen. A press or drag sets the
/// position, and the release calls the seek.
public sealed class SeekBar : Grid
{
    private const double ThinTrack = 4;
    private const double ThickTrack = 6;
    private const double KnobSize = 10;
    private const double FrameRate = 30;

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush KnobBrush = Freeze(Brushes.White);
    private static readonly Brush TimeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));

    private readonly SeekClock _clock = new();
    private readonly Action<double> _seek;
    private readonly Rectangle _track;
    private readonly Rectangle _fill;
    private readonly ScaleTransform _scale = new(0, 1);
    private readonly TranslateTransform _knobShift = new();
    private readonly Ellipse _knob;
    private readonly TextBlock _elapsed;
    private readonly TextBlock _remaining;
    private readonly DispatcherTimer _labels = new() { Interval = TimeSpan.FromSeconds(1) };
    private double _duration;     // seconds; the shown session's
    private double _width;        // the bar's width in DIP, at the last layout
    private double _dragFraction;
    private bool _hasTime;        // the session has a duration and a position
    private bool _playing;
    private bool _animating;
    private bool _canSeek;
    private bool _hovered;
    private bool _dragging;

    /// seek gets the position, in seconds, when a press or drag is released.
    public SeekBar(Action<double> seek)
    {
        _seek = seek;
        Focusable = false;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _track = new Rectangle
        {
            Height = ThinTrack,
            Fill = TrackBrush,
            RadiusX = 2,
            RadiusY = 2,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Focusable = false,
        };
        _fill = new Rectangle
        {
            Height = ThinTrack,
            RadiusX = 2,
            RadiusY = 2,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _scale,
            IsHitTestVisible = false,
            Focusable = false,
        };
        _knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            Fill = KnobBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _knobShift,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Focusable = false,
        };
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_knob);

        var labels = new Grid { Margin = new Thickness(0, 2, 0, 0), IsHitTestVisible = false, Focusable = false };
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _elapsed = Label(HorizontalAlignment.Left);
        _remaining = Label(HorizontalAlignment.Right);
        Grid.SetColumn(_remaining, 1);
        labels.Children.Add(_elapsed);
        labels.Children.Add(_remaining);
        Grid.SetRow(labels, 1);
        Children.Add(labels);

        Cursor = Cursors.Arrow;
        MouseEnter += (_, _) => OnHover(true);
        MouseLeave += (_, _) => OnHover(false);
        SizeChanged += (_, _) => OnLaidOut();
        Loaded += (_, _) => OnShown();
        Unloaded += (_, _) => OnHidden();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                OnShown();
            }
            else
            {
                OnHidden();
            }
        };
        _labels.Tick += (_, _) =>
        {
            if (_hasTime && !_dragging)
            {
                SetLabels(_clock.At(NowMs()));
            }
        };
        ApplyHover();
    }

    /// Shows the session's position: it sets the fill and knob, the tint and the labels, and starts or stops the animation. Nothing
    /// is shown when the session has no duration or no position (the card hides the bar then).
    public void Update(NowPlaying shown, Color tint)
    {
        if (_fill.Fill is not SolidColorBrush current || current.Color != tint)
        {
            _fill.Fill = Freeze(new SolidColorBrush(tint));
        }
        _canSeek = shown.CanSeek;
        ToolTip = _canSeek ? L10n.L("Drag to seek") : null;
        ApplyHover();
        if (shown.Position is not { } position || shown.Duration is not { } duration)
        {
            _hasTime = false;
            StopAnimation();
            return;
        }

        _hasTime = true;
        _duration = duration;
        _width = ActualWidth;
        var playingChanged = shown.Playing != _playing;
        _playing = shown.Playing;
        _clock.Report(position, shown.Playing, duration, shown.PositionAtMs);
        if (_dragging)
        {
            return;
        }

        var now = NowMs();
        if (!shown.Playing)
        {
            Park(now);
        }
        else if (_clock.Rebased || playingChanged || !_animating)
        {
            Restart(now);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_canSeek || !_hasTime)
        {
            return;
        }
        _dragging = true;
        CaptureMouse();
        StopAnimation();
        Drag(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Drag(e);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        ReleaseMouseCapture();
        _seek(_dragFraction * _duration);
        e.Handled = true;
    }

    /// The pointer over the bar: thick and with the knob and labels when the session can seek, and the labels refresh each second.
    private void OnHover(bool over)
    {
        _hovered = over;
        ApplyHover();
        if (over && _canSeek && _hasTime)
        {
            SetLabels(_clock.At(NowMs()));
            _labels.Start();
        }
        else
        {
            _labels.Stop();
        }
    }

    private void ApplyHover()
    {
        var thick = _hovered && _canSeek;
        _track.Height = thick ? ThickTrack : ThinTrack;
        _fill.Height = thick ? ThickTrack : ThinTrack;
        _knob.Visibility = thick ? Visibility.Visible : Visibility.Collapsed;
        _elapsed.Opacity = thick ? 1 : 0;
        _remaining.Opacity = thick ? 1 : 0;
    }

    /// The bar was laid out at a new width: the knob and the fill follow it (the animation restarts at the new width).
    private void OnLaidOut()
    {
        if (!_hasTime || _dragging)
        {
            return;
        }
        var now = NowMs();
        if (_playing)
        {
            Restart(now);
        }
        else
        {
            Park(now);
        }
    }

    /// Shown again (the pill opened, or the card came back on screen): the fill resumes from the clock while playing.
    private void OnShown()
    {
        if (_hasTime && _playing && !_dragging)
        {
            Restart(NowMs());
        }
    }

    /// Off screen (the pill closed, or the card left): nothing moves and no label refreshes.
    private void OnHidden()
    {
        StopAnimation();
        _labels.Stop();
    }

    /// A press or drag: the fill and knob follow the pointer, and the labels show the position it names.
    private void Drag(MouseEventArgs e)
    {
        _width = ActualWidth;
        _dragFraction = _width > 0 ? Math.Clamp(e.GetPosition(this).X / _width, 0, 1) : 0;
        SetFraction(_dragFraction);
        SetLabels(_dragFraction * _duration);
    }

    /// The fill from the predicted position to the end, over the remaining time (linear, 30 fps). The fill's start is set first, so the
    /// bar shows the position while the animation waits for a width.
    private void Restart(long now)
    {
        StopAnimation();
        _width = ActualWidth;
        var at = _clock.At(now);
        var start = Fraction(at);
        SetFraction(start);
        SetLabels(at);
        var remaining = _duration - at;
        if (remaining <= 0 || _width <= 0)
        {
            return;
        }

        var duration = new Duration(TimeSpan.FromSeconds(remaining));
        var scale = new DoubleAnimation(start, 1, duration);
        Timeline.SetDesiredFrameRate(scale, (int)FrameRate);
        var shift = new DoubleAnimation(KnobX(start), KnobX(1), duration);
        Timeline.SetDesiredFrameRate(shift, (int)FrameRate);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _knobShift.BeginAnimation(TranslateTransform.XProperty, shift);
        _animating = true;
    }

    /// Paused: the fill and knob stay at the predicted position, and nothing animates.
    private void Park(long now)
    {
        StopAnimation();
        var at = _clock.At(now);
        SetFraction(Fraction(at));
        SetLabels(at);
    }

    private void StopAnimation()
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _knobShift.BeginAnimation(TranslateTransform.XProperty, null);
        _animating = false;
    }

    /// Sets the fill's and knob's place, with no animation (the animation is stopped first).
    private void SetFraction(double fraction)
    {
        _scale.ScaleX = fraction;
        _knobShift.X = KnobX(fraction);
    }

    /// The knob's X at a fraction: its centre sits on the position.
    private double KnobX(double fraction) => fraction * _width - KnobSize / 2;

    private double Fraction(double seconds) => _duration > 0 ? Math.Clamp(seconds / _duration, 0, 1) : 0;

    private void SetLabels(double seconds)
    {
        _elapsed.Text = TimerState.Mmss(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        _remaining.Text = "-" + TimerState.Mmss(TimeSpan.FromSeconds(Math.Max(0, _duration - seconds)));
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static TextBlock Label(HorizontalAlignment alignment) => new()
    {
        FontSize = 10,
        Foreground = TimeBrush,
        HorizontalAlignment = alignment,
        Opacity = 0,
        IsHitTestVisible = false,
        Focusable = false,
    };

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
