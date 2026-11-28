using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The Permissions pane's Temperatures row: Turn off is shown whenever the sign-in marker exists, in every helper state. The helper
/// is built over fakes (no UAC, no pipe, no stop event), and the pane is shown off screen.
public sealed class PermissionsPaneTurnOffTests
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

    /// Shows the pane with a helper in the given state, with or without the marker, and returns the button texts.
    private static List<string> PaneButtons(HelperState state, bool marker)
    {
        var result = new List<string>();
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var markerPath = SensorNames.MarkerPath(Sid);
            var helper = new SensorHelper(settings, new NoLaunch(), new NoPipe(), new NoStop(), "NotchTasksSensors.exe", Sid, 1,
                path => marker && path == markerPath);
            Fixture.Call(helper, "SetState", state);
            var pane = new PermissionsPane(settings, helper, new Fixture.NoThermalCounter());
            var window = new Window { Content = pane };
            try
            {
                Fixture.ShowOffScreen(window);
                Fixture.Call(pane, "Refresh", false);
                window.UpdateLayout();
                result.AddRange(ButtonTexts(pane));
            }
            finally
            {
                window.Close();
            }
        });
        return result;
    }

    [Theory]
    [InlineData(HelperState.Off)]
    [InlineData(HelperState.Starting)]
    [InlineData(HelperState.On)]
    [InlineData(HelperState.Declined)]
    [InlineData(HelperState.Unavailable)]
    [InlineData(HelperState.Paused)]
    public void TurnOff_IsVisible_WheneverTheMarkerExists(HelperState state)
    {
        Assert.Contains(L10n.L("Turn off"), PaneButtons(state, marker: true));
    }

    [Theory]
    [InlineData(HelperState.Off)]
    [InlineData(HelperState.Starting)]
    [InlineData(HelperState.Declined)]
    [InlineData(HelperState.Unavailable)]
    [InlineData(HelperState.Paused)]
    public void TurnOff_IsHidden_WithoutTheMarker_WhenNothingIsOn(HelperState state)
    {
        Assert.DoesNotContain(L10n.L("Turn off"), PaneButtons(state, marker: false));
    }
}
