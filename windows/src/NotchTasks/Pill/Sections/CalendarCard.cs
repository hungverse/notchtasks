using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Calendars;
using NotchTasks.Services;

namespace NotchTasks.Pill.Sections;

/// The calendar card in the open pill (Mac EventList): the all-day events, then the next timed event as the main row with its status
/// and a progress line while it runs, then up to three more rows, and "+N more events". A title opens Calendar and closes the pill, Join
/// opens the meeting link, and the x hides the event. With the store unavailable it shows the one-line hint instead. Update rebuilds the
/// rows from the module, so the card follows each publish. One element kept for the pill's life.
public sealed class CalendarCard : Border
{
    /// The timed rows after the main one (the Mac's NotchView.rows).
    private const int Rows = 3;
    /// Opens the Windows Calendar app. It shows the calendar, not the one event: the store does not give a link to an appointment.
    private const string CalendarApp = "outlookcal:";
    /// Under this many seconds to the start, the status reads in the event's colour (the Mac's 600 s rule).
    private const double SoonSeconds = 600;

    private static readonly Color GreyColor = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color GreenColor = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color DefaultColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly FontFamily TextFamily = new("Segoe UI Variable Text, Segoe UI");

    private readonly StackPanel _rows = new() { Focusable = false };
    private AppState? _state;

