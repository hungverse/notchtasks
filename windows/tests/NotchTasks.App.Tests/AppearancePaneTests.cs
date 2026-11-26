using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class AppearancePaneTests
{
    /// The theme tiles of the grid, in the order they are shown.
    private static List<Button> Tiles(AppearancePane pane) =>
        Fixture.Named<Panel>(pane, "ThemeGrid").Children.OfType<Button>().ToList();

    [Fact]
    public void ThemeGrid_ShowsTheSixThemesInTheMacsOrder()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings);

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
            var pane = Fixture.Create<AppearancePane>(state.Settings);
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
            var pane = Fixture.Create<AppearancePane>(state.Settings);

            state.Settings.SetMany(Themes.All.First(t => t.Id == "ocean").Values);
            pane.ShowStoredValues();

            Assert.True(pane.ShowsSelected("ocean"));
            Assert.False(pane.ShowsSelected("sunset"));
        });
    }
}
