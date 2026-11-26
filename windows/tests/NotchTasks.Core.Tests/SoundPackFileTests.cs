using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NotchTasks.Core.Sounds;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class SoundPackFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "NotchTasksPackTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private SoundPackFile Pack() => new(Path.Combine(_folder, "Sounds"));

    private string WriteSource(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] Wav = Encoding.ASCII.GetBytes("RIFFfakewavebytes");
    private static readonly string[] System14 = SoundFiles.Names.ToArray();

    [Fact]
    public void RoundTrip_KeepsBuiltInFileAndSilentEvents()
    {
        var source = WriteSource("my bell.wav", Wav);
        var map = new Dictionary<string, string>
        {
            ["taskDone"] = "Glass",
            ["agentAsk"] = "",
            ["ciFailed"] = source,
        };

        var json = Pack().Export(map, 0.5);
        var imported = Pack().Import(json, System14);

        Assert.Null(imported.Error);
        Assert.Equal("Glass", imported.Map["taskDone"]);
        Assert.Equal("", imported.Map["agentAsk"]);
        var kept = imported.Map["ciFailed"];
        Assert.Equal(Path.Combine(_folder, "Sounds", "my bell.wav"), kept);
        Assert.Equal(Wav, File.ReadAllBytes(kept));
        Assert.Equal(0.5, imported.Volume);
    }

    [Fact]
    public void Export_WritesTheMacFormat()
    {
        var source = WriteSource("chime.mp3", Wav);
        var json = Pack().Export(new Dictionary<string, string> { ["open"] = source, ["close"] = "Tink" }, 0.8);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("notchtasks.sounds", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(0.8, root.GetProperty("volume").GetDouble());
        Assert.Equal("file:chime.mp3", root.GetProperty("sounds").GetProperty("open").GetString());
        Assert.Equal("Tink", root.GetProperty("sounds").GetProperty("close").GetString());
        Assert.Equal(Convert.ToBase64String(Wav), root.GetProperty("files").GetProperty("chime.mp3").GetString());
    }

    [Fact]
    public void Export_DropsAMissingFile()
    {
        var json = Pack().Export(new Dictionary<string, string> { ["open"] = Path.Combine(_folder, "gone.wav") }, 0.8);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("sounds").TryGetProperty("open", out _));
    }

    [Fact]
    public void Store_NumbersTheNameWhenADifferentFileHasIt()
    {
        var pack = Pack();
        var first = pack.Store(Wav, "bell.wav");
        var second = pack.Store(Encoding.ASCII.GetBytes("other bytes"), "bell.wav");

        Assert.Equal(Path.Combine(_folder, "Sounds", "bell.wav"), first);
        Assert.Equal(Path.Combine(_folder, "Sounds", "bell 2.wav"), second);
    }

    [Fact]
    public void Store_ReusesTheFileWhenTheBytesAreTheSame()
    {
        var pack = Pack();
        var first = pack.Store(Wav, "bell.wav");
        var again = pack.Store(Wav, "bell.wav");

        Assert.Equal(first, again);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "Sounds")));
    }

    [Fact]
    public void Store_SanitisesThePathAndRefusesOtherTypes()
    {
        var pack = Pack();
        var stored = pack.Store(Wav, "..\\..\\evil.wav");

        Assert.NotNull(stored);
        Assert.Equal(Path.Combine(_folder, "Sounds", "evil.wav"), stored);
        Assert.Equal(Path.Combine(_folder, "Sounds", "hidden.wav"), pack.Store(Wav, ".hidden.wav"));
        Assert.Null(pack.Store(Wav, "song.aiff"));
    }

    [Theory]
    [InlineData("CON.wav")]
    [InlineData("nul.mp3")]
    [InlineData("COM1.wav")]
    [InlineData("lpt9.m4a")]
    [InlineData("aux.wma")]
    public void Store_RefusesWindowsDeviceNames(string name)
    {
        Assert.Null(Pack().Store(Wav, name));
    }

    [Fact]
    public void Store_AllowsNamesThatOnlyLookLikeDevices()
    {
        Assert.Equal(Path.Combine(_folder, "Sounds", "COM10.wav"), Pack().Store(Wav, "COM10.wav"));
    }

    [Fact]
    public void Import_RefusesAFileOverTheLimit_AndKeepsTheRest()
    {
        var big = Convert.ToBase64String(new byte[SoundPackFile.MaxFile + 1]);
        var json = new JsonObject
        {
            ["format"] = SoundPackFile.Format,
            ["version"] = 1,
            ["sounds"] = new JsonObject { ["open"] = "file:big.wav", ["close"] = "file:small.wav" },
            ["files"] = new JsonObject { ["big.wav"] = big, ["small.wav"] = Convert.ToBase64String(Wav) },
        }.ToJsonString();

        var result = Pack().Import(json, System14);

        Assert.Null(result.Error);
        Assert.False(result.Map.ContainsKey("open"));
        Assert.True(File.Exists(result.Map["close"]));
    }

    [Fact]
    public void Import_SkipsMalformedBase64_AndKeepsTheRest()
    {
        var json = new JsonObject
        {
            ["format"] = SoundPackFile.Format,
            ["version"] = 1,
            ["sounds"] = new JsonObject { ["open"] = "file:bad.wav", ["close"] = "file:good.wav" },
            ["files"] = new JsonObject { ["bad.wav"] = "!!! not base64 !!!", ["good.wav"] = Convert.ToBase64String(Wav) },
        }.ToJsonString();

        var result = Pack().Import(json, System14);

        Assert.Null(result.Error);
        Assert.False(result.Map.ContainsKey("open"));
        Assert.Equal(Wav, File.ReadAllBytes(result.Map["close"]));
    }

    [Fact]
    public void Import_TraversalName_StaysInsideTheFolder()
    {
        var json = new JsonObject
        {
            ["format"] = SoundPackFile.Format,
            ["version"] = 1,
            ["sounds"] = new JsonObject { ["open"] = "file:../../evil.wav" },
            ["files"] = new JsonObject { ["../../evil.wav"] = Convert.ToBase64String(Wav) },
        }.ToJsonString();

        var result = Pack().Import(json, System14);

        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(_folder, "Sounds", "evil.wav"), result.Map["open"]);
        Assert.Equal(Path.Combine(_folder, "Sounds"), Path.GetDirectoryName(result.Map["open"]));
    }

    [Fact]
    public void Store_RefusesAFileOverTheLimit()
    {
        Assert.Null(Pack().Store(new byte[SoundPackFile.MaxFile + 1], "big.wav"));
    }

    [Fact]
    public void Keep_CopiesAChosenFileIntoTheFolder()
    {
        var source = WriteSource("ping.m4a", Wav);
        var kept = Pack().Keep(source);

        Assert.Equal(Path.Combine(_folder, "Sounds", "ping.m4a"), kept);
        Assert.Equal(Wav, File.ReadAllBytes(kept!));
    }

    [Fact]
    public void Keep_RefusesAnUnsupportedTypeAndAHugeFile()
    {
        Assert.Null(Pack().Keep(WriteSource("song.aiff", Wav)));
        Assert.Null(Pack().Keep(WriteSource("huge.wav", new byte[SoundPackFile.MaxFile + 1])));
    }

    [Fact]
    public void Import_NonPackJson_IsAnError()
    {
        var result = Pack().Import("{\"hello\": 1}", System14);

        Assert.Equal("This file isn't a NotchTasks sound pack.", result.Error);
        Assert.Empty(result.Map);
    }

    [Fact]
    public void Import_NotJson_IsAnError()
    {
        Assert.Equal("This file isn't a NotchTasks sound pack.", Pack().Import("not json at all", System14).Error);
    }

    [Fact]
    public void Import_OnlyAiffFiles_IsNoUsableSounds()
    {
        var json = """
            {"format":"notchtasks.sounds","version":1,"volume":0.7,
             "sounds":{"open":"file:a.aiff","close":"file:b.caf"},
             "files":{"a.aiff":"AAAA","b.caf":"AAAA"}}
            """;

        var result = Pack().Import(json, System14);

        Assert.Equal("No usable sounds in this file.", result.Error);
        Assert.Empty(result.Map);
    }

    [Fact]
    public void Import_SkipsAnUnknownBuiltInNameAndAnUnknownEvent()
    {
        var json = """
            {"format":"notchtasks.sounds","version":1,
             "sounds":{"open":"Pop","close":"NoSuchSound","nonEvent":"Pop"}}
            """;

        var result = Pack().Import(json, System14);

        Assert.Null(result.Error);
        Assert.Equal(new Dictionary<string, string> { ["open"] = "Pop" }, result.Map);
    }

    [Fact]
    public void Import_ClampsTheVolume()
    {
        var high = Pack().Import("{\"format\":\"notchtasks.sounds\",\"version\":1,\"volume\":3,\"sounds\":{\"open\":\"Pop\"}}", System14);
        var low = Pack().Import("{\"format\":\"notchtasks.sounds\",\"version\":1,\"volume\":-2,\"sounds\":{\"open\":\"Pop\"}}", System14);
        var none = Pack().Import("{\"format\":\"notchtasks.sounds\",\"version\":1,\"sounds\":{\"open\":\"Pop\"}}", System14);

        Assert.Equal(1.0, high.Volume);
        Assert.Equal(0.0, low.Volume);
        Assert.Null(none.Volume);
    }
}