    public CalendarCard()
    {
        Focusable = false;
        Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));  // the Mac's white 6 %
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(10);
        Child = _rows;
    }

    /// Raised when an event's title opens Calendar: the pill closes, as the Mac's open() collapses it.
    public event Action? OpenRequested;

    /// Refreshes the card from the calendar module and returns true. Returns false, and leaves the card empty, when there is no event
    /// to list and no hint to show.
    public bool Update(AppState state)
    {
        var calendar = state.Calendar;
        var visible = calendar.Visible;
        if (!calendar.Unavailable && visible.Count == 0)
        {
            return false;
        }
        _state = state;
        _rows.Children.Clear();
        if (calendar.Unavailable)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = L10n.L("Calendar can't be opened. Check Settings > Privacy & security > Calendar."),
                Foreground = new SolidColorBrush(GreyColor),
                FontFamily = TextFamily,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            return true;
        }

        var now = DateTimeOffset.Now;
        var allDay = visible.Where(e => e.AllDay).ToList();
        var timed = visible.Where(e => !e.AllDay).ToList();
        foreach (var e in allDay)
        {
            _rows.Children.Add(AllDayRow(e));
        }
        if (allDay.Count > 0 && timed.Count > 0)
        {
            _rows.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
        if (timed.Count > 0)
        {
            _rows.Children.Add(MainRow(timed[0], now));
        }
        foreach (var e in timed.Skip(1).Take(Rows))
        {
            _rows.Children.Add(TimedRow(e, now));
        }
        if (timed.Count > Rows + 1)
        {
            _rows.Children.Add(Text(L10n.L("+{0} more events", timed.Count - Rows - 1), 10, Grey()));
        }
        return true;
    }

    /// An all-day event: the sun, "All day", the title, its calendar and how many days it runs.
    private FrameworkElement AllDayRow(CalendarEvent e)
    {
        var tint = Tint(e.Color);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        left.Children.Add(Glyph("sun", 10, new SolidColorBrush(tint), 15));
        left.Children.Add(Text(L10n.L("All day"), 11, Grey(), FontWeights.SemiBold, margin: new Thickness(4, 0, 6, 0)));
        left.Children.Add(Link(Text(e.Title, 12, Brushes.White, FontWeights.Medium), e, OpenCalendar));
        var detail = string.Join(" · ", new[] { e.Calendar, UntilText(e) }.Where(s => !string.IsNullOrEmpty(s)));
        if (detail.Length > 0)
        {
            left.Children.Add(Text(" " + detail, 10, Grey()));
        }
        return Line(e, left);
    }

    /// The next timed event: its colour bar, its title, the span and status, a progress line while it runs, and its Join button.
    private FrameworkElement MainRow(CalendarEvent e, DateTimeOffset now)
    {
        var tint = Tint(e.Color);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Link(Text(e.Title, 13, Brushes.White, FontWeights.SemiBold), e, OpenCalendar));
        text.Children.Add(StatusLine(e, now, tint, 11));
        if (e.Running(now))
        {
            text.Children.Add(Progress(e.Elapsed(now), tint));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bar = new Border
        {
            Width = 4,
            Height = 40,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(tint),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(bar);
        grid.Children.Add(text);
        if (e.Link is { } link)
        {
            var join = JoinButton(link);
            Grid.SetColumn(join, 2);
            grid.Children.Add(join);
        }
        var close = CloseButton(e, 10);
        Grid.SetColumn(close, 3);
        grid.Children.Add(close);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(grid);
        return row;
    }

    /// A later timed event: its dot, its start time, its title, its status, and a video icon when it has a link.
    private FrameworkElement TimedRow(CalendarEvent e, DateTimeOffset now)
    {
        var tint = Tint(e.Color);
        var running = e.Running(now);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        left.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Stroke = new SolidColorBrush(tint),
            StrokeThickness = 1.2,
            Fill = running ? new SolidColorBrush(tint) : Brushes.Transparent,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(Text(e.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), 11, Grey(), FontWeights.SemiBold, margin: new Thickness(0, 0, 8, 0)));
        left.Children.Add(Link(Text(e.Title, 12, new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))), e, OpenCalendar));
        left.Children.Add(Text(running ? L10n.L("happening now") : L10n.L("in ") + Ago.Text((e.Start - now).TotalSeconds), 10, Grey(), margin: new Thickness(8, 0, 0, 0)));
        if (e.Link is { } link)
        {
            left.Children.Add(VideoButton(link));
        }
        return Line(e, left);
    }

    /// A row with the close button docked at its right, and its content filling the rest.
    private FrameworkElement Line(CalendarEvent e, FrameworkElement content)
    {
        var row = new DockPanel();
        var close = CloseButton(e, 8);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(content);
        return row;
    }

    /// The status under a timed event's title: its start and end ("9:00–10:00"), then "Happening now · 25 min left" or "Coming up · in 5 min".
    private static TextBlock StatusLine(CalendarEvent e, DateTimeOffset now, Color tint, double size)
    {
        var running = e.Running(now);
        var status = running
            ? L10n.L("Happening now · {0} left", Ago.Text((e.End - now).TotalSeconds))
            : L10n.L("Coming up · in {0}", Ago.Text((e.Start - now).TotalSeconds));
        var soon = running || (e.Start - now).TotalSeconds < SoonSeconds;
        var span = e.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) + "–" + e.End.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        return Text(span + " · " + status, size, soon ? new SolidColorBrush(tint) : Grey());
    }

    /// The progress line of a running event: its share done, in the event's colour.
    private static FrameworkElement Progress(double fraction, Color tint)
    {
        var done = new Grid();
        done.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.001), GridUnitType.Star) });
        done.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.001), GridUnitType.Star) });
        var fill = new Border { Background = new SolidColorBrush(tint), CornerRadius = new CornerRadius(1.5) };
        Grid.SetColumn(fill, 0);
        done.Children.Add(fill);

        var progress = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 0) };
        progress.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(1.5) });
        progress.Children.Add(done);
        return progress;
    }

    /// The Join button: a green capsule with the video icon and the word, which opens the meeting link.
    private FrameworkElement JoinButton(string link)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(Glyph("video", 11, Brushes.White, 13));
        content.Children.Add(Text(L10n.L("Join"), 11, Brushes.White, FontWeights.SemiBold, margin: new Thickness(4, 0, 0, 0)));
        var capsule = new Border
        {
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, GreenColor.R, GreenColor.G, GreenColor.B)),
            Margin = new Thickness(8, 0, 6, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        capsule.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            ItemCard.OpenUrl(link);
        };
        return capsule;
    }

    /// The small video icon of a later row: the same as Join, without the word.
    private FrameworkElement VideoButton(string link)
    {
        var icon = Glyph("video", 10, new SolidColorBrush(GreenColor), 14);
        icon.Margin = new Thickness(8, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Cursor = Cursors.Hand;
        icon.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            ItemCard.OpenUrl(link);
        };
        return icon;
    }

    /// The x that hides the event from the notch.
    private FrameworkElement CloseButton(CalendarEvent e, double size)
    {
        var box = new Border
        {
            Padding = new Thickness(4),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = ToastView.GlyphOf("xmark", new SolidColorBrush(GreyColor), size),
            ToolTip = L10n.L("Hide this event from the notch"),
        };
        box.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            _state?.DismissCalendarEvent(e.Id);
        };
        return box;
    }

    /// A text that opens an event in Calendar when clicked, and says so in its tooltip.
    private static TextBlock Link(TextBlock text, CalendarEvent e, Action open)
    {
        text.Cursor = Cursors.Hand;
        text.ToolTip = e.Calendar.Length == 0 ? L10n.L("Open in Calendar") : L10n.L("{0} · open in Calendar", e.Calendar);
        text.MouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            open();
        };
        return text;
    }

    /// Opens the Calendar app, then closes the pill.
    private void OpenCalendar()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(CalendarApp) { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            ErrorLog.Write(e);
        }
        OpenRequested?.Invoke();
    }

    /// "until 6/11" for an all-day event that runs past its first day, else empty (the Mac's untilText).
    private static string UntilText(CalendarEvent e) =>
        e.AllDay && e.SpanDays >= 1 ? L10n.L("until ") + e.LastDay.ToString("d/M", CultureInfo.CurrentCulture) : string.Empty;

    private static Color Tint(string hex) =>
        HexColor.TryNormalize(hex, out var normalized) ? HexColor.ToColor(normalized) : DefaultColor;

    private static Brush Grey() => new SolidColorBrush(GreyColor);

    private static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        FontFamily = TextFamily,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = margin ?? new Thickness(0),
    };

    /// A glyph from the Fluent font in a brush, `box` DIPs square with the glyph centred (the Mac's symbol sizes).
    private static TextBlock Glyph(string name, double size, Brush brush, double box)
    {
        var (family, glyph) = ModuleGlyphs.Get(name);
        return new TextBlock
        {
            Text = glyph,
            FontFamily = family,
            FontSize = size,
            Foreground = brush,
            Width = box,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
