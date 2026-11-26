using NotchTasks.Core.Levels;
using Xunit;

namespace NotchTasks.Core.Tests.Levels;

/// The volume and brightness bar's logic (the Mac's sampleLevels): the first reading is a baseline and shows nothing, a move of more
/// than 0.004 shows the bar, a new output device's volume is a baseline (not the user's change), and the bar hides when its time is up.
public sealed class LevelsModuleTests
{
    private static LevelsModule Module() => new();

    [Fact]
    public void FirstVolumeReading_IsTheBaseline_AndShowsNothing()
    {
        var module = Module();

        Assert.False(module.Volume(new VolumeReading(0.9, false)));
        Assert.Null(module.Hud);
    }

    [Fact]
    public void VolumeMove_ShowsTheVolumeBar()
    {
        var module = Module();
        module.SetVolumeBaseline(new VolumeReading(0.5, false));

        Assert.True(module.Volume(new VolumeReading(0.6, false)));

        Assert.Equal(new LevelHud(LevelKind.Volume, 0.6, false), module.Hud);
    }

    [Fact]
    public void SmallMove_ShowsNothing_ButMovesTheBaseline()
    {
        // 0.004 is the Mac's step: a smaller move is no change. The baseline still follows the volume, so a slow drift adds up to a bar.
        var module = Module();
        module.SetVolumeBaseline(new VolumeReading(0.5, false));

        Assert.False(module.Volume(new VolumeReading(0.503, false)));
        Assert.False(module.Volume(new VolumeReading(0.506, false)));
        Assert.Null(module.Hud);
    }

    [Fact]
    public void MuteToggle_ShowsTheMutedBar()
    {
        var module = Module();
        module.SetVolumeBaseline(new VolumeReading(0.5, false));

        Assert.True(module.Volume(new VolumeReading(0.5, true)));

        Assert.Equal(new LevelHud(LevelKind.Volume, 0.5, true), module.Hud);
    }

    [Fact]
    public void BrightnessMove_ShowsTheBrightnessBar()
    {
        var module = Module();
        module.SetBrightnessBaseline(0.4);

        Assert.True(module.Brightness(0.6));

        Assert.Equal(new LevelHud(LevelKind.Brightness, 0.6), module.Hud);
    }

    [Fact]
    public void SmallBrightnessMove_ShowsNothing()
    {
        var module = Module();
        module.SetBrightnessBaseline(0.4);

        Assert.False(module.Brightness(0.402));
        Assert.Null(module.Hud);
    }

    [Fact]
    public void DeviceChange_ReBaselinesTheVolume_SoTheJumpShowsNothing()
    {
        // A new output (headphones connecting) brings its own volume: that is not the user turning it, as on the Mac.
        var module = Module();
        module.SetVolumeBaseline(new VolumeReading(0.2, false));

        module.SetVolumeBaseline(new VolumeReading(0.8, false));

        Assert.False(module.Volume(new VolumeReading(0.8, false)));
        Assert.Null(module.Hud);
        Assert.True(module.Volume(new VolumeReading(0.3, false)));
    }

    [Fact]
    public void Changed_IsRaisedWhenABarShows_AndNotForAQuietReading()
    {
        var module = Module();
        var raised = 0;
        module.Changed += () => raised++;
        module.SetVolumeBaseline(new VolumeReading(0.5, false));

        module.Volume(new VolumeReading(0.5, false));
        Assert.Equal(0, raised);

        module.Volume(new VolumeReading(0.7, false));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Hide_ClearsTheBar_AndRaisesChanged_OnlyWhenOneShows()
    {
        var module = Module();
        var raised = 0;
        module.Changed += () => raised++;
        module.Hide();
        Assert.Equal(0, raised);

        module.SetVolumeBaseline(new VolumeReading(0.5, false));
        module.Volume(new VolumeReading(0.9, false));
        module.Hide();

        Assert.Null(module.Hud);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Clear_ForgetsTheBaselines_SoTheNextReadingIsAFirstOne()
    {
        // A module that is turned off and on again starts from a fresh baseline: the first reading after it shows nothing.
        var module = Module();
        module.SetVolumeBaseline(new VolumeReading(0.1, false));
        module.Volume(new VolumeReading(0.9, false));

        module.Clear();

        Assert.Null(module.Hud);
        Assert.False(module.Volume(new VolumeReading(0.95, false)));
    }

    [Theory]
    [InlineData(0.0, false, "volume.off")]
    [InlineData(0.9, true, "volume.off")]
    [InlineData(0.0, true, "volume.off")]
    [InlineData(0.33, false, "volume.low")]
    [InlineData(0.34, false, "volume.mid")]
    [InlineData(0.66, false, "volume.mid")]
    [InlineData(0.67, false, "volume.high")]
    [InlineData(1.0, false, "volume.high")]
    public void SymbolName_FollowsTheMacsVolumeSteps(double value, bool muted, string expected) =>
        Assert.Equal(expected, LevelsModule.SymbolName(new LevelHud(LevelKind.Volume, value, muted)));

    [Theory]
    [InlineData(0.49, "brightness.low")]
    [InlineData(0.5, "brightness.high")]
    public void SymbolName_FollowsTheMacsBrightnessSteps(double value, string expected) =>
        Assert.Equal(expected, LevelsModule.SymbolName(new LevelHud(LevelKind.Brightness, value)));
}
