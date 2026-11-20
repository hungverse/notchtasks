using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using System.Text.Json.Nodes;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ModulesPaneBatteryTests
{
    /// The titles of the rows the pane shows, read from its Rows panel after Apply.
    private static List<string> RowTitles(ModulesPane pane)
    {
        Fixture.Call(pane, "Apply");
        var rows = Fixture.Named<StackPanel>(pane, "Rows");
        return rows.Children.OfType<Grid>()
            .Select(row => Descendants<TextBlock>(row).First(t => t.FontWeight == FontWeights.SemiBold).Text)
            .ToList();
    }

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
    public void BatteryRow_IsHiddenOnAPcWithoutABattery()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var battery = L10n.L(ModuleCatalog.Title("battery"));

            var withBattery = RowTitles(Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => true)));
            var withoutBattery = RowTitles(Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => false)));

            Assert.Contains(battery, withBattery);
            Assert.DoesNotContain(battery, withoutBattery);
            Assert.Equal(withBattery.Count - 1, withoutBattery.Count);
        });
    }

    [Fact]
    public void Move_KeepsAHiddenBatteryAtItsStoredPlace()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var stored = new[] { "agents", "ci", "battery", "media", "timer" };
            var array = new JsonArray();
            foreach (var id in stored)
            {
                array.Add(JsonValue.Create(id));
            }
            state.Settings.Set("modules", array);
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => false));

            // Down: media swaps with timer, the nearest shown neighbour; battery keeps index 2.
            Fixture.Call(pane, "Move", "media", 1);
            Assert.Equal(
                new[] { "agents", "ci", "battery", "timer", "media", "shelf", "limits", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
                state.Settings.Modules);

            // Up: back to the stored order.
            Fixture.Call(pane, "Move", "media", -1);
            Assert.Equal(
                new[] { "agents", "ci", "battery", "media", "timer", "shelf", "limits", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
                state.Settings.Modules);
        });
    }
}
