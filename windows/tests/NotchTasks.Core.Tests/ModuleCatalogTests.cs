using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class ModuleCatalogTests
{
    private static readonly string[] MacOrder =
    {
        "agents", "ci", "media", "calendar", "timer", "shelf", "limits", "battery", "devices", "calls", "privacy",
        "levels", "network", "downloads", "health", "mirror",
    };

    [Fact]
    public void All_InMacOrder()
    {
        Assert.Equal(MacOrder, ModuleCatalog.All);
    }

    [Fact]
    public void OnWindows_HidesLevelsCalendarFans()
    {
        Assert.False(ModuleCatalog.OnWindows("levels"));
        Assert.False(ModuleCatalog.OnWindows("calendar"));
        Assert.False(ModuleCatalog.OnWindows("fans"));
        Assert.True(ModuleCatalog.OnWindows("agents"));
        Assert.True(ModuleCatalog.OnWindows("media"));
    }

    [Fact]
    public void Order_KeepsUserOrder_AppendsNew()
    {
        // The user's order first; unknown and off-Windows ids and repeats are dropped; the rest follow in the Mac's order.
        var order = ModuleCatalog.Order(new[] { "timer", "agents", "calendar", "bogus", "timer", "levels" });

        Assert.Equal(
            new[] { "timer", "agents", "ci", "media", "shelf", "limits", "battery", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
            order);
    }

    [Fact]
    public void Order_OfNothing_IsTheWindowsModulesInMacOrder()
    {
        Assert.Equal(
            new[] { "agents", "ci", "media", "timer", "shelf", "limits", "battery", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
            ModuleCatalog.Order(Array.Empty<string>()));
    }

    [Fact]
    public void Titles_AreTheMacEnglishKeys()
    {
        Assert.Equal("AI agent", ModuleCatalog.Title("agents"));
        Assert.Equal("Build & CI", ModuleCatalog.Title("ci"));
        Assert.Equal("Now Playing", ModuleCatalog.Title("media"));
        Assert.Equal("Volume & brightness", ModuleCatalog.Title("levels"));
        Assert.Equal("Agents and AI apps at work", ModuleCatalog.Blurb("agents"));
    }

    [Fact]
    public void Glyph_IsSetForEveryModule()
    {
        foreach (var id in ModuleCatalog.All)
        {
            Assert.False(string.IsNullOrEmpty(ModuleCatalog.Glyph(id)), id);
        }
    }
}
