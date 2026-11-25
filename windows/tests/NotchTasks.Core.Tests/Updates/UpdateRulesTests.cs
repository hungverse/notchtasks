using NotchTasks.Core.Updates;
using Xunit;

namespace NotchTasks.Core.Tests.Updates;

/// Port of the Mac's Updater.selfTest rules and electron/test/updater.test.js (OS versions, newer builds, allowed URLs).
public sealed class UpdateRulesTests
{
    [Fact]
    public void Footer_FollowsTheMacTable()
    {
        Assert.Equal(FooterState.Current, UpdateRules.Footer(false, false, false, false, false));
        Assert.Equal(FooterState.Checking, UpdateRules.Footer(true, false, false, false, false));
        Assert.Equal(FooterState.UpToDate, UpdateRules.Footer(false, false, false, false, true));
        Assert.Equal(FooterState.Offered, UpdateRules.Footer(false, false, true, false, false));
        Assert.Equal(FooterState.Failed, UpdateRules.Footer(false, false, true, true, false));
    }

    [Fact]
    public void Footer_InstallingShowsTheOffer()
    {
        Assert.Equal(FooterState.Offered, UpdateRules.Footer(true, true, true, true, true));
        Assert.Equal(FooterState.Offered, UpdateRules.Footer(false, true, false, true, false));
    }

    [Fact]
    public void ShouldAnnounce_OncePerBuild()
    {
        Assert.True(UpdateRules.ShouldAnnounce(3, 2));
        Assert.False(UpdateRules.ShouldAnnounce(3, 3));
    }

    [Fact]
    public void IsNewer_MeansAHigherBuild()
    {
        Assert.True(UpdateRules.IsNewer(2, 1));
        Assert.False(UpdateRules.IsNewer(1, 1));
        Assert.False(UpdateRules.IsNewer(1, 2));
    }

    [Theory]
    [InlineData("https://example.com/a.exe", true)]
    [InlineData("http://127.0.0.1:8080/a.exe", true)]
    [InlineData("http://example.com/a.exe", false)]
    [InlineData("file:///C:/a.exe", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    public void IsAllowedUrl_HttpsOrLoopbackOnly(string url, bool expected)
    {
        Assert.Equal(expected, UpdateRules.IsAllowedUrl(url));
    }

    [Fact]
    public void ParseOS_SplitsNumbersAndRefusesTheRest()
    {
        Assert.Equal(new[] { 10, 0, 22631 }, UpdateRules.ParseOS("10.0.22631"));
        Assert.Null(UpdateRules.ParseOS("10.x"));
        Assert.Null(UpdateRules.ParseOS(""));
        Assert.Null(UpdateRules.ParseOS("1.2.3.4"));
    }

    [Fact]
    public void CompareOS_MissingPartsCountAsZero()
    {
        Assert.Equal(-1, UpdateRules.CompareOS(new[] { 10, 0, 19045 }, new[] { 10, 0, 22631 }));
        Assert.Equal(0, UpdateRules.CompareOS(new[] { 10, 0 }, new[] { 10, 0, 0 }));
        Assert.Equal(1, UpdateRules.CompareOS(new[] { 11, 0, 22000 }, new[] { 10, 0, 99999 }));
    }

    [Fact]
    public void BuildOf_MajorMinorAndPatch()
    {
        Assert.Equal(20307, UpdateRules.BuildOf(new Version(2, 3, 7)));
        Assert.Equal(20300, UpdateRules.BuildOf(new Version(2, 3)));
    }
}
