using System.Globalization;
using NotchTasks.Core;
using Xunit;

namespace NotchTasks.Core.Tests;

// All tests share L10n's static state (Init), so they live in one class and run one at a time.
[Collection("L10n")]
public sealed class L10nTests
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

    private static string RepoRoot() => Path.GetDirectoryName(Path.GetDirectoryName(MacSources()))!;

    [Fact]
    public void En_ReturnsKey()
    {
        L10n.Init(MacSources(), "en");

        Assert.Equal("en", L10n.Current);
        Assert.Equal("Run command", L10n.L("Run command"));
    }

    [Fact]
    public void Ja_Translates()
    {
        L10n.Init(MacSources(), "ja");

        Assert.Equal("ja", L10n.Current);
        Assert.Equal("コマンドを実行", L10n.L("Run command"));
    }

    [Fact]
    public void Legacy_ViKeyMapsToEnglish()
    {
        L10n.Init(MacSources(), "fr");

        Assert.Equal(L10n.L("Waiting for you"), L10n.L("Đang chờ bạn"));
    }

    [Fact]
    public void Missing_ReturnsKey()
    {
        L10n.Init(MacSources(), "ja");

        Assert.Equal("A text nobody translated", L10n.L("A text nobody translated"));
    }

    [Fact]
    public void Placeholders_AreFilledInOrder()
    {
        Assert.Equal("2 và a", L10n.Fill("{1} và {0}", "a", 2));
        Assert.Equal("3 of 5", L10n.L("{0} of {1}", 3, 5));
    }

    [Fact]
    public void Placeholders_NumbersUseInvariantCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("1.5", L10n.Fill("{0}", 1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Theory]
    [InlineData("fr-CA", "fr")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("sw", "en")]
    [InlineData("", "en")]
    public void Match_PicksSupportedLanguage(string preferred, string expected)
    {
        Assert.Equal(expected, L10n.Match(new[] { preferred }));
    }

    [Fact]
    public void Match_SkipsUnsupportedAndTakesNextPreference()
    {
        Assert.Equal("vi", L10n.Match(new[] { "sw", "vi-VN" }));
    }

    [Fact]
    public void Init_EmptyLanguageFollowsCurrentUiCulture()
    {
        L10n.Init(MacSources(), "");

        Assert.Equal(L10n.Match(new[] { CultureInfo.CurrentUICulture.Name }), L10n.Current);
    }

    [Fact]
    public void WindowsOverlay_FillsKeyMissingFromMacTable()
    {
        // "Nothing here yet." is in electron/l10n/windows.json (vi) but not in mac/Sources/l10n-vi.json.
        var folder = Path.Combine(Path.GetTempPath(), "nt-l10n-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(MacSources(), "l10n-*.json"))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }
            File.Copy(Path.Combine(RepoRoot(), "electron", "l10n", "windows.json"), Path.Combine(folder, "windows.json"));

            L10n.Init(folder, "vi");

            Assert.Equal("Chưa có mục nào.", L10n.L("Nothing here yet."));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
