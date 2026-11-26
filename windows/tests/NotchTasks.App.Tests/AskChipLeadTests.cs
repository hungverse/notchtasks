using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The ask chip's lead is the asking agent's icon with an orange hand badge (Mac ask chip), so a waiting agent shows who it is.
public sealed class AskChipLeadTests
{
    private static readonly IReadOnlyDictionary<string, string> NoColours = new Dictionary<string, string>();

    [Fact]
    public void AskLead_WithTheAgentsItem_IsTheIconWithAHandBadge()
    {
        Sta.Run(() =>
        {
            var claude = new WatchItem("claude-1", "Claude", WatchKind.Claude);
            var chip = new Chip(ChipKind.Ask, "agents", "ask") { AskCount = 1, AskItem = claude };

            var lead = Assert.IsType<Grid>(ChipView.Lead(chip, 18, null, NoColours));

            // the icon, then the badge over its corner
            Assert.Equal(2, lead.Children.Count);
            var badge = Assert.IsType<Border>(lead.Children[1]);
            var hand = Assert.IsType<TextBlock>(badge.Child);
            Assert.Equal(ModuleGlyphs.Get("hand").Glyph, hand.Text);
        });
    }

    [Fact]
    public void AskLead_BadgeOverhangsTheIconCornerAsTheMacs()
    {
        // Mac offset(x: 0.2 × size, y: 0.14 × size): the badge reaches past the icon's bottom-right corner
        Sta.Run(() =>
        {
            var chip = new Chip(ChipKind.Ask, "agents", "ask") { AskCount = 1, AskItem = Claude };

            var lead = Assert.IsType<Grid>(ChipView.Lead(chip, 18, null, NoColours));
            var badge = Assert.IsType<Border>(lead.Children[1]);

            Assert.Equal(-18 * 0.2, badge.Margin.Right, 6);
            Assert.Equal(-18 * 0.14, badge.Margin.Bottom, 6);
        });
    }

    [Fact]
    public void AskChip_PulsesItsBadgeWhileAnimated()
    {
        Sta.Run(() =>
        {
            var (left, right, window) = AskHost();
            ChipView.Fill(left, right, AskChips(), NoPlacements, NoColours, null, DateTimeOffset.Now, null, animationsOn: true);

            // Loaded is raised once the lead is in the loaded window, so the loop starts after the dispatcher has run
            var badge = BadgeOf(left);
            Fixture.PumpUntil(() => badge.HasAnimatedProperties);
            Assert.True(badge.HasAnimatedProperties);
            window.Close();
        });
    }

    [Fact]
    public void AskChip_StaysStillUnderReducedMotion()
    {
        Sta.Run(() =>
        {
            var (left, right, window) = AskHost();
            ChipView.Fill(left, right, AskChips(), NoPlacements, NoColours, null, DateTimeOffset.Now, null,
                motion: "reduced", animationsOn: true);

            Assert.False(BadgeOf(left).HasAnimatedProperties);
            window.Close();
        });
    }

    [Fact]
    public void AskChip_KeepsItsLeadAcrossSeconds()
    {
        // the signature has no clock second for an ask, so a second later the same lead element stays on screen (the pulse carries on)
        Sta.Run(() =>
        {
            var (left, right, window) = AskHost();
            var now = DateTimeOffset.Now;
            ChipView.Fill(left, right, AskChips(), NoPlacements, NoColours, null, now, null, animationsOn: false);
            var first = AskPanel(left);
            var leadBefore = first.Children[0];

            ChipView.Fill(left, right, AskChips(), NoPlacements, NoColours, null, now.AddSeconds(1), null, animationsOn: false);

            var second = AskPanel(left);
            Assert.Same(first, second);
            Assert.Same(leadBefore, second.Children[0]);
            window.Close();
        });
    }

    private static readonly IReadOnlyDictionary<string, string> NoPlacements = new Dictionary<string, string>();

    private static readonly WatchItem Claude = new("claude-1", "Claude", WatchKind.Claude);

    private static IReadOnlyList<Chip> AskChips() => new[] { new Chip(ChipKind.Ask, "agents", "ask") { AskCount = 1, AskItem = Claude } };

    /// A loaded window with the two wings, as the pill has them.
    private static (StackPanel Left, StackPanel Right, Window Window) AskHost()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(left);
        host.Children.Add(right);
        var window = new Window { Content = host, Width = 400, Height = 60 };
        Fixture.ShowOffScreen(window);
        return (left, right, window);
    }

    /// The ask chip's panel in the left wing: its lead is the grid with the badge.
    private static StackPanel AskPanel(StackPanel left) =>
        (StackPanel)Assert.Single(left.Children.OfType<FrameworkElement>(), e => e.Tag is "chip");

    /// The hand badge of the ask chip's lead (the border over the icon's corner).
    private static Border BadgeOf(StackPanel left) =>
        (Border)((Grid)AskPanel(left).Children[0]).Children[1];

    [Fact]
    public void AskLead_WithoutAnItem_IsTheHandAlone()
    {
        Sta.Run(() =>
        {
            var chip = new Chip(ChipKind.Ask, "agents", "ask") { AskCount = 2 };

            var lead = Assert.IsType<TextBlock>(ChipView.Lead(chip, 18, null, NoColours));

            Assert.Equal(ModuleGlyphs.Get("hand").Glyph, lead.Text);
        });
    }
}
