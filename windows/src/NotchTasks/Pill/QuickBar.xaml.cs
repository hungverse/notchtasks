using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;

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
        ShelfLink.Click += (_, _) => _state?.PickShelfFiles();
        MirrorButton.Click += (_, _) => _state?.OpenMirror();
    }

    /// The shelf's tray glyph (Segoe Fluent Icons E8B7, the folder the shelf uses).
    private const string ShelfGlyphText = "";

    /// The gear: open the Control Panel.
    public event Action? ControlPanelRequested;

    /// The power button: quit the app.
    public event Action? QuitRequested;

    /// Brings the bar in line with the app state: the timer's glyph and pills show while the timer module is on and no timer
    /// runs. The pill window calls this on every published change.
    public void Update(AppState state)
    {
        _state = state;
        TimerGlyph.Text = ModuleGlyphs.Get("timer");
        var show = state.Snapshot.ActiveModules.Contains("timer") && state.Timer.State is null;
        TimerBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The Mirror pill: while the mirror module is on and the mirror is closed. It follows the timer's pills, with a gap when they show.
        MirrorGlyph.Text = ModuleGlyphs.Get("camera");
        MirrorLabel.Text = L10n.L("Mirror");
        MirrorButton.ToolTip = L10n.L("Quick camera check");
        MirrorButton.Margin = new Thickness(show ? 8 : 0, 0, 0, 0);
        MirrorButton.Visibility = state.Snapshot.ActiveModules.Contains("mirror") && !state.MirrorOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        // The shelf's link: while the shelf is on, empty and no file is dragged over the pill (the card shows then).
        var shelfLink = state.Snapshot.ActiveModules.Contains("shelf") && state.Shelf.Items.Count == 0 && !state.ShelfTargeted;
        ShelfGlyph.Text = ShelfGlyphText;
        ShelfLabel.Text = L10n.L("Drag and drop or choose files");
        ShelfLink.ToolTip = L10n.L("Drag files onto the notch, or click to choose files for the shelf");
        ShelfLink.Visibility = shelfLink ? Visibility.Visible : Visibility.Collapsed;
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
