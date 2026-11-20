using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
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
}
