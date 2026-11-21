using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ControlPanelWindowTests
{
    public static IEnumerable<object[]> Panes() => Enum.GetValues<PaneId>().Select(pane => new object[] { pane });

    [Fact]
    public void Accent_IsTheMacsSystemBlue()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);

                // The Mac's blue, not the Windows accent: the window's own MacAccent.xaml wins over the theme's brush.
                var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.FindResource("AccentFillColorDefaultBrush"));
                Assert.Equal(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF), brush.Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

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
    public void Sidebar_ShowsTheAppVersion()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);
                var version = Fixture.Named<TextBlock>(window, "VersionText");

                Assert.False(string.IsNullOrWhiteSpace(version.Text));
                Assert.DoesNotContain('+', version.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PaneHost_HasTheContentMargin()
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

                Assert.Equal(new Thickness(32, 24, 32, 32), host.Margin);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SwitchingPane_ResetsScrollToTheTop()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);
                window.MinHeight = 0;
                window.Height = 150; // short enough that the Modules pane must scroll (its page is short without the title)
                var scroll = Fixture.Named<ScrollViewer>(window, "PaneScroll");
                Fixture.Call(window, "SelectPane", PaneId.Modules);
                window.UpdateLayout();

                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
                window.UpdateLayout();
                // Guard: the offset only means something if this pane really scrolls.
                Assert.True(scroll.VerticalOffset > 0, $"The pane must be scrollable for this test (extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}).");

                Fixture.Call(window, "SelectPane", PaneId.Overview);
                window.UpdateLayout();

                Assert.Equal(0, scroll.VerticalOffset);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Slider_GetsTheMacsBlueTrack()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Behavior);
            try
            {
                Fixture.ShowOffScreen(window);
                var pane = Assert.IsAssignableFrom<FrameworkElement>(Fixture.Named<ContentControl>(window, "PaneHost").Content);
                var slider = Fixture.Named<Slider>(pane, "DelaySlider");
                window.UpdateLayout();

                // The MacSlider template: its Track is PART_Track, and the filled part (the decrease button) is the Mac's blue.
                var track = Assert.IsType<System.Windows.Controls.Primitives.Track>(slider.Template.FindName("PART_Track", slider));
                var fill = Assert.IsType<System.Windows.Controls.Primitives.RepeatButton>(track.DecreaseRepeatButton);
                Assert.False(fill.Focusable);
                fill.ApplyTemplate();
                var border = Assert.IsType<Border>(System.Windows.Media.VisualTreeHelper.GetChild(fill, 0));
                var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(border.Background);
                Assert.Equal(System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF), brush.Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectingAPane_SetsTheWindowTitleToItsName_AndHidesThePageTitle()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);
                Fixture.Call(window, "SelectPane", PaneId.Modules);
                window.UpdateLayout();

                // The title bar names the pane, as the sidebar row does; the big title in the page is collapsed.
                var modules = PaneItem.All.First(item => item.Pane == PaneId.Modules);
                Assert.Equal(modules.Title, window.Title);

                var pane = Assert.IsAssignableFrom<FrameworkElement>(Fixture.Named<ContentControl>(window, "PaneHost").Content);
                var titleStyle = window.FindResource("PaneTitle");
                var titles = TextBlocksIn(pane).Where(text => text.Style == titleStyle).ToList();
                Assert.NotEmpty(titles);
                Assert.All(titles, text => Assert.Equal(Visibility.Collapsed, text.Visibility));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// Every TextBlock under `root`, depth first.
    private static IEnumerable<TextBlock> TextBlocksIn(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                yield return text;
            }
            foreach (var deeper in TextBlocksIn(child))
            {
                yield return deeper;
            }
        }
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
