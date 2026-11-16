using System.Text.Json.Nodes;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class BadgeEditsTests
{
    private static JsonArray Parse(string json) => (JsonArray)JsonNode.Parse(json)!;

    private static string Id(JsonNode? node) => node!["id"]!.GetValue<string>();

    private static IReadOnlyList<string> IdsOnSide(JsonArray badges, bool right) =>
        badges.Where(n => n!["right"]!.GetValue<bool>() == right).Select(Id).ToList();

    [Fact]
    public void Add_MaxThreePerSide()
    {
        var badges = Parse("[]");
        for (var i = 0; i < 4; i++)
        {
            badges = BadgeEdits.Add(badges, right: false, sprite: null);
        }
        badges = BadgeEdits.Add(badges, right: true, sprite: null);

        Assert.Equal(3, BadgeEdits.CountOnSide(badges, right: false));
        Assert.Equal(1, BadgeEdits.CountOnSide(badges, right: true));
        Assert.Equal(4, badges.Count);
    }

    [Fact]
    public void Add_TakesTheElectronDefaults()
    {
        var badges = BadgeEdits.Add(Parse("[]"), right: true, sprite: null);

        var b = (JsonObject)badges[0]!;
        Assert.Equal("star", b["symbol"]!.GetValue<string>());
        Assert.Equal("circle", b["shape"]!.GetValue<string>());
        Assert.False(b["border"]!.GetValue<bool>());
        Assert.True(b["right"]!.GetValue<bool>());
        Assert.Null(b["image"]);
        Assert.Null(b["text"]);
        Assert.Null(b["color"]);
        Assert.Null(b["fg"]);
        Assert.Null(b["plain"]);
        Assert.Null(b["sprite"]);
        Assert.Null(b["frames"]);
        Assert.Null(b["fps"]);
        Assert.False(string.IsNullOrEmpty(b["id"]!.GetValue<string>()));
    }

    [Fact]
    public void Add_WithSprite_SetsSpriteAndPlain()
    {
        var badges = BadgeEdits.Add(Parse("[]"), right: false, sprite: "px-rocket");

        var b = (JsonObject)badges[0]!;
        Assert.Equal("px-rocket", b["sprite"]!.GetValue<string>());
        Assert.True(b["plain"]!.GetValue<bool>());
    }

    [Fact]
    public void Add_GivesEachBadgeItsOwnId()
    {
        var badges = BadgeEdits.Add(Parse("[]"), right: false, sprite: null);
        badges = BadgeEdits.Add(badges, right: false, sprite: null);

        Assert.NotEqual(Id(badges[0]), Id(badges[1]));
    }

    [Fact]
    public void Remove_ById()
    {
        var badges = Parse("""[{"id":"a","right":false},{"id":"b","right":false},{"id":"c","right":true}]""");

        var result = BadgeEdits.Remove(badges, "b");

        Assert.Equal(new[] { "a" }, IdsOnSide(result, right: false));
        Assert.Equal(new[] { "c" }, IdsOnSide(result, right: true));
    }

    [Fact]
    public void Move_ReordersWithinASide()
    {
        var badges = Parse("""[{"id":"a","right":false},{"id":"d","right":true},{"id":"b","right":false},{"id":"c","right":false}]""");

        var result = BadgeEdits.Move(badges, "c", 0);

        Assert.Equal(new[] { "c", "a", "b" }, IdsOnSide(result, right: false));
        Assert.Equal(new[] { "d" }, IdsOnSide(result, right: true));
    }

    [Fact]
    public void Move_ClampsThePositionAndIgnoresAnUnknownId()
    {
        var badges = Parse("""[{"id":"a","right":false},{"id":"b","right":false}]""");

        Assert.Equal(new[] { "b", "a" }, IdsOnSide(BadgeEdits.Move(badges, "a", 9), right: false));
        Assert.Equal(new[] { "a", "b" }, IdsOnSide(BadgeEdits.Move(badges, "nope", 0), right: false));
    }

    [Fact]
    public void Update_KeepsUnknownFields()
    {
        var badges = Parse("""[{"id":"a","right":false,"custom":"keep","x":5}]""");

        var result = BadgeEdits.Update(badges, "a", b => b["symbol"] = "seg:E734");

        var b = (JsonObject)result[0]!;
        Assert.Equal("seg:E734", b["symbol"]!.GetValue<string>());
        Assert.Equal("keep", b["custom"]!.GetValue<string>());
        Assert.Equal(5, b["x"]!.GetValue<int>());
    }

    [Fact]
    public void Update_UnknownIdLeavesEverythingAsItWas()
    {
        var badges = Parse("""[{"id":"a","right":false,"symbol":"star"}]""");

        var result = BadgeEdits.Update(badges, "zzz", b => b["symbol"] = "changed");

        Assert.Equal("star", result[0]!["symbol"]!.GetValue<string>());
    }

    [Fact]
    public void Edits_DoNotChangeTheInput()
    {
        var badges = Parse("""[{"id":"a","right":false,"symbol":"star"}]""");
        var before = badges.ToJsonString();

        BadgeEdits.Add(badges, right: true, sprite: null);
        BadgeEdits.Remove(badges, "a");
        BadgeEdits.Update(badges, "a", b => b["symbol"] = "changed");
        BadgeEdits.Move(badges, "a", 0);

        Assert.Equal(before, badges.ToJsonString());
    }

    [Fact]
    public void ApplySprite_FillsNothingAndClearsTheSymbolPicture()
    {
        var badge = new JsonObject { ["id"] = "a", ["symbol"] = "seg:E734", ["text"] = "hi", ["image"] = null };

        BadgeEdits.ApplySprite(badge, "px-spark");

        Assert.Equal("px-spark", badge["sprite"]!.GetValue<string>());
        Assert.True(badge["plain"]!.GetValue<bool>());
        Assert.Null(badge["text"]);
        Assert.Equal("seg:E734", badge["symbol"]!.GetValue<string>());
    }

    [Fact]
    public void ApplySymbol_ClearsTheSprite()
    {
        var badge = new JsonObject { ["id"] = "a", ["sprite"] = "px-spark", ["frames"] = 4, ["fps"] = 8 };

        BadgeEdits.ApplySymbol(badge, BadgeEdits.SymbolFor("E734"));

        Assert.Equal("seg:E734", badge["symbol"]!.GetValue<string>());
        Assert.Null(badge["sprite"]);
        Assert.Null(badge["frames"]);
        Assert.Null(badge["fps"]);
    }

    [Fact]
    public void SymbolCodes_AreFourHexDigitsAndUnique()
    {
        Assert.All(BadgeEdits.SymbolCodes, code => Assert.Matches("^[0-9A-F]{4}$", code));
        Assert.Equal(BadgeEdits.SymbolCodes.Count, BadgeEdits.SymbolCodes.Distinct().Count());
        Assert.Equal("seg:E734", BadgeEdits.SymbolFor("E734"));
    }
}
