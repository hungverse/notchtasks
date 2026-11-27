using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Updates;
using Xunit;

namespace NotchTasks.Core.Tests.Updates;

// The text tests read L10n's static state, so this class shares the L10n collection and runs one at a time.
[Collection("L10n")]
public sealed class UpdateManifestTests
{
    private const string Sha = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";

    private static string Good(string extra = "") =>
        "{\"version\":\"2.3.0\",\"build\":7,\"url\":\"https://notchtasks.web.app/a.exe\",\"sha256\":\"" + Sha +
        "\",\"minOS\":\"10.0.19045\",\"notes\":{\"en\":\"English\",\"vi\":\"Tiếng Việt\"}" + extra + "}";

    [Fact]
    public void Parse_ValidEntry_KeepsTheFieldsAndLowerCasesTheChecksum()
    {
        var m = UpdateManifest.Parse(JsonNode.Parse(Good()));

        Assert.Equal("2.3.0", m.Version);
        Assert.Equal(7, m.Build);
        Assert.Equal("https://notchtasks.web.app/a.exe", m.Url);
        Assert.Equal(Sha.ToLowerInvariant(), m.Sha256);
        Assert.Equal("10.0.19045", m.MinOS);
    }

    [Theory]
    [InlineData("{\"version\":\"2.0\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"version\":\"2.3.0\",\"build\":\"7\",\"url\":\"https://a.test/a.exe\",\"sha256\":\"" + "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899" + "\",\"minOS\":\"10.0\",\"notes\":{}}")]
    [InlineData("{\"version\":\"2.3.0\",\"build\":0,\"url\":\"https://a.test/a.exe\",\"sha256\":\"" + "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899" + "\",\"minOS\":\"10.0\",\"notes\":{}}")]
    [InlineData("{\"version\":\"2.3.0\",\"build\":7,\"url\":\"http://example.com/a.exe\",\"sha256\":\"" + "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899" + "\",\"minOS\":\"10.0\",\"notes\":{}}")]
    [InlineData("{\"version\":\"2.3.0\",\"build\":7,\"url\":\"https://a.test/a.exe\",\"sha256\":\"xyz\",\"minOS\":\"10.0\",\"notes\":{}}")]
    [InlineData("{\"version\":\"2.3.0\",\"build\":7,\"url\":\"https://a.test/a.exe\",\"sha256\":\"" + "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899" + "\",\"minOS\":\"ten\",\"notes\":{}}")]
    [InlineData("{\"version\":\"2.3.0\",\"build\":7,\"url\":\"https://a.test/a.exe\",\"sha256\":\"" + "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899" + "\",\"minOS\":\"10.0\",\"notes\":{\"en\":5}}")]
    public void Parse_RefusesAnInvalidEntry(string json)
    {
        var ex = Assert.Throws<UpdateFailure>(() => UpdateManifest.Parse(JsonNode.Parse(json)));

        Assert.Equal(UpdateFailureKind.InvalidManifest, ex.Kind);
    }

    [Fact]
    public void Parse_NullNode_IsInvalid()
    {
        Assert.Equal(UpdateFailureKind.InvalidManifest, Assert.Throws<UpdateFailure>(() => UpdateManifest.Parse(null)).Kind);
    }

    [Fact]
    public void NoteFor_FallsBackToEnglishThenToNoNotes()
    {
        var m = UpdateManifest.Parse(JsonNode.Parse(Good()));
        var bare = m with { Notes = new Dictionary<string, string>() };

        Assert.Equal("Tiếng Việt", m.NoteFor("vi"));
        Assert.Equal("English", m.NoteFor("fr"));
        Assert.Equal(L10n.L("No notes."), bare.NoteFor("fr"));
    }

    [Fact]
    public void Failures_CarryTheMacText()
    {
        Assert.Equal(L10n.L("Couldn't download the update."), new UpdateFailure(UpdateFailureKind.Download).Message);
        Assert.Equal(L10n.L("The downloaded file doesn't match its checksum."), new UpdateFailure(UpdateFailureKind.Hash).Message);
        Assert.Equal(L10n.L("Couldn't start the installer."), new UpdateFailure(UpdateFailureKind.Launch).Message);
    }

    [Fact]
    public void LaunchText_HasATranslationInEveryLanguage()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "windows", "l10n", "windows.json")))
        {
            root = Path.GetDirectoryName(root);
        }
        Assert.NotNull(root);
        var table = JsonNode.Parse(File.ReadAllText(Path.Combine(root!, "windows", "l10n", "windows.json")))!.AsObject();

        foreach (var (code, _) in L10n.Languages.Where(l => l.Code != "en"))
        {
            var text = table[code]?["Couldn't start the installer."]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(text), $"no text for {code}");
        }
    }
}
