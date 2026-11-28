using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The Permissions pane's Temperatures row while the thermal counter reads here: it says the readings are read without administrator,
/// and offers no Turn on (no UAC). The helper is built over fakes.
public sealed class PermissionsPaneCounterTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class NoLaunch : IProcessLauncher
    {
        public Task<Process?> RunAsAsync(string fileName, string arguments) => Task.FromResult<Process?>(null);
    }

    private sealed class NoPipe : ISensorPipe
    {
        public Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ThermalReading>?>(null);
    }

    private sealed class NoStop : IHelperStop
    {
        public bool Signal() => false;
    }

    private sealed class FakeCounter : IThermalCounter
    {
        public IReadOnlyList<ThermalReading> Zones { get; set; } = Array.Empty<ThermalReading>();

        public Task<IReadOnlyList<ThermalReading>> ReadAsync() => Task.FromResult(Zones);
    }

    /// The text of every button under the element (the pane's rows are built as logical children).
    private static IEnumerable<string> ButtonTexts(DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button { Content: string text })
            {
                yield return text;
            }
            foreach (var nested in ButtonTexts(child))
            {
                yield return nested;
            }
        }
    }

    /// The text of every text block under the element.
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

    [Fact]
    public void CounterReads_TheRowReadsWithoutAdministrator_AndOffersNoTurnOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var helper = new SensorHelper(settings, new NoLaunch(), new NoPipe(), new NoStop(), "NotchTasksSensors.exe", Sid, 1, _ => false);
            var counter = new FakeCounter { Zones = new[] { new ThermalReading("\\_TZ.TZ00", 27.85) } };
            var pane = new PermissionsPane(settings, helper, counter);
            var window = new Window { Content = pane };
            try
            {
                Fixture.ShowOffScreen(window);
                Fixture.PumpUntil(() => TextsOf(pane).Contains(L10n.L("Read without administrator")));
                window.UpdateLayout();

                Assert.DoesNotContain(L10n.L("Turn on"), ButtonTexts(pane));
                Assert.DoesNotContain(L10n.L("Try again"), ButtonTexts(pane));
                Assert.Equal(HelperState.Off, helper.State);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
