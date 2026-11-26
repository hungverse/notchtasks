using System.Text.Json.Nodes;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The profile file (Customize.swift Profile): one .json file with the customization, shared with the Mac.
public sealed class SettingsProfileTests : IDisposable
{
    /// The 22 keys of the Mac's Profile struct, which a Windows export writes under the Mac's names.
    private static readonly string[] MacProfileKeys =
    {
        "modules", "disabled", "expandMode", "hoverDelay", "haptics", "sounds", "systemNotifications", "size", "accent",
        "customAccent", "menuBarIcon", "doneAfter", "limitWarn", "background", "gradientA", "gradientB", "toastStyle", "motion",
        "moduleColors", "placements", "soundMap", "volume",
    };

    /// A profile as the Mac writes it (sorted keys, Profile in Customize.swift): a few keys Windows lacks (haptics, menuBarIcon
    /// is kept), a Mac-only module (levels), a Mac file path as a sound, and a key from a later version.
    private const string MacProfile = """
    {
      "accent" : "custom",
      "background" : "gradient",
      "customAccent" : "#00F5D4",
      "disabled" : [ "timer", "levels" ],
      "doneAfter" : 45,
      "expandMode" : "click",
      "futureKey" : { "anything" : 1 },
      "gradientA" : "#1E1B4B",
      "gradientB" : "#000000",
      "haptics" : false,
      "hoverDelay" : 0.3,
      "limitWarn" : 90,
      "menuBarIcon" : false,
      "moduleColors" : { "agents" : "#FF375F", "ci" : "orange" },
      "modules" : [ "agents", "levels", "ci", "timer", "shelf", "limits", "battery", "network", "downloads", "health" ],
      "motion" : "snappy",
      "placements" : { "ci" : "left", "media" : "auto" },
      "size" : "regular",
      "soundMap" : { "taskDone" : "Ping", "agentAsk" : "/Users/someone/Music/alert.aiff" },
      "sounds" : true,
      "systemNotifications" : true,
      "toastStyle" : "card",
      "volume" : 0.5
    }
    """;

    private readonly string _dir;
    private readonly string _file;

    public SettingsProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nt-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still holding the folder; the OS temp cleaner takes it later.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    [Fact]
    public void Export_WritesTheMacsTwentyTwoKeysInSortedOrder()
    {
        using var store = SettingsStore.Load(_file);

        var json = JsonNode.Parse(SettingsProfile.Export(store))!.AsObject();

        Assert.Equal(MacProfileKeys.OrderBy(k => k, StringComparer.Ordinal), json.Select(pair => pair.Key));
    }

