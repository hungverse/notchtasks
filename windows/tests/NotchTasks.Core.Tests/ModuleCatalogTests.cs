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
    public void OnWindows_HidesLevelsAndFans_ButNotCalendar()
    {
        // Calendar reads the Windows appointment store, so it is on this PC; levels and fans are not.
        Assert.False(ModuleCatalog.OnWindows("levels"));
        Assert.False(ModuleCatalog.OnWindows("fans"));
        Assert.True(ModuleCatalog.OnWindows("calendar"));
        Assert.True(ModuleCatalog.OnWindows("agents"));
        Assert.True(ModuleCatalog.OnWindows("media"));
    }

    [Fact]
    public void Built_IncludesCalendar()
    {
        // Phase 9 builds the calendar module: it leaves "Coming soon" and asks for nothing extra.
        Assert.True(ModuleCatalog.Built("calendar"));
    }

    [Fact]
    public void Built_IncludesPhase3aModules()
    {
        // Phase 3a builds these seven, and nothing else is built but the ones Phase 3b, 3c and 3d add.
        foreach (var id in new[] { "agents", "timer", "battery", "network", "downloads", "health", "privacy", "calls" })
        {
            Assert.True(ModuleCatalog.Built(id), id);
        }
        Assert.False(ModuleCatalog.Built("bogus"));
        Assert.False(ModuleCatalog.Built("levels"));
    }

    [Fact]
    public void Built_IncludesCiAndPhase3dModules()
    {
        // Phase 3c's CI module and Phase 3d's Now Playing, file shelf, devices and camera mirror leave "Coming soon".
        foreach (var id in new[] { "ci", "media", "shelf", "devices", "mirror" })
        {
            Assert.True(ModuleCatalog.Built(id), id);
        }
    }

    [Fact]
    public void Built_IncludesLimits()
    {
        // Phase 3b builds AI limits (with agents, already built).
        Assert.True(ModuleCatalog.Built("limits"));
        Assert.True(ModuleCatalog.Built("agents"));
    }

    [Fact]
    public void Order_KeepsUserOrder_AppendsNew()
    {
        // The user's order first; unknown and off-Windows ids and repeats are dropped; the rest follow in the Mac's order.
        var order = ModuleCatalog.Order(new[] { "timer", "agents", "calendar", "bogus", "timer", "levels" });

        Assert.Equal(
            new[] { "timer", "agents", "calendar", "ci", "media", "shelf", "limits", "battery", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
            order);
    }

    [Fact]
    public void Order_OfNothing_IsTheWindowsModulesInMacOrder()
    {
        Assert.Equal(
            new[] { "agents", "ci", "media", "calendar", "timer", "shelf", "limits", "battery", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
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
        // levels, fans and bogus are hidden or unknown: they keep their positions; agents and timer swap.
        var stored = new[] { "levels", "timer", "fans", "agents", "bogus" };

        var result = ModuleCatalog.Reorder(stored, "agents", -1);

        Assert.Equal(
            new[] { "levels", "agents", "fans", "timer", "bogus", "ci", "media", "calendar", "shelf", "limits", "battery", "devices", "calls", "privacy", "network", "downloads", "health", "mirror" },
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

    [Fact]
    public void Move_DownUpFirstAndLast_MovesOneVisibleModule()
    {
        // The default order of the Windows modules: every index is a visible module.
        var stored = ModuleCatalog.Order(Array.Empty<string>());
        var n = stored.Count;
        bool All(string _) => true;

        // Down: the module at 0 goes to 2, the two after it move up.
        Assert.Equal(new[] { stored[1], stored[2], stored[0] }, ModuleCatalog.Move(stored, 0, 2, All).Take(3));
        // Up: the module at 2 goes to 0.
        Assert.Equal(new[] { stored[2], stored[0], stored[1] }, ModuleCatalog.Move(stored, 2, 0, All).Take(3));
        // To first and to last.
        Assert.Equal(stored[3], ModuleCatalog.Move(stored, 3, 0, All)[0]);
        var last = ModuleCatalog.Move(stored, 0, n - 1, All);
        Assert.Equal(stored[0], last[n - 1]);
        Assert.Equal(stored.Skip(1), last.Take(n - 1));
    }

    [Fact]
    public void Move_OntoItself_AndOutOfRange_NoChange()
    {
        var stored = ModuleCatalog.Order(Array.Empty<string>());
        bool All(string _) => true;

        Assert.Equal(stored, ModuleCatalog.Move(stored, 2, 2, All));
        Assert.Equal(stored, ModuleCatalog.Move(stored, -1, 0, All));
        Assert.Equal(stored, ModuleCatalog.Move(stored, 0, stored.Count, All));
    }
}
