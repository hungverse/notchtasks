using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Notifications;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class AppearancePaneTests
{
    /// The theme tiles of the grid, in the order they are shown.
    private static List<Button> Tiles(AppearancePane pane) =>
        Fixture.Named<Panel>(pane, "ThemeGrid").Children.OfType<Button>().ToList();

    /// Counts the system notifications the app posts.
    private sealed class RecordingNotifier : INotifier
    {
        public List<Toast> Posted { get; } = new();

        public void Post(Toast t) => Posted.Add(t);
    }

    /// An app state whose system notifier is `notifier`, over the temp settings of the test.
    private static AppState StateWith(SettingsStore settings, INotifier notifier) =>
        new(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Notifier = notifier });

    [Fact]
    public void TestAlert_QueuesATaskFinishedToastInTheAccent_WithNoSystemNotification()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var notifier = new RecordingNotifier();
            using var state = StateWith(settings, notifier);
            settings.Set("accent", JsonValue.Create("orange"));
            var pane = Fixture.Create<AppearancePane>(settings, state);

            Fixture.Named<Button>(pane, "TestAlertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var toast = state.Toasts.Current;
            Assert.NotNull(toast);
            Assert.Equal("Task finished", toast.Title);
            Assert.Equal("Test alert", toast.Subtitle);
            Assert.Equal("taskDone", toast.Event);
            Assert.Equal("#FF9F0A", toast.Tint);
            Assert.Empty(notifier.Posted);
        });
    }

    [Fact]
    public void TestAlert_MatchAppColorIsTheMacsGreen()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var state = StateWith(settings, new RecordingNotifier());
            var pane = Fixture.Create<AppearancePane>(settings, state);

            Fixture.Named<Button>(pane, "TestAlertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("#30D158", state.Toasts.Current?.Tint);
        });
    }

    [Fact]
    public void ThemeGrid_ShowsTheSixThemesInTheMacsOrder()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);

            Assert.Equal(Themes.All.Select(t => t.Id), Tiles(pane).Select(b => (string)b.Tag));
        });
    }

    [Fact]
    public void ClickingATheme_AppliesItToTheStoreAndSelectsItsTile()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            var glass = Tiles(pane).First(b => (string)b.Tag == "glass");

            glass.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // The pane is not shown here, so it takes the store's values as a shown pane does on each change.
            pane.ShowStoredValues();

            Assert.Equal("glass", state.Settings.Background);
            Assert.Equal("banner", state.Settings.ToastStyle);
            Assert.Equal("smooth", state.Settings.Motion);
            Assert.Equal("white", state.Settings.Accent);
            Assert.True(pane.ShowsSelected("glass"));
            Assert.False(pane.ShowsSelected("classic"));
        });
    }

    [Fact]
    public void AnOutsideChange_MovesTheSelectionToTheMatchingTheme()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);

            state.Settings.SetMany(Themes.All.First(t => t.Id == "ocean").Values);
            pane.ShowStoredValues();

            Assert.True(pane.ShowsSelected("ocean"));
            Assert.False(pane.ShowsSelected("sunset"));
        });
    }

    /// A profile as the Mac writes it, with a key Windows lacks (haptics) and a Mac module (calendar).
    private const string MacProfile = """
    {
      "accent" : "custom",
      "background" : "glass",
      "customAccent" : "#00F5D4",
      "disabled" : [ "timer", "calendar" ],
      "doneAfter" : 45,
      "expandMode" : "hover",
      "gradientA" : "#1E1B4B",
      "gradientB" : "#000000",
      "haptics" : false,
      "hoverDelay" : 0.15,
      "limitWarn" : 80,
      "menuBarIcon" : true,
      "moduleColors" : {},
      "modules" : [ "agents", "calendar", "timer" ],
      "motion" : "snappy",
      "placements" : {},
      "size" : "compact",
      "soundMap" : {},
      "sounds" : true,
      "systemNotifications" : true,
      "toastStyle" : "card",
      "volume" : 0.5
    }
    """;

    [Fact]
    public void ExportProfile_WritesTheMacsProfileFile()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var path = Path.Combine(root.Folder, "NotchTasks.json");

            Assert.Null(AppearancePane.WriteProfile(SettingsProfile.Export(state.Settings), path));

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(22, json.Count);
            Assert.True((bool?)json["haptics"]);
        });
    }

    [Fact]
    public void ExportProfile_AFailedWriteReportsIt()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var bad = Path.Combine(root.Folder, "no-such-folder", "NotchTasks.json");

            Assert.NotNull(AppearancePane.WriteProfile("{}", bad));
        });
    }

    [Fact]
    public void ImportProfile_AppliesTheMacProfile_ToTheStore()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            var path = Path.Combine(root.Folder, "mac.json");
            File.WriteAllText(path, MacProfile);

            pane.ApplyProfile(AppearancePane.ReadProfile(path));

            Assert.Equal("glass", state.Settings.Background);
            Assert.Equal("#00F5D4", state.Settings.CustomAccent);
            Assert.Equal("snappy", state.Settings.Motion);
            Assert.Equal("card", state.Settings.ToastStyle);
            Assert.Equal(45, state.Settings.DoneAfter, 1);
            Assert.Equal(0.5, state.Settings.Volume, 2);
            Assert.Equal(new[] { "timer" }, state.Settings.Disabled);
        });
    }

    [Fact]
    public void ImportProfile_ANonProfile_ShowsTheMessageAndChangesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            string? shown = null;
            pane.Message = (caption, text) => shown = caption + "|" + text;
            var path = Path.Combine(root.Folder, "notes.json");
            File.WriteAllText(path, "{\"hello\":1}");
            var before = state.Settings.Background;

            pane.ApplyProfile(AppearancePane.ReadProfile(path));

            Assert.Equal("Couldn't import the profile|This file isn't a NotchTasks profile.", shown);
            Assert.Equal(before, state.Settings.Background);
        });
    }

    [Fact]
    public void ImportProfile_AMissingFile_ShowsTheProblem()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            string? shown = null;
            pane.Message = (caption, text) => shown = caption + "|" + text;

            var read = AppearancePane.ReadProfile(Path.Combine(root.Folder, "gone.json"));
            pane.ApplyProfile(read);

            Assert.NotNull(read.Error);
            Assert.Equal("Couldn't import the profile|" + read.Error, shown);
        });
    }
}
