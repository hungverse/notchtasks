using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Temperatures pane (the Mac's Fans & temperature without the fans). Read-only: it shows the zones, the hottest first, with the
/// hottest in its tint at the top. The zones come from the thermal counter when it reads here (no administrator; the pane says so and
/// offers no button). Otherwise they come from the temperature helper: while the helper is not On, the pane says why and offers the one
/// opt-in (the Permissions pane's Turn on, with the sign-in check box); nothing is read from the helper then. While the pane is shown,
/// the zones are read every 2 s (while the counter or the helper gives them), and the timer stops when the pane is hidden.
public partial class TemperaturesPane : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _store;
    private readonly SensorHelper _helper;
    private readonly IThermalCounter _counter;
    private readonly Func<DependencyObject, bool> _consent;
    private readonly DispatcherTimer _poll;
    private bool _reading;
    private bool _counterReads;  // the counter gave a plausible reading at its last read: the pane reads without administrator
    private IReadOnlyList<ThermalReading> _readings = Array.Empty<ThermalReading>();

    /// The pane for the settings store (the sign-in check box's stored value), the temperature helper and the thermal counter. It is read
    /// only while it is shown. consent asks the Turn on confirmation (tests pass their own); Cancel starts nothing.
    public TemperaturesPane(SettingsStore store, SensorHelper helper, IThermalCounter counter, Func<DependencyObject, bool>? consent = null)
    {
        _store = store;
        _helper = helper;
        _counter = counter;
        _consent = consent ?? TemperatureConsent.Ask;
        InitializeComponent();
        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += (_, _) => _ = ReadAsync();

        HottestGlyph.SetNamed("thermometer");  // the toast's glyph, in its family
        TurnOnButton.Click += (_, _) => TurnOnFromClick(SignInCheck.IsChecked == true);
        ApplyTexts();

        Loaded += OnLoaded;
        Unloaded += OnPaneHidden;
    }

    /// Every text of the pane in the current language. Run when the pane is made and when the language changes.
    private void ApplyTexts()
    {
        SignInCheck.Content = L10n.L("Start with Windows without asking again");
        CounterNote.Text = L10n.L("Read without administrator");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _helper.StateChanged += OnHelperChanged;
        L10n.Changed += OnLanguageChanged;
        ApplyTexts();
        Render();
        _ = ReadAsync();  // the counter first: it says whether the pane reads without administrator
    }

    private void OnPaneHidden(object sender, RoutedEventArgs e)
    {
        _helper.StateChanged -= OnHelperChanged;
        L10n.Changed -= OnLanguageChanged;
        _poll.Stop();
    }

    /// The helper turned on, off or declined: the pane follows it while it shows. Off, the helper's readings are dropped.
    private void OnHelperChanged()
    {
        if (_helper.State != HelperState.On)
        {
            _readings = Array.Empty<ThermalReading>();
        }
        Render();
        _ = ReadAsync();
    }

    private void OnLanguageChanged()
    {
        ApplyTexts();
        Render();
    }

    /// Reads the zones once: the counter, and the helper when the counter gives no plausible reading and the helper is On. One read at a
    /// time; a read that ends after the pane hid changes nothing. The timer runs while either source gives zones.
    private async Task ReadAsync()
    {
        if (_reading)
        {
            return;
        }
        _reading = true;
        try
        {
            var counter = await _counter.ReadAsync();
            var counterReads = Thermals.Plausible(counter).Count > 0;
            var fromHelper = _helper.State == HelperState.On ? await _helper.ReadAsync() : Array.Empty<ThermalReading>();
            if (!IsLoaded)
            {
                return;
            }
            // The helper's readings come first while it is On (its CPU reading); the counter's otherwise.
            var helperReads = Thermals.Plausible(fromHelper).Count > 0;
            _counterReads = counterReads;
            _readings = helperReads ? fromHelper : counter;
            var reading = counterReads || _helper.State == HelperState.On;
            if (reading && !_poll.IsEnabled)
            {
                _poll.Start();
            }
            else if (!reading)
            {
                _poll.Stop();
            }
            Render();
        }
        finally
        {
            _reading = false;
        }
    }

    /// The opt-in as the Permissions pane offers it: Turn on from Off, Try again from Declined or Paused. Nothing else starts it.
    private async void TurnOnFromClick(bool atSignIn)
    {
        if (!_consent(this))
        {
            return;  // Cancel: nothing starts
        }
        try
        {
            await _helper.TurnOnAsync(atSignIn);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            ErrorLog.Write(e);
        }
        Render();
    }

    /// Shows the readings while a source gives them (the counter, or the helper when On), and the help card otherwise. The counter's
    /// readings need no button: the card is not shown then.
    private void Render()
    {
        var on = _counterReads || _helper.State == HelperState.On;
        CounterNote.Visibility = VisibleIf(_counterReads && _helper.State != HelperState.On);
        HelperCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        HottestCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SensorsCaps.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SensorsCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            RenderReadings();
            return;
        }

        (string? title, string? button, bool signIn) = _helper.State switch
        {
            HelperState.Off => (L10n.L("CPU temperature") + " · " + L10n.L("Needs administrator"), L10n.L("Turn on"), true),
            HelperState.Declined => (L10n.L("Not turned on: administrator was not allowed"), L10n.L("Try again"), true),
            HelperState.Paused => (L10n.L("Turn on again to read temperatures this session"), L10n.L("Try again"), true),
            HelperState.Failed => (L10n.L("The temperature helper did not start"), L10n.L("Try again"), true),
            HelperState.Unavailable => (L10n.L("This PC does not report temperatures"), null, false),
            HelperState.Starting => (L10n.L("Checking…"), null, false),
            _ => (null, null, false),
        };
        HelperTitle.Text = title ?? string.Empty;
        HelperTitle.Visibility = VisibleIf(title is not null);
        // The opt-in note explains the choice the Off state offers; the other titles say their own thing.
        HelperNote.Text = L10n.L("Optional. Reading temperatures on Windows needs administrator, so a small helper runs with administrator rights only if you turn this on.");
        HelperNote.Visibility = VisibleIf(_helper.State == HelperState.Off);
        TurnOnButton.Content = button ?? string.Empty;
        TurnOnButton.Visibility = VisibleIf(button is not null);
        SignInCheck.Visibility = VisibleIf(signIn);
        SignInCheck.IsChecked = _store.SensorHelperAtSignIn;
        HelperActions.Visibility = VisibleIf(signIn);
    }

    /// The hottest reading in its tint, and every plausible zone as a row, hottest first.
    private void RenderReadings()
    {
        var zones = Thermals.Plausible(_readings);
        var hottest = Thermals.Hottest(zones);
        if (hottest is { } top)
        {
            HottestValue.Text = $"{Math.Round(top.Celsius, MidpointRounding.AwayFromZero):0}°";
            HottestValue.Foreground = ToBrush(Thermals.Tint(top.Celsius));
            HottestGlyph.Foreground = ToBrush(Thermals.Tint(top.Celsius));
            HottestName.Text = top.Name;
        }
        else
        {
            HottestValue.Text = "—";
            // No reading: the dash and the glyph take the theme's secondary text brush, not a fixed grey.
            HottestValue.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            HottestGlyph.SetResourceReference(FluentGlyph.ForegroundProperty, "TextFillColorSecondaryBrush");
            HottestName.Text = L10n.L("Could not read temperature sensors.");
        }

        SensorsCaps.Text = $"{L10n.L("Sensors")} ({zones.Count})";
        SensorRows.Children.Clear();
        for (var i = 0; i < zones.Count; i++)
        {
            if (i > 0)
            {
                SensorRows.Children.Add(new Separator { Margin = new Thickness(16, 0, 16, 0) });
            }
            SensorRows.Children.Add(BuildRow(zones[i]));
        }
    }

    /// One zone: its name on the left, its degrees on the right, in a SettingRow (48 DIP).
    private UIElement BuildRow(ThermalReading zone)
    {
        var row = new DockPanel { Style = (Style)FindResource("SettingRow") };
        var value = new TextBlock
        {
            Text = zone.Celsius.ToString("F1", CultureInfo.CurrentCulture) + "°",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(new TextBlock
        {
            Text = zone.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = zone.Name,
        });
        return row;
    }

    private static Visibility VisibleIf(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static Brush ToBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
