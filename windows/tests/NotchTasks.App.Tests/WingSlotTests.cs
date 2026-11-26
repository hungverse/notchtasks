using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.AppTests;

/// The Icons pane's wing editor: a No background icon has no grey box behind it, and the selection is an accent ring.
public sealed class WingSlotTests
{
    // the icon tiles of the left wing are the buttons with no tooltip (the + slot has one) and the badge id is in IconSelection order
    private static Border FaceOf(BadgesPane pane, int index) =>
        Assert.IsType<Border>(Fixture.Named<StackPanel>(pane, "LeftWing").Children.OfType<Button>().Where(b => b.ToolTip is null).ElementAt(index).Content);

    [Fact]
    public void PlainBadge_HasNoGreyBox_AndAFilledBadgeKeepsItsSlot()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var state = Fixture.NewState();
            var store = state.Settings;
            store.Set("badges", JsonNode.Parse("[{\"id\":\"plain\",\"symbol\":\"star\",\"right\":false,\"plain\":true},"
                + "{\"id\":\"solid\",\"symbol\":\"star\",\"right\":false,\"color\":\"#30D158\"}]"));
            IconSelection.Select("plain");
            try
            {
                var pane = new BadgesPane(store, state);
                var window = new Window { Content = pane };
                Fixture.ShowOffScreen(window);
                try
                {
                    Assert.Same(Brushes.Transparent, FaceOf(pane, 0).Background);
                    Assert.IsType<SolidColorBrush>(FaceOf(pane, 1).Background);
                    // the selection is the accent ring of the slot, a border, not a fill
                    Assert.Equal(2, FaceOf(pane, 0).BorderThickness.Left);
                    Assert.Equal(0, FaceOf(pane, 1).BorderThickness.Left);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                IconSelection.Clear();
            }
        });
    }
}
