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

/// The Temperatures pane (the Mac's Fans & temperature without the fans). Read-only: it shows the temperature helper's zones, the
/// hottest first, with the hottest in its tint at the top. While the helper is not On, it says why and offers the one opt-in
/// (the Permissions pane's Turn on, with the sign-in check box); nothing is read then. While the pane is shown and the helper is On,
/// it reads the zones every 2 s, and the timer stops when the pane is hidden.
public partial class TemperaturesPane : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _store;
    private readonly SensorHelper _helper;
    private readonly DispatcherTimer _poll;
    private bool _reading;
    private IReadOnlyList<ThermalReading> _readings = Array.Empty<ThermalReading>();

    /// The pane for the settings store (the sign-in check box's stored value) and the temperature helper. It is read only while it is shown.
    public TemperaturesPane(SettingsStore store, SensorHelper helper)
    {
        _store = store;
        _helper = helper;
        InitializeComponent();
        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += (_, _) => _ = ReadAsync();

        HottestGlyph.Text = ModuleGlyphs.Get("thermometer").Glyph;  // the toast's glyph, in the Fluent regular font
        TurnOnButton.Click += (_, _) => TurnOnFromClick(SignInCheck.IsChecked == true);
        ApplyTexts();

        Loaded += OnLoaded;
        Unloaded += OnPaneHidden;
    }

    /// Every text of the pane in the current language. Run when the pane is made and when the language changes.
    private void ApplyTexts()
    {
        SignInCheck.Content = L10n.L("Start with Windows without asking again");
        SensorsCaps.Text = L10n.L("Sensors").ToUpper(CultureInfo.CurrentUICulture);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _helper.StateChanged += OnHelperChanged;
        L10n.Changed += OnLanguageChanged;
        ApplyTexts();
        if (_helper.State == HelperState.On)
        {
            _poll.Start();
            _ = ReadAsync();
        }
        Render();
    }

    private void OnPaneHidden(object sender, RoutedEventArgs e)
    {
        _helper.StateChanged -= OnHelperChanged;
        L10n.Changed -= OnLanguageChanged;
        _poll.Stop();
    }

    /// The helper turned on, off or declined: the pane follows it while it shows. Off, the readings are dropped.
    private void OnHelperChanged()
    {
        if (_helper.State == HelperState.On)
        {
            _poll.Start();
            _ = ReadAsync();
        }
        else
        {
            _poll.Stop();
            _readings = Array.Empty<ThermalReading>();
        }
        Render();
    }

    private void OnLanguageChanged()
    {
        ApplyTexts();
        Render();
    }

    /// Reads the zones once. One read at a time; a read that ends after the helper stopped, or after the pane hid, changes nothing.
    private async Task ReadAsync()
    {
        if (_reading || _helper.State != HelperState.On)
        {
            return;
        }
        _reading = true;
        try
        {
            var readings = await _helper.ReadAsync();
            if (!IsLoaded || _helper.State != HelperState.On)
            {
                return;
            }
            _readings = readings;
            RenderReadings();
        }
        finally
        {
            _reading = false;
        }
    }

    /// The opt-in as the Permissions pane offers it: Turn on from Off, Try again from Declined or Paused. Nothing else starts it.
    private async void TurnOnFromClick(bool atSignIn)
    {
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

    /// Shows the help card while the helper is not On, and the readings while it is.
    private void Render()
    {
        var on = _helper.State == HelperState.On;
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
            HelperState.Off => (null, L10n.L("Turn on"), true),
            HelperState.Declined => (L10n.L("Not turned on: administrator was not allowed"), L10n.L("Try again"), true),
            HelperState.Paused => (L10n.L("Turn on again to read temperatures this session"), L10n.L("Try again"), true),
            HelperState.Unavailable => (L10n.L("This PC does not report temperatures"), null, false),
            HelperState.Starting => (L10n.L("Checking…"), null, false),
            _ => (null, null, false),
        };
        HelperTitle.Text = title ?? string.Empty;
        HelperTitle.Visibility = VisibleIf(title is not null);
        HelperNote.Text = L10n.L("Optional. Reading temperatures on Windows needs administrator, so a small helper runs with administrator rights only if you turn this on.");
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
            HottestValue.Foreground = ToBrush("#808080");
            HottestGlyph.Foreground = ToBrush("#808080");
            HottestName.Text = L10n.L("Could not read temperature sensors.");
        }

        SensorsCaps.Text = $"{L10n.L("Sensors").ToUpper(CultureInfo.CurrentUICulture)} ({zones.Count})";
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
