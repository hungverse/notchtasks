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
