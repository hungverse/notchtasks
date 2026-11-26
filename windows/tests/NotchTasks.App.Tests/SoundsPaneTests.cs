using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Sounds;
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
    public void ChooseYourFile_IsInEveryEventsList()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<SoundsPane>(state.Settings);
            var rows = Fixture.Named<StackPanel>(pane, "EventRows");

            Assert.Equal(ChoiceItems.SoundEvents.Count, rows.Children.Count);
            foreach (DockPanel row in rows.Children)
            {
                var picker = (ComboBox)row.Children[1];
                Assert.Contains(ChoiceItems.ChooseFile, picker.Items.Cast<Choice>());
                Assert.Equal("Choose your file…", ChoiceItems.ChooseFile.Title);
            }
        });
    }

    [Fact]
    public void PlayIsDisabled_ForASilentEvent()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("soundMap", JsonNode.Parse("{\"taskDone\":\"\",\"agentAsk\":\"Ping\"}"));
            var pane = Fixture.Create<SoundsPane>(state.Settings);
            var rows = Fixture.Named<StackPanel>(pane, "EventRows");
            pane.ShowStoredValues();

            // The first row is taskDone (silent), the second agentAsk (Ping): the play button is the row's first child.
            var silent = (DockPanel)rows.Children[0];
            var sounding = (DockPanel)rows.Children[1];
            Assert.False(((Button)silent.Children[0]).IsEnabled);
            Assert.True(((Button)sounding.Children[0]).IsEnabled);
        });
    }

    [Fact]
    public void ImportPack_WritesTheSoundsAndVolume()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<SoundsPane>(state.Settings);
            var file = Path.Combine(root.Folder, "pack.notchsounds");
            File.WriteAllText(file, "{\"format\":\"notchtasks.sounds\",\"version\":1,\"volume\":0.35,"
                + "\"sounds\":{\"taskDone\":\"Pop\",\"ciFailed\":\"Tink\"}}");

            pane.ApplyImport(SoundsPane.ReadPack(new SoundPackFile(Path.Combine(root.Folder, "Sounds")), file));

            Assert.Equal("Pop", state.Settings.SoundMap["taskDone"]);
            Assert.Equal("Tink", state.Settings.SoundMap["ciFailed"]);
            Assert.Equal(0.35, state.Settings.Volume, 2);
        });
    }

    [Fact]
    public void ImportPack_ANonPack_ShowsTheMessageAndChangesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var before = state.Settings.Volume;
            var pane = Fixture.Create<SoundsPane>(state.Settings);
            string? shown = null;
            pane.Message = (caption, text) => shown = caption + "|" + text;
            var file = Path.Combine(root.Folder, "notes.json");
            File.WriteAllText(file, "{\"hello\":1}");

            pane.ApplyImport(SoundsPane.ReadPack(new SoundPackFile(Path.Combine(root.Folder, "Sounds")), file));

            Assert.Equal("Couldn't import the sound pack|This file isn't a NotchTasks sound pack.", shown);
            Assert.Equal(before, state.Settings.Volume);
        });
    }

    [Fact]
    public void ImportPack_AnIoError_ShowsTheExceptionMessage()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<SoundsPane>(state.Settings);
            string? shown = null;
            pane.Message = (caption, text) => shown = caption + "|" + text;
            var missing = Path.Combine(root.Folder, "gone.notchsounds");

            var outcome = SoundsPane.ReadPack(new SoundPackFile(Path.Combine(root.Folder, "Sounds")), missing);
            pane.ApplyImport(outcome);

            Assert.NotNull(outcome.Problem);
            Assert.DoesNotContain("isn't a NotchTasks sound pack", outcome.Problem);
            Assert.Equal("Couldn't import the sound pack|" + outcome.Problem, shown);
        });
    }

    [Fact]
    public void ExportPack_WritesThePackFile_AndAFailedWriteReportsIt()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var files = new SoundPackFile(Path.Combine(root.Folder, "Sounds"));
            var map = new Dictionary<string, string> { ["taskDone"] = "Glass" };
            var good = Path.Combine(root.Folder, "pack.notchsounds");

            Assert.Null(SoundsPane.WritePack(files, map, 0.5, good));
            Assert.Contains("\"taskDone\":\"Glass\"", File.ReadAllText(good).Replace(" ", string.Empty).Replace("\n", string.Empty));

            var bad = Path.Combine(root.Folder, "no-such-folder", "pack.notchsounds");
            Assert.NotNull(SoundsPane.WritePack(files, map, 0.5, bad));
        });
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
