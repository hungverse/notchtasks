using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The cards update in place on a publish that changed nothing, the tint is one function shared by the card and its chip, and an
/// app item launches by its program name.
public sealed class ItemCardUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, string> NoColours = new Dictionary<string, string>();

    private static ItemState Agent(string id, WatchKind kind = WatchKind.Process, string? icon = null, params Job[] jobs) =>
        new(new WatchItem(id, "Agent " + id, kind, Icon: icon), jobs.Length == 0
            ? new[] { new Job("1001", "Task", "3%", true, Symbol: "checkmark.circle") }
            : jobs);

    private static ProviderLimits Claude(params double[] used) => new("claude", "claude", null,
        used.Select((u, i) => new LimitWindow(i == 0 ? "5h" : "7d", u, Now.AddHours(2))).ToList(), Now);

    [Fact]
    public void UnchangedPublish_KeepsBadgesIconAndRowLeads()
    {
        Sta.Run(() =>
        {
            var cards = new AgentCards();
            var running = new[]
            {
                Agent("claude", WatchKind.Claude, null,
                    new Job("1001", "Task one", "3%", true, Symbol: "checkmark.circle"),
                    new Job("1002", "Task two", "1%", false, Url: "https://example.com/log")),
            };
            var providers = new[] { Claude(40, 95) };

            cards.Sync(null, running, providers, NoColours, null, null, Now);
            var card = Assert.Single(cards.Panel.Children.OfType<ItemCard>());
            var badges = card.BadgeChildren.ToList();
            var icon = card.IconContent;
            var leads = card.RowLeads.ToList();
            Assert.Equal(2, badges.Count);

            // a second publish with the same state: nothing is rebuilt
            cards.Sync(null, running, providers, NoColours, null, null, Now.AddSeconds(2));
            Assert.Equal(badges.Count, card.BadgeChildren.Count);
            for (var i = 0; i < badges.Count; i++)
            {
                Assert.Same(badges[i], card.BadgeChildren[i]);
            }
            Assert.Same(icon, card.IconContent);
            Assert.Equal(leads.Count, card.RowLeads.Count);
            for (var i = 0; i < leads.Count; i++)
            {
                Assert.Same(leads[i], card.RowLeads[i]);
            }
        });
    }

    [Fact]
    public void ChangedLimit_RefitsTheBadges()
    {
        Sta.Run(() =>
        {
            var cards = new AgentCards();
            var running = new[] { Agent("claude", WatchKind.Claude) };
            cards.Sync(null, running, new[] { Claude(40) }, NoColours, null, null, Now);
            var card = Assert.Single(cards.Panel.Children.OfType<ItemCard>());
            var before = card.BadgeChildren.ToList();

            cards.Sync(null, running, new[] { Claude(55) }, NoColours, null, null, Now);
            Assert.NotSame(before[0], card.BadgeChildren[0]);
        });
    }

    [Fact]
    public void CardAndChip_UseOneTint()
    {
        Sta.Run(() =>
        {
            var accent = Color.FromRgb(0x11, 0x22, 0x33);
            var state = Agent("tinted", WatchKind.Process, "{\"color\":\"#AABBCC\"}");
            var cards = new AgentCards();
            cards.Sync(null, new[] { state }, Array.Empty<ProviderLimits>(), NoColours, accent, null, Now);
            var card = Assert.Single(cards.Panel.Children.OfType<ItemCard>());

            var chip = new Chip(ChipKind.Agent, "agents", "agent-tinted") { Agent = state };
            Assert.Equal(card.Tint, ChipView.TintOf(chip, accent, NoColours));
            Assert.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), card.Tint);
        });
    }

    [Fact]
    public void Tint_OrderIsModuleColour_IconColour_Accent_ThenPalette()
    {
        var accent = Color.FromRgb(0x11, 0x22, 0x33);
        var iconItem = new WatchItem("i", "I", WatchKind.Process, Icon: "{\"color\":\"#AABBCC\"}");
        var plain = new WatchItem("p", "P", WatchKind.Process);
        var module = new Dictionary<string, string> { ["agents"] = "#00FF00" };

        Assert.Equal(Color.FromRgb(0x00, 0xFF, 0x00), ItemTint.Of(iconItem, module, accent));  // module colour first
        Assert.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), ItemTint.Of(iconItem, NoColours, accent));  // then the icon colour
        Assert.Equal(accent, ItemTint.Of(plain, NoColours, accent));  // then the accent
        var palette = ItemTint.Of(plain, NoColours, null);
        Assert.Equal(palette, ItemTint.Of(plain, NoColours, null));  // a palette pick is stable per item
    }

    [Fact]
    public void Tint_CodexIsItsBlue_WhenNothingElseSets_It()
    {
        var codex = new WatchItem("c", "Codex", WatchKind.Codex, BundleID: "com.openai.codex");
        Assert.Equal(Color.FromRgb(0x6E, 0x8B, 0xFF), ItemTint.Of(codex, NoColours, null));
    }

    [Fact]
    public void LaunchTarget_IsTheProgramName_ForAPresetApp()
    {
        var code = new WatchItem("code", "Code", WatchKind.App, BundleID: "Code.exe");
        var antigravity = new WatchItem("agy", "Antigravity", WatchKind.App, BundleID: "com.google.antigravity");
        Assert.Equal("Code.exe", ItemCard.LaunchTarget(code));
        Assert.Equal("Antigravity", ItemCard.LaunchTarget(antigravity));
        Assert.Null(ItemCard.LaunchTarget(new WatchItem("none", "None", WatchKind.App, BundleID: "dev.unknown.app")));
    }
}