    [Fact]
    public void Export_WritesTheStoredValues_UnderTheMacsNames()
    {
        using var store = SettingsStore.Load(_file);
        store.SetMany(Themes.All.First(t => t.Id == "ocean").Values);
        store.Set("disabled", JsonNode.Parse("[\"timer\",\"ci\"]"));
        store.Set("volume", JsonValue.Create(0.4));

        var json = JsonNode.Parse(SettingsProfile.Export(store))!.AsObject();

        Assert.Equal("gradient", (string?)json["background"]);
        Assert.Equal("custom", (string?)json["accent"]);
        Assert.Equal("#7DD3FC", (string?)json["customAccent"]);
        Assert.Equal("smooth", (string?)json["motion"]);
        Assert.Equal(new[] { "timer", "ci" }, json["disabled"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(0.4, (double?)json["volume"]);
        Assert.Equal("Glass", (string?)json["soundMap"]!["taskDone"]);
    }

    [Fact]
    public void Export_WritesHapticsOn_BecauseTheMacNeedsTheKey()
    {
        using var store = SettingsStore.Load(_file);

        var json = JsonNode.Parse(SettingsProfile.Export(store))!.AsObject();

        Assert.True((bool?)json["haptics"]);
    }

    [Fact]
    public void Read_AMacProfile_GivesTheKeysWindowsHas_AndIgnoresTheRest()
    {
        var read = SettingsProfile.Read(MacProfile);

        Assert.Null(read.Error);
        Assert.Equal("gradient", (string?)read.Values["background"]);
        Assert.Equal("custom", (string?)read.Values["accent"]);
        Assert.Equal("snappy", (string?)read.Values["motion"]);
        Assert.Equal("card", (string?)read.Values["toastStyle"]);
        Assert.Equal("regular", (string?)read.Values["size"]);
        Assert.Equal(45.0, (double?)read.Values["doneAfter"]);
        Assert.Equal(90.0, (double?)read.Values["limitWarn"]);
        Assert.Equal(0.3, (double?)read.Values["hoverDelay"]);
        Assert.Equal(0.5, (double?)read.Values["volume"]);
        Assert.False((bool?)read.Values["menuBarIcon"]);
        Assert.True((bool?)read.Values["sounds"]);

        // Windows calls the disabled set "disabled", as the Mac's profile does: the key is the same, and levels is not on Windows.
        Assert.Equal(new[] { "timer" }, ((JsonArray)read.Values["disabled"]!).Select(n => (string?)n));
        Assert.Equal(
            new[] { "agents", "ci", "timer", "shelf", "limits", "battery", "network", "downloads", "health" },
            ((JsonArray)read.Values["modules"]!).Select(n => (string?)n));
        Assert.Equal("#FF375F", (string?)((JsonObject)read.Values["moduleColors"]!)["agents"]);
        Assert.Null(((JsonObject)read.Values["moduleColors"]!)["ci"]);  // "orange" is not a hex colour
        Assert.Equal("left", (string?)((JsonObject)read.Values["placements"]!)["ci"]);
        Assert.Equal("Ping", (string?)((JsonObject)read.Values["soundMap"]!)["taskDone"]);
    }

    [Fact]
    public void Read_KeysWindowsLacks_AreNotKept()
    {
        var read = SettingsProfile.Read(MacProfile);

        Assert.False(read.Values.ContainsKey("haptics"));
        Assert.False(read.Values.ContainsKey("futureKey"));
    }

    [Fact]
    public void Read_ASoundThatIsAMacFilePath_IsDropped()
    {
        var read = SettingsProfile.Read(MacProfile);

        Assert.False(((JsonObject)read.Values["soundMap"]!).ContainsKey("agentAsk"));
    }

    [Fact]
    public void Read_AnInvalidValue_IsSkippedAndTheRestKept()
    {
        var read = SettingsProfile.Read(
            "{\"background\":\"neon\",\"doneAfter\":1000,\"accent\":\"#zzzzzz\",\"volume\":\"loud\",\"sounds\":\"yes\","
            + "\"motion\":\"bouncy\",\"customAccent\":\"#123456\"}");

        Assert.Null(read.Error);
        Assert.Equal(new[] { "customAccent", "motion" }, read.Values.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"hello\":1}")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Read_NotAProfile_GivesAnErrorAndNoValues(string text)
    {
        var read = SettingsProfile.Read(text);

        Assert.NotNull(read.Error);
        Assert.Equal(L10n.L("This file isn't a NotchTasks profile."), read.Error);
        Assert.Empty(read.Values);
    }

    [Fact]
    public void Export_ThenRead_ThenApply_GivesTheSameProfile()
    {
        using var source = SettingsStore.Load(_file);
        source.SetMany(Themes.All.First(t => t.Id == "glass").Values);
        source.Set("disabled", JsonNode.Parse("[\"timer\",\"shelf\"]"));
        source.Set("moduleColors", JsonNode.Parse("{\"ci\":\"#30D158\"}"));
        source.Set("doneAfter", JsonValue.Create(60.0));
        var exported = SettingsProfile.Export(source);

        var other = Path.Combine(_dir, "other", "settings.json");
        using var target = SettingsStore.Load(other);
        target.SetMany(SettingsProfile.Read(exported).Values);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(exported), JsonNode.Parse(SettingsProfile.Export(target))));
    }
}
