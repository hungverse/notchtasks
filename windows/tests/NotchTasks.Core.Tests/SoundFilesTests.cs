using NotchTasks.Core.Sounds;
using Xunit;

namespace NotchTasks.Core.Tests;

public class SoundFilesTests
{
    [Fact]
    public void Ping_MapsToDingWav()
    {
        Assert.Equal("ding.wav", SoundFiles.FileName("Ping"));
    }

    [Fact]
    public void Glass_MapsToWindowsDing()
    {
        Assert.Equal("Windows Ding.wav", SoundFiles.FileName("Glass"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoSuchSound")]
    [InlineData("ping")]
    [InlineData(null)]
    public void UnknownOrEmpty_IsNull(string? name)
    {
        Assert.Null(SoundFiles.FileName(name));
    }

    [Fact]
    public void AllFourteenPackSoundsHaveFiles()
    {
        string[] names =
        [
            "Basso", "Blow", "Bottle", "Frog", "Funk", "Glass", "Hero",
            "Morse", "Ping", "Pop", "Purr", "Sosumi", "Submarine", "Tink",
        ];
        foreach (var name in names)
        {
            Assert.EndsWith(".wav", SoundFiles.FileName(name));
        }
    }
}
