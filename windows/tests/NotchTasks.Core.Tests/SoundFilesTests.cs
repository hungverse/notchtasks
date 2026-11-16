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
    public void Names_ListTheFourteenSortedByName()
    {
        Assert.Equal(14, SoundFiles.Names.Count);
        Assert.Equal(SoundFiles.Names.OrderBy(n => n, StringComparer.Ordinal), SoundFiles.Names);
        Assert.Equal("Basso", SoundFiles.Names[0]);
        Assert.Equal("Tink", SoundFiles.Names[^1]);
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
