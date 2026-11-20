using NotchTasks.Core.Media;
using Xunit;

namespace NotchTasks.Core.Tests.Media;

/// MediaApps maps a session's SourceAppUserModelId to a display name and an exe name (the known table, then the fallbacks).
public class MediaAppsTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Chrome", "Google Chrome")]
    [InlineData("MSEdge", "Microsoft Edge")]
    [InlineData("308046B0AF4A39CB", "Firefox")]
    [InlineData("Brave", "Brave")]
    [InlineData("Opera", "Opera")]
    [InlineData("vlc.exe", "VLC")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "Apple Music")]
    public void Name_KnownAumids(string aumid, string expected)
    {
        Assert.Equal(expected, MediaApps.Name(aumid));
    }

    [Fact]
    public void Name_ExeAndPackageFallbacks()
    {
        // An unknown .exe: its file name without the extension.
        Assert.Equal("notepad", MediaApps.Name("notepad.exe"));
        Assert.Equal("Tool", MediaApps.Name("Tool.EXE"));

        // An unknown package: the package family's name before "_", dots turned into spaces.
        Assert.Equal("Microsoft ZuneVideo", MediaApps.Name("Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo"));
        Assert.Equal("Foo Bar", MediaApps.Name("Foo.Bar_abc123!App"));

        // Anything else: the aumid itself.
        Assert.Equal("Some.App", MediaApps.Name("Some.App"));
    }

    [Fact]
    public void Exe_KnownAndUnknown()
    {
        Assert.Equal("chrome.exe", MediaApps.Exe("Chrome"));
        Assert.Equal("spotify.exe", MediaApps.Exe("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));
        Assert.Equal("firefox.exe", MediaApps.Exe("308046B0AF4A39CB"));
        Assert.Equal("vlc.exe", MediaApps.Exe("vlc.exe"));

        // Packaged apps in the table have no exe.
        Assert.Null(MediaApps.Exe("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"));
        Assert.Null(MediaApps.Exe("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"));

        // Unknown: a .exe is lower-cased, anything else (and a package) has none.
        Assert.Equal("tool.exe", MediaApps.Exe("Tool.EXE"));
        Assert.Null(MediaApps.Exe("Some.App"));
        Assert.Null(MediaApps.Exe("Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo"));
    }
}
