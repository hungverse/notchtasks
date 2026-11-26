using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class OverviewPaneTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    [Fact]
    public void Stats_AreRunningCiErrorsAndPermissions()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<OverviewPane>(state.Settings, state);
            Fixture.Call(pane, "Apply");

            var stats = Fixture.Named<UniformGrid>(pane, "Stats");
            var labels = stats.Children.OfType<Border>()
                .Select(card => Descendants<TextBlock>(card).Last().Text)
                .ToList();

            Assert.Equal(3, labels.Count);
            Assert.Equal(new[] { L10n.L("Running"), L10n.L("CI errors"), L10n.L("Permissions") }, labels);
        });
    }

    [Fact]
    public void ReportButton_IsAtTheBottom_AfterTheGuide_WithSyncOff()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();  // no sync: the report still opens from here (Mac Preferences.swift:166)
            var pane = Fixture.Create<OverviewPane>(state.Settings, state);
            Fixture.Call(pane, "Apply");

            var report = Fixture.Named<Button>(pane, "ReportButton");
            var guide = Fixture.Named<Button>(pane, "GuideButton");
            Assert.Equal(L10n.L("Report a problem"), report.Content);
            Assert.Same(guide.Parent, report.Parent);
            var parent = (StackPanel)report.Parent;
            Assert.True(parent.Children.IndexOf(report) > parent.Children.IndexOf(guide));
        });
    }

    [Fact]
    public void ModuleSwitch_WritesTheModuleOffAndOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<OverviewPane>(state.Settings, state);
            Fixture.Call(pane, "Apply");
            var title = L10n.L("Now Playing");
            var groups = Fixture.Named<StackPanel>(pane, "ModuleGroups");
            CheckBox SwitchFor() => Descendants<CheckBox>(groups).First(box => AutomationProperties.GetName(box) == title);

            // Off: the switch is cleared and the module goes into the disabled list.
            SwitchFor().IsChecked = false;
            SwitchFor().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Contains("media", state.Settings.Disabled);

            // On again: the module leaves the list.
            SwitchFor().IsChecked = true;
            SwitchFor().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.DoesNotContain("media", state.Settings.Disabled);
        });
    }

    /// Sets the notch snapshot the pane reads its counts from (AppState.Snapshot has a private setter).
    private static void SetCounts(AppState state, int running, int alerts)
    {
        var agents = Enumerable.Range(1, running)
            .Select(i => new ItemState(new WatchItem("agent" + i, "Agent", WatchKind.Process), new[] { new Job("j" + i, "Task", "3%", true) }))
            .ToList();
        var failures = Enumerable.Range(1, alerts).Select(i => new JobAlert("a" + i, "Build", "Project", "failed", null)).ToList();
        var snapshot = new NotchSnapshot(new[] { "agents" }, 0, null, null, Fixture.QuietSensors, null, null)
        {
            Running = agents,
            Alerts = failures,
        };
        typeof(AppState).GetProperty(nameof(AppState.Snapshot))!.SetValue(state, snapshot);
    }

    [Fact]
    public void NotchPreview_ShowsTheRunningCountOnTheLeft_AndNoAlertIconWithoutAlerts()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<OverviewPane>(state.Settings, state);
            SetCounts(state, running: 2, alerts: 0);

            Fixture.Call(pane, "UpdateCounts");

            var left = Fixture.Named<StackPanel>(pane, "RunningPart");
            var right = Fixture.Named<StackPanel>(pane, "AlertPart");
            // The sparkle tile, the count and the working bars.
            Assert.Equal(3, left.Children.Count);
            Assert.Equal("2", Assert.IsType<TextBlock>(left.Children[1]).Text);
            Assert.Empty(right.Children);
        });
    }

    [Fact]
    public void NotchPreview_LeavesTheLeftEmptyWithoutRunning_AndShowsTheAlertsOnTheRight()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<OverviewPane>(state.Settings, state);
            SetCounts(state, running: 0, alerts: 1);

            Fixture.Call(pane, "UpdateCounts");

            var left = Fixture.Named<StackPanel>(pane, "RunningPart");
            var right = Fixture.Named<StackPanel>(pane, "AlertPart");
            Assert.Empty(left.Children);
            // The wrench and the 60% progress ring.
            Assert.Equal(2, right.Children.Count);
            Assert.Equal("", Assert.IsType<FluentGlyph>(right.Children[0]).Code);
        });
    }
}
