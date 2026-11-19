using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Notch;
using NotchTasks.Services;
using NotchTasks.Services.Modules;

namespace NotchTasks.Pill.Sections;

/// The live call's card in the open pill (Mac CallCard). One element kept for the pill's life, so a click on its buttons is not
/// lost when the pill rebuilds its sections. Update refreshes it from the sensors on every publish; the sensors source publishes
/// once a second while a call is live, which keeps the time current without a clock of the card's own.
public partial class CallCard : UserControl
{
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private AppState? _state;

    public CallCard()
    {
        InitializeComponent();
    }

    /// Raised after Open is clicked: the pill closes (the Mac's openCallApp collapses the pill).
    public event Action? OpenRequested;

    /// Refreshes the card from the live call and returns true. Returns false, and leaves the card alone, when no call is on.
    public bool Update(AppState state)
    {
        if (state.Sensors.Call is not { } call)
        {
            return false;
        }
        _state = state;
        CallGlyph.Text = ModuleGlyphs.Get(call.Video ? "video" : "phone");
        TitleText.Text = call.Video ? L10n.L("Video call · {0}", call.Name) : L10n.L("On a call · {0}", call.Name);
        MuteGlyph.Text = ModuleGlyphs.Get(call.Muted ? "micoff" : "mic");
        MuteCaption.Text = call.Muted ? L10n.L("Unmute") : L10n.L("Mute");
        MuteCircle.Background = new SolidColorBrush(call.Muted ? Red : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        RefreshTime();
        return true;
    }

    /// The time so far, and " · Mic muted" while muted, in green (red while muted).
    private void RefreshTime()
    {
        if (_state?.Sensors.Call is not { } call)
        {
            return;
        }
        var elapsed = TimerState.Mmss(DateTimeOffset.Now - call.Since);
        TimeText.Text = call.Muted ? elapsed + L10n.L(" · Mic muted") : elapsed;
        TimeText.Foreground = new SolidColorBrush(ChipView.CallTimeTint(call.Muted, _state.Settings.ModuleColors));
    }

    /// Mute or unmute the mic, then take the sensors' reading again.
    private void OnMute(object sender, RoutedEventArgs e)
    {
        if (_state is not { } state)
        {
            return;
        }
        state.ToggleCallMute();
    }

    /// Restores the call app's window, then closes the pill. The click is the user's own, so the window may take focus.
    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_state?.Sensors.Call is not { } call)
        {
            return;
        }
        CallOpener.Open(call.App);
        OpenRequested?.Invoke();
    }
}
