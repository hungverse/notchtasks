using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Pill.Sections;

namespace NotchTasks.Pill;

/// The open pill's quick bar: the timer's quick-start pills at the left, and the Control Panel and Quit buttons at the right.
/// Its buttons never take the focus: the pill window does not activate.
public partial class QuickBar : UserControl
{
    private AppState? _state;

    public QuickBar()
    {
        InitializeComponent();
        ControlPanelButton.Click += (_, _) => ControlPanelRequested?.Invoke();
        QuitButton.Click += (_, _) => QuitRequested?.Invoke();
        foreach (var pill in new[] { Timer5, Timer15, Timer25, Timer45 })
        {
            pill.Click += OnTimerPill;
        }
    }

    /// The gear: open the Control Panel.
    public event Action? ControlPanelRequested;

    /// The power button: quit the app.
    public event Action? QuitRequested;

    /// Brings the bar in line with the app state: the timer's glyph and pills show while the timer module is on and no timer
    /// runs. The glyph takes the timer's tint, as the card does. The pill window calls this on every published change.
    public void Update(AppState state, Color accent)
    {
        _state = state;
        TimerGlyph.Text = ModuleGlyphs.Get("timer");
        TimerGlyph.Foreground = new SolidColorBrush(TimerCard.TintOf(state, accent));
        var show = state.Snapshot.ActiveModules.Contains("timer") && state.Timer.State is null;
        TimerBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// A quick-start pill: starts a timer of its minutes (the pill's Tag), then publishes the change.
    private void OnTimerPill(object sender, RoutedEventArgs e)
    {
        if (_state is not { } state || sender is not Button { Tag: string text } || !int.TryParse(text, out var minutes))
        {
            return;
        }
        state.Timer.Start(minutes, DateTimeOffset.Now);
        state.Publish();
    }
}
