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
    public void Accent_IsTheWindowsSystemAccent()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);

                // The Windows accent: the window's accent brush is the theme's own (the application's), not a Mac override.
                var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.FindResource("AccentFillColorDefaultBrush"));
                Assert.Same(Application.Current.FindResource("AccentFillColorDefaultBrush"), brush);
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
                // The footer's version button: the app's version, without the build hash.
                var footer = Fixture.Named<UpdateFooter>(window, "Footer");
                var version = Assert.IsType<string>(Fixture.Named<System.Windows.Controls.Button>(footer, "VersionButton").Content);

                Assert.False(string.IsNullOrWhiteSpace(version));
                Assert.DoesNotContain('+', version);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// Every pane's content, as a column of blocks: the visible cards (Borders) in the pane's stacks, not inside another card.
    private static IEnumerable<Border> Blocks(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Border { Visibility: Visibility.Visible } border)
            {
                yield return border;
            }
            else if (child is StackPanel { Visibility: Visibility.Visible } stack)
            {
                foreach (var deeper in Blocks(stack))
                {
                    yield return deeper;
                }
            }
        }
    }

    [Fact]
    public void EveryPane_FillsTheSameColumn()
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
                var widths = new Dictionary<PaneId, double>();
                foreach (var pane in Enum.GetValues<PaneId>())
                {
                    Fixture.Call(window, "SelectPane", pane);
                    window.UpdateLayout();
                    widths[pane] = ((FrameworkElement)host.Content).ActualWidth;
                }

                // Overview (a hero, three stats and the module groups) and Behavior (three short cards) are very different,
                // yet both are the host's full column.
                Assert.True(host.ActualWidth > 0);
                Assert.All(widths, pair => Assert.Equal(host.ActualWidth, pair.Value, 0.5));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EveryPane_ColumnIsCappedAt760OnAWideWindow()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Overview);
            try
            {
                Fixture.ShowOffScreen(window);
                window.Width = 1400;
                window.UpdateLayout();
                var host = Fixture.Named<ContentControl>(window, "PaneHost");

                foreach (var pane in Enum.GetValues<PaneId>())
                {
                    Fixture.Call(window, "SelectPane", pane);
                    window.UpdateLayout();
                    // a screen narrower than 1400 (a CI runner) shrinks the window: then the column fills what is left
                    Assert.Equal(Math.Min(760, host.ActualWidth), ((FrameworkElement)host.Content).ActualWidth, 0.5);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EveryPane_CardsStretchToTheColumn()
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

                foreach (var pane in Enum.GetValues<PaneId>())
                {
                    Fixture.Call(window, "SelectPane", pane);
                    window.UpdateLayout();
                    var content = (FrameworkElement)host.Content;
                    foreach (var card in Blocks(content))
                    {
                        Assert.True(Math.Abs(card.ActualWidth - content.ActualWidth) < 0.5,
                            $"{pane}: a card is {card.ActualWidth} DIP wide in a {content.ActualWidth} DIP column.");
                    }
                }
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
    public void Slider_UsesTheFluentTemplateAndIsLaidOut()
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

                // The Windows theme's slider (no Mac override): its track is laid out, and the filled part has a width.
                var track = Assert.IsType<System.Windows.Controls.Primitives.Track>(slider.Template.FindName("PART_Track", slider));
                var fill = Assert.IsType<System.Windows.Controls.Primitives.RepeatButton>(track.DecreaseRepeatButton);
                window.UpdateLayout();
                Assert.True(fill.ActualWidth > 0, $"The filled part must have a width (was {fill.ActualWidth}).");
                Assert.True(fill.IsVisible, "The filled part must be visible.");
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

    [Fact]
    public void SidebarRows_AreThirtySixDip()
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
                window.UpdateLayout();

                // The Windows 11 NavigationView sidebar: every row is 36 DIP tall (the margin between rows is not part of it).
                for (var i = 0; i < sidebar.Items.Count; i++)
                {
                    var row = Assert.IsType<ListBoxItem>(sidebar.ItemContainerGenerator.ContainerFromIndex(i));
                    Assert.Equal(36, row.ActualHeight);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Window_RendersInLightAndDarkTheme(bool dark)
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var previous = Application.Current.ThemeMode;
            Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
            var window = Fixture.Create<ControlPanelWindow>(state, PaneId.Appearance);
            try
            {
                Fixture.ShowOffScreen(window);

                // The brushes of the look resolve in this theme: a missing key makes FindResource throw.
                foreach (var key in new[]
                {
                    "ApplicationBackgroundBrush", "TextFillColorPrimaryBrush", "CardBackgroundFillColorDefaultBrush",
                    "CardStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush", "SubtleFillColorTertiaryBrush",
                    "ControlStrongStrokeColorDefaultBrush", "ControlStrongFillColorDefaultBrush", "AccentFillColorDefaultBrush",
                })
                {
                    Assert.NotNull(window.FindResource(key));
                }

                // The window draws in this theme: a bitmap of it renders without throwing and has its size.
                window.UpdateLayout();
                var width = Math.Max(1, (int)window.ActualWidth);
                var height = Math.Max(1, (int)window.ActualHeight);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                Assert.Equal(width, bitmap.PixelWidth);
                Assert.Equal(height, bitmap.PixelHeight);
            }
            finally
            {
                window.Close();
                Application.Current.ThemeMode = previous;
            }
        });
    }
}
