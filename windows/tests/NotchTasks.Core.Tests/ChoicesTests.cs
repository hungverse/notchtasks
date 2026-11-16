using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class ChoicesTests
{
    [Fact]
    public void Choices_HaveMacDefaultsFirst()
    {
        // The Mac's defaults: auto accent, black background, compact size, island toast, hover opening. Bouncy is the
        // Mac's default motion, kept in the Mac's own order (second), so it is checked for membership.
        Assert.Equal("auto", Choices.Accents[0].Value);
        Assert.Equal("black", Choices.Backgrounds[0].Value);
        Assert.Equal("compact", Choices.Sizes[0].Value);
        Assert.Equal("island", Choices.ToastStyles[0].Value);
        Assert.Equal("hover", Choices.ExpandModes[0].Value);
        Assert.Contains(Choices.Motions, m => m.Value == "bouncy");
    }

    [Fact]
    public void Accents_MatchTheStoredNames()
    {
        Assert.Equal(Appearance.AccentNames, Choices.Accents.Select(a => a.Value));
    }

    [Fact]
    public void Titles_AreTheMacEnglishKeys()
    {
        Assert.Equal(new[] { "Black", "Frosted glass", "Gradient", "Glowing edge" }, Choices.Backgrounds.Select(b => b.TitleKey));
        Assert.Equal(new[] { "Smooth", "Bouncy", "Snappy", "Minimal" }, Choices.Motions.Select(m => m.TitleKey));
        Assert.Equal(new[] { "Compact", "Large" }, Choices.Sizes.Select(s => s.TitleKey));
        Assert.Equal("Custom…", Choices.Accents[^1].TitleKey);
    }

    [Fact]
    public void ToastStyles_HaveTheirBlurbs()
    {
        Assert.All(Choices.ToastStyles, t => Assert.False(string.IsNullOrEmpty(t.BlurbKey)));
        Assert.Equal("Slides down from the notch.", Choices.ToastStyles[0].BlurbKey);
    }
}
