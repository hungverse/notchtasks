using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The Build & CI section of the open pill (alert cards, build cards), the build chips and the alerts chip, the job rows' tool-icon
/// lead and progress line, and the drawn glyphs of the CI symbols.
[Collection("L10n")]
public sealed class CiCardsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Color Tint = Color.FromRgb(0x32, 0xD2, 0xFF);
    private static readonly IReadOnlyDictionary<string, string> NoColours = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, JsonObject> NoBadges = new Dictionary<string, JsonObject>();

    private static JobAlert Alert(string id, string url = "https://example.test/run/7") =>
        new(id, "Build", "repo", "failure", url);

    private static ItemState Build(string id, string? tool = null, double? progress = null, string symbol = "hammer.fill") =>
        new(new WatchItem(id, "Build " + id, WatchKind.Dev), new[] { new Job(id + "-1", "Gradle", "build", true, null, progress, symbol, tool) });

    private static JsonObject Badge(string symbol = "seg:E90F", string color = "#30D158") =>
        new() { ["symbol"] = symbol, ["color"] = color };

    /// Every element of a type in the logical tree under root.
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }
            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    [Fact]
    public void CiCards_AlertRowDismissCallsModule()
    {
        Sta.Run(() =>
        {
            string? dismissed = null;
            var cards = new CiCards(id => dismissed = id, _ => { });
            var panel = Assert.IsType<StackPanel>(cards.Sync(new[] { Alert("gh-7") }, Array.Empty<ItemState>(), NoBadges, NoColours, null, null, Now));
            var card = Assert.Single(panel.Children.OfType<AlertCard>());

            card.HideButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("gh-7", dismissed);
            Assert.Equal("Hide alert", card.HideButton.ToolTip);
        });
    }

    [Fact]
    public void CiCards_AlertRowOpensItsLog_WhenItHasAUrl()
    {
        Sta.Run(() =>
        {
            string? opened = null;
            var cards = new CiCards(_ => { }, url => opened = url);
            var panel = Assert.IsType<StackPanel>(cards.Sync(new[] { Alert("gh-7") }, Array.Empty<ItemState>(), NoBadges, NoColours, null, null, Now));
            var card = Assert.Single(panel.Children.OfType<AlertCard>());

            Assert.True(card.ViewLogShown);
            card.OpenLog();

            Assert.Equal("https://example.test/run/7", opened);
        });
    }

    [Fact]
    public void CiCards_NoViewLogPill_WithoutAUrl()
    {
        Sta.Run(() =>
        {
            var cards = new CiCards(_ => { }, _ => { });
            var panel = Assert.IsType<StackPanel>(cards.Sync(new[] { Alert("gh-7", url: null!) }, Array.Empty<ItemState>(), NoBadges, NoColours, null, null, Now));

            Assert.False(Assert.Single(panel.Children.OfType<AlertCard>()).ViewLogShown);
        });
    }

    [Fact]
    public void CiCards_UpdateInPlace_SameChildren()
    {
        Sta.Run(() =>
        {
            var cards = new CiCards(_ => { }, _ => { });
            var alerts = new[] { Alert("gh-1"), Alert("gh-2") };
            var builds = new[] { Build("b1"), Build("b2") };

            var first = Assert.IsType<StackPanel>(cards.Sync(alerts, builds, NoBadges, NoColours, null, null, Now));
            var before = first.Children.Cast<UIElement>().ToList();
            var second = Assert.IsType<StackPanel>(cards.Sync(alerts, builds, NoBadges, NoColours, null, null, Now.AddSeconds(2)));

            Assert.Same(first, second);
            Assert.Equal(4, second.Children.Count);
            Assert.All(Enumerable.Range(0, before.Count), i => Assert.Same(before[i], second.Children[i]));
        });
    }

    [Fact]
    public void CiCards_NothingToShow_IsNull()
    {
        Sta.Run(() =>
        {
            var cards = new CiCards(_ => { }, _ => { });

            Assert.Null(cards.Sync(Array.Empty<JobAlert>(), Array.Empty<ItemState>(), NoBadges, NoColours, null, null, Now));
        });
    }

    [Fact]
    public void CiCards_AlertsComeBeforeBuildCards()
    {
        Sta.Run(() =>
        {
            var cards = new CiCards(_ => { }, _ => { });
            var panel = Assert.IsType<StackPanel>(cards.Sync(new[] { Alert("gh-1") }, new[] { Build("b1") }, NoBadges, NoColours, null, null, Now));

            Assert.IsType<AlertCard>(panel.Children[0]);
            Assert.IsType<ItemCard>(panel.Children[1]);
        });
    }

    [Fact]
    public void ItemCard_ToolIconLead_WhenSet()
    {
        Sta.Run(() =>
        {
            var card = new ItemCard();
            var badges = new Dictionary<string, JsonObject> { ["Gradle"] = Badge() };

            card.Update(Build("b1", tool: "Gradle"), Array.Empty<LimitWindow>(), Tint, null, Now, badges);

            Assert.IsType<Grid>(card.RowLeads[0]);  // the badge, drawn as the Icons pane draws it
        });
    }

    [Fact]
    public void ItemCard_SymbolLead_WhenNoToolIcon()
    {
        Sta.Run(() =>
        {
            var card = new ItemCard();

            card.Update(Build("b1", tool: "Gradle"), Array.Empty<LimitWindow>(), Tint, null, Now, NoBadges);

            Assert.IsType<Path>(card.RowLeads[0]);  // the hammer vector
        });
    }

    [Fact]
    public void ItemCard_ProgressLine_WhenProgress()
    {
        Sta.Run(() =>
        {
            var card = new ItemCard();

            card.Update(Build("b1", progress: 0.4), Array.Empty<LimitWindow>(), Tint, null, Now, NoBadges);
            Assert.Equal(1, card.ProgressLines);

            card.Update(Build("b1"), Array.Empty<LimitWindow>(), Tint, null, Now, NoBadges);
            Assert.Equal(0, card.ProgressLines);
        });
    }

    [Fact]
    public void AlertsChip_Trail_OneAndMany()
    {
        Sta.Run(() =>
        {
            var one = new Chip(ChipKind.Alerts, "ci", "alerts") { AlertCount = 1 };
            var many = new Chip(ChipKind.Alerts, "ci", "alerts") { AlertCount = 3 };

            Assert.Equal("1 error", Assert.IsType<TextBlock>(ChipView.Trail(one, 11, null, NoColours, Now)).Text);
            Assert.Equal("3 errors", Assert.IsType<TextBlock>(ChipView.Trail(many, 11, null, NoColours, Now)).Text);
        });
    }

    [Fact]
    public void AlertsChip_LeadIsTheWarningGlyph()
    {
        Sta.Run(() =>
        {
            var chip = new Chip(ChipKind.Alerts, "ci", "alerts") { AlertCount = 1 };

            var lead = Assert.IsType<TextBlock>(ChipView.Lead(chip, 18, null, NoColours));

            Assert.Equal(ModuleGlyphs.Get("warning"), lead.Text);
        });
    }

    [Fact]
    public void CiSymbols_EachDrawsAPath()
    {
        // every job symbol the CI rules and the rows use has a drawn path that parses
        foreach (var symbol in new[]
                 {
                     "hammer.fill", "shippingbox.fill", "checkmark.diamond.fill", "icloud.and.arrow.up.fill", "server.rack",
                     "arrow.down.circle.fill", "doc.on.doc.fill", "film.stack", "archivebox.fill", "arrow.triangle.branch",
                     "arrow.left.arrow.right", "photo.stack.fill", "waveform", "cube.transparent.fill", "airplane", "xmark",
                 })
        {
            var data = ModuleGlyphs.Vector(symbol);
            Assert.False(string.IsNullOrEmpty(data), symbol);
            Assert.NotNull(Geometry.Parse(data!));
        }
    }
}
