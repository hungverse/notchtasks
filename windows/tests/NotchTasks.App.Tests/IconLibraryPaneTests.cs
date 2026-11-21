using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The Icon Library pane's use menu (the Mac's useMenu): its sections and entries, the full side that is disabled, and the writes
/// a choice makes (a watched app's Icon, a build tool's icon). Every test works in a temp settings folder.
[Collection("L10n")]
public sealed class IconLibraryPaneTests
{
    /// Writes the settings file with this JSON, then loads the store over it.
    private static SettingsStore StoreWith(string json)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        return SettingsStore.Load(AppPaths.SettingsFile);
    }

    private const string TwoItemsAndATool =
        "{\"items\":["
        + "{\"id\":\"a1\",\"name\":\"Alpha\",\"kind\":\"process\",\"process\":\"alpha\",\"enabled\":true},"
        + "{\"id\":\"b1\",\"name\":\"Beta\",\"kind\":\"process\",\"process\":\"beta\",\"enabled\":true}],"
        + "\"seenTools\":[\"Git\"]}";

    private static SpriteEntry AnAnimation() => SpriteLibrary.All[0];

    /// The header texts of a menu's disabled entries, in order.
    private static List<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(m => !m.IsEnabled).Select(m => ((TextBlock)m.Header).Text).ToList();

    /// The entry of a menu by its text, enabled or not.
    private static MenuItem Entry(ContextMenu menu, string text) =>
        menu.Items.OfType<MenuItem>().Single(m => ((TextBlock)m.Header).Text == text);

    private static void Choose(MenuItem entry) => entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [Fact]
    public void UseMenu_HasTheThreeSections_ForTwoItemsAndOneTool()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith(TwoItemsAndATool);
            var pane = new IconLibraryPane(store);

            var menu = pane.UseMenu(AnAnimation());

            Assert.Equal(new[]
            {
                L10n.L("Pinned icon on the notch"),
                L10n.L("Set for a watched app"),
                L10n.L("Set for a build tool"),
            }, Headers(menu));
            Assert.Equal(new[]
            {
                L10n.L("Add on the left"),
                L10n.L("Add on the right"),
                "Alpha",
                "Beta",
                "Git",
            }, menu.Items.OfType<MenuItem>().Where(m => m.IsEnabled).Select(m => ((TextBlock)m.Header).Text));
            Assert.Equal(2, menu.Items.OfType<Separator>().Count());
        });
    }

    [Fact]
    public void UseMenu_NoItemsOrTools_ShowsOnlyThePinnedSection()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            // an empty list (not a missing one, which gives the default watch list)
            var pane = new IconLibraryPane(StoreWith("{\"items\":[]}"));

            var menu = pane.UseMenu(AnAnimation());

            Assert.Equal(new[] { L10n.L("Pinned icon on the notch") }, Headers(menu));
            Assert.Empty(menu.Items.OfType<Separator>());
        });
    }

    [Fact]
    public void UseMenu_FullSideIsDisabled()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith("{}");
            // the left side is full: its entry is disabled, the right side's is not
            var left = Enumerable.Range(0, BadgeEdits.PerSide)
                .Select(i => (JsonNode)new JsonObject { ["id"] = $"l{i}", ["sprite"] = "x", ["right"] = false })
                .ToArray();
            store.Set("badges", new JsonArray(left));
            var pane = new IconLibraryPane(store);

            var menu = pane.UseMenu(AnAnimation());

            Assert.False(Entry(menu, L10n.L("Add on the left")).IsEnabled);
        });
    }

    [Fact]
    public void UseMenu_ChoosingAnItemEntry_SetsItsIcon()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith(TwoItemsAndATool);
            var pane = new IconLibraryPane(store);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                var animation = AnAnimation();
                Choose(Entry(pane.UseMenu(animation), "Beta"));

                var icon = JsonNode.Parse(store.Items[1].Icon!)!.AsObject();
                Assert.Equal(animation.Id, (string?)icon["sprite"]);
                Assert.True((bool?)icon["plain"]);
                Assert.Null(store.Items[0].Icon);
                Assert.Equal(L10n.L("Set {0} for {1}.", animation.Name, "Beta"), Fixture.Named<TextBlock>(pane, "StatusText").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UseMenu_ChoosingAToolEntry_SetsItsToolIcon()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith(TwoItemsAndATool);
            var pane = new IconLibraryPane(store);
            var window = new Window { Content = pane };
            Fixture.ShowOffScreen(window);
            try
            {
                var animation = AnAnimation();
                Choose(Entry(pane.UseMenu(animation), "Git"));

                var icon = store.ToolIcons["Git"];
                Assert.Equal(animation.Id, (string?)icon["sprite"]);
                Assert.True((bool?)icon["plain"]);
                Assert.False(store.ToolIcons.ContainsKey("npm"));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
