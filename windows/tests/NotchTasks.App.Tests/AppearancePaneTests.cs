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
}
