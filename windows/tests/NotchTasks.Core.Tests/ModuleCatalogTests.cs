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
    public void Built_IncludesPhase3aModules()
    {
        // Phase 3a builds these seven; the rest stay "Coming soon" (ci, media, shelf, limits, devices, mirror are later phases).
        foreach (var id in new[] { "agents", "timer", "battery", "network", "downloads", "health", "privacy", "calls" })
        {
            Assert.True(ModuleCatalog.Built(id), id);
        }
        foreach (var id in new[] { "ci", "media", "shelf", "limits", "devices", "mirror", "bogus" })
        {
            Assert.False(ModuleCatalog.Built(id), id);
        }
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

    [Fact]
    public void Reorder_KeepsHiddenIdsInPlace()
    {
        // calendar, levels and bogus are hidden or unknown: they keep their positions; agents and timer swap.
        var stored = new[] { "calendar", "timer", "levels", "agents", "bogus" };

        var result = ModuleCatalog.Reorder(stored, "agents", -1);

        Assert.Equal(
            new[] { "calendar", "agents", "levels", "timer", "bogus", "ci", "media", "shelf", "limits", "battery", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
            result);
    }

    [Fact]
    public void Reorder_AtEdge_NoChange()
    {
        var stored = new[] { "timer", "agents", "calendar" };

        // The first visible module cannot move up; the last visible one (mirror, not stored) cannot move down.
        Assert.Equal(stored, ModuleCatalog.Reorder(stored, "timer", -1));
        Assert.Equal(stored, ModuleCatalog.Reorder(stored, "mirror", 1));
    }

    [Fact]
    public void Reorder_WithShownPredicate_KeepsAHiddenIdAtItsPlace()
    {
        // battery is hidden on this PC: a move of timer swaps only with media, and battery stays at index 2.
        var stored = new[] { "agents", "ci", "battery", "media", "timer" };

        var result = ModuleCatalog.Reorder(stored, "timer", -1, id => id != "battery");

        Assert.Equal(new[] { "agents", "ci", "battery", "timer", "media" }, result.Take(5));
    }
}
