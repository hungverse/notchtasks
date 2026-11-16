using System.IO;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class SoundsPaneTests
{
    /// The Mac's language tables (the texts of the Play tooltip are there).
    private static string MacSources()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("mac/Sources"), "mac", "Sources");
    }

    [Fact]
    public void PlayTooltip_FollowsTheLanguage()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<SoundsPane>(state.Settings);
            var folder = MacSources();
            try
            {
                // The first row's play button is the first child of its DockPanel.
                var rows = Fixture.Named<StackPanel>(pane, "EventRows");
                var play = (Button)((DockPanel)rows.Children[0]).Children[0];

                L10n.Init(folder, "en");
                Assert.Equal("Play", play.ToolTip);

                // A language change applies at once, without a rebuilt pane.
                L10n.Init(folder, "ja");
                Assert.Equal(L10n.L("Play"), play.ToolTip);
                Assert.NotEqual("Play", play.ToolTip);
            }
            finally
            {
                L10n.Init(folder, "en");
            }
        });
    }
}
