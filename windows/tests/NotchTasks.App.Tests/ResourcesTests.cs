using System.Windows;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ResourcesTests
{
    [Fact]
    public void AppResourcesLoadWithThemeModeSystem()
    {
        Sta.Run(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.Equal(ThemeMode.System, Application.Current.ThemeMode);
            // Keys the Control Panel and the guide use: they stay null when the shared styles do not load.
            Assert.NotNull(Application.Current.TryFindResource("PaneTitle"));
            Assert.NotNull(Application.Current.TryFindResource("SectionHeader"));
            Assert.NotNull(Application.Current.TryFindResource("RowLabel"));
        });
    }
}
