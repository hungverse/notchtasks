using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The Temperatures pane: read-only. It shows the helper's readings, hottest first, while it is shown and the helper is On. When the
/// helper is off, it says why and offers the one opt-in button. The helper is built over fakes (no UAC, no pipe, no stop event).
public sealed class TemperaturesPaneTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class FakePipe : ISensorPipe
    {
        public IReadOnlyList<ThermalReading>? Answer;

        public Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct) => Task.FromResult(Answer);
    }

    private sealed class NoLaunch : IProcessLauncher
    {
        public Task<Process?> RunAsAsync(string fileName, string arguments) => Task.FromResult<Process?>(null);
    }

    private sealed class NoStop : IHelperStop
    {
        public bool Signal() => false;
    }

    /// The text of every button under the element.
    private static IEnumerable<string> ButtonTexts(DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button { Content: string text } && text.Length > 0)  // an unused button has no text
            {
                yield return text;
            }
            foreach (var nested in ButtonTexts(child))
            {
                yield return nested;
            }
        }
    }

    /// The text of every text block under the element, in order.
    private static IEnumerable<string> TextsOf(DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock { Text: var text } && !string.IsNullOrEmpty(text))
            {
                yield return text;
            }
            foreach (var nested in TextsOf(child))
            {
                yield return nested;
            }
        }
    }

    /// A helper in the given state over a fake pipe with these readings, and a pane for it.
    private static (SensorHelper Helper, TemperaturesPane Pane) NewPane(HelperState state, IReadOnlyList<ThermalReading>? readings, bool marker = false)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var pipe = new FakePipe { Answer = readings };
        var markerPath = SensorNames.MarkerPath(Sid);
        var helper = new SensorHelper(settings, new NoLaunch(), pipe, new NoStop(), "NotchTasksSensors.exe", Sid, 1,
            path => marker && path == markerPath);
        Fixture.Call(helper, "SetState", state);
        return (helper, new TemperaturesPane(settings, helper));
    }

    /// Shows the pane off screen (its Loaded runs, so an On helper is read) and lays it out.
    private static Window Show(TemperaturesPane pane)
    {
        var window = new Window { Content = pane };
        Fixture.ShowOffScreen(window);
        window.UpdateLayout();
        return window;
    }

    [Theory]
    [InlineData(HelperState.Off, "Turn on")]
    [InlineData(HelperState.Declined, "Try again")]
    [InlineData(HelperState.Paused, "Try again")]
    public void HelperNotOn_ExplainsAndOffersTheOptIn(HelperState state, string button)
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (_, pane) = NewPane(state, null);
            var window = Show(pane);
            try
            {
                Assert.Contains(L10n.L(button), ButtonTexts(pane));
                Assert.Equal(Visibility.Visible, Fixture.Named<Border>(pane, "HelperCard").Visibility);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<Border>(pane, "SensorsCard").Visibility);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<Border>(pane, "HottestCard").Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Unavailable_ExplainsWithoutAButton()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (_, pane) = NewPane(HelperState.Unavailable, null);
            var window = Show(pane);
            try
            {
                Assert.Empty(ButtonTexts(pane));
                Assert.Contains(L10n.L("This PC does not report temperatures"), TextsOf(pane));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Off_OffersTheOptInWithTheSignInCheckBox()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (helper, pane) = NewPane(HelperState.Off, null);
            var window = Show(pane);
            try
            {
                // The pane only offers the opt-in: the click is the Permissions pane's (TurnOnAsync), and nothing starts by itself.
                Assert.Equal(HelperState.Off, helper.State);
                Assert.Equal(Visibility.Visible, Fixture.Named<CheckBox>(pane, "SignInCheck").Visibility);
                Assert.Equal(Visibility.Visible, Fixture.Named<Button>(pane, "TurnOnButton").Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void On_ListsTheReadings_HottestFirst_WithTheHottestInTheHeader()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (_, pane) = NewPane(HelperState.On, new[]
            {
                new ThermalReading("TZ00", 55),
                new ThermalReading("CPU Package", 71.5),
                new ThermalReading("Bogus", 130),
            });
            var window = Show(pane);
            try
            {
                Assert.Equal(Visibility.Collapsed, Fixture.Named<Border>(pane, "HelperCard").Visibility);
                Assert.Equal(Visibility.Visible, Fixture.Named<Border>(pane, "SensorsCard").Visibility);
                Assert.Equal(Visibility.Visible, Fixture.Named<Border>(pane, "HottestCard").Visibility);

                var rows = Fixture.Named<StackPanel>(pane, "SensorRows");
                var grids = rows.Children.OfType<DockPanel>().ToList();
                Assert.Equal(2, grids.Count);  // the implausible 130 C is dropped
                var row = grids[0];  // a SettingRow: 32 DIP of controls between 8 DIP of margin above and below: 48 DIP in all
                Assert.Equal(48, row.ActualHeight + row.Margin.Top + row.Margin.Bottom, 0);
                Assert.Contains("CPU Package", TextsOf(grids[0]));
                Assert.Contains("TZ00", TextsOf(grids[1]));

                Assert.Equal("72°", Fixture.Named<TextBlock>(pane, "HottestValue").Text);
                Assert.Equal("CPU Package", Fixture.Named<TextBlock>(pane, "HottestName").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void On_IsReadWhileShown_AndTheReadingTimerStopsWhenHidden()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (_, pane) = NewPane(HelperState.On, new[] { new ThermalReading("CPU", 50) });
            var poll = Fixture.Field<DispatcherTimer>(pane, "_poll");
            Assert.False(poll.IsEnabled);  // not shown yet: nothing is read

            var window = Show(pane);
            try
            {
                Assert.True(poll.IsEnabled);
                Fixture.Call(pane, "OnPaneHidden", pane, new RoutedEventArgs());  // what the pane's Unloaded does when it leaves the window
                Assert.False(poll.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Off_ReadsNothing_AndRunsNoTimer()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (_, pane) = NewPane(HelperState.Off, new[] { new ThermalReading("CPU", 50) });
            var poll = Fixture.Field<DispatcherTimer>(pane, "_poll");
            var window = Show(pane);
            try
            {
                Assert.False(poll.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
