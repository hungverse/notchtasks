using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ControlPanelWindowTests
{
    public static IEnumerable<object[]> Panes() => Enum.GetValues<PaneId>().Select(pane => new object[] { pane });

    [Theory]
    [MemberData(nameof(Panes))]
    public void EveryPaneBuildsOnSelection(PaneId pane)
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);
                var host = Fixture.Named<ContentControl>(window, "PaneHost");

                Fixture.Call(window, "SelectPane", pane);
                window.UpdateLayout();

                Assert.NotNull(host.Content);
                Assert.IsAssignableFrom<UserControl>(host.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SidebarHasOneRowPerPane()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);
                var sidebar = Fixture.Named<ListBox>(window, "Sidebar");
                Assert.Equal(Enum.GetValues<PaneId>().Length, sidebar.Items.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
