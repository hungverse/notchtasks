using NotchTasks.Core;
using NotchTasks.Core.Sounds;
using Xunit;

namespace NotchTasks.Core.Tests;

// The legacy-name test changes L10n's state (Init), so the class runs in the L10n collection, one test at a time.
[Collection("L10n")]
public sealed class SoundPacksTests
{
    private static string MacSources()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!, "mac", "Sources");
    }

    [Fact]
    public void Packs_MatchElectron()
    {
        // electron/src/core/settings.js SOUND_PACKS: the names, and the sound each pack gives an agent ask.
        Assert.Equal(new[] { "Default", "Minimal", "Lively", "Retro", "Silent" }, SoundPacks.All.Select(p => p.Name));
        Assert.Equal(new[] { "Ping", "Tink", "Submarine", "Ping", "" }, SoundPacks.All.Select(p => p.Map["agentAsk"]));
    }

    [Fact]
    public void DefaultPack_MatchesElectron()
    {
        var map = SoundPacks.All[0].Map;

        Assert.Equal("Glass", map["taskDone"]);
        Assert.Equal("Basso", map["ciFailed"]);
        Assert.Equal("Bottle", map["deviceOff"]);
        Assert.Equal("", map["unplugged"]);
        Assert.Equal("", map["open"]);
    }

    [Fact]
    public void EveryPack_HasEveryEvent()
    {
        var ids = SoundPacks.Events.Select(e => e.Id).ToArray();

        Assert.Equal(22, ids.Length);
        foreach (var (_, map) in SoundPacks.All)
        {
            Assert.Equal(ids.OrderBy(x => x), map.Keys.OrderBy(x => x));
        }
    }

    [Fact]
    public void Events_HaveTheMacEnglishTitles()
    {
        Assert.Equal(("taskDone", "AI / task done"), SoundPacks.Events[0]);
        Assert.Equal(("warning", "Hot computer, disk full"), SoundPacks.Events[19]);
        Assert.Equal(("close", "Notch collapses"), SoundPacks.Events[21]);
    }

    [Fact]
    public void Current_FindsPack()
    {
        Assert.Equal("Lively", SoundPacks.Current(SoundPacks.All[2].Map));
    }

    [Fact]
    public void Current_NullForCustomMap()
    {
        var map = new Dictionary<string, string>(SoundPacks.All[0].Map) { ["taskDone"] = "Hero" };

        Assert.Null(SoundPacks.Current(map));
    }

    [Fact]
    public void Current_MissingEventsCountAsDefault()
    {
        Assert.Equal("Default", SoundPacks.Current(new Dictionary<string, string>()));
    }

    [Fact]
    public void ElectronPackNames_AreLegacyKeysOfThePacks()
    {
        L10n.Init(MacSources(), "en");

        Assert.Equal("Default", L10n.L("Mặc định"));
        Assert.Equal("Minimal", L10n.L("Tối giản"));
        Assert.Equal("Lively", L10n.L("Sôi động"));
        Assert.Equal("Silent", L10n.L("Im lặng"));
    }
}
