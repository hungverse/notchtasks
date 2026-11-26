using System.Linq;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.AppTests;

/// The Icons pane's title and its sidebar label. The Mac's pane is "Icons & lock" (Preferences.swift Pane.title) with a lock
/// screen that Windows does not have, so the pane is titled with the badges-only key "Icons".
[Collection("L10n")]
public sealed class BadgesPaneTitleTests
{
    [Fact]
    public void Badges_TitleKeyIsIcons()
    {
        Assert.Equal("Icons", PaneId.Badges.TitleKey());
    }

    [Fact]
    public void Badges_NavLabelResolvesToTheIconsKey()
    {
        L10n.Init(System.AppContext.BaseDirectory, "en");

        var item = PaneItem.All.Single(row => row.Pane == PaneId.Badges);

        Assert.Equal("Icons", item.Title);
        Assert.Equal(L10n.L(PaneId.Badges.TitleKey()), item.Title);
    }
}
