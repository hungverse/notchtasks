using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.AppTests;

/// The Build & CI box of the Watching pane and the badge editor it opens: the caption, the tools (the seen ones, and every known
/// tool with Show all), the changed dot, a click that makes the icon, and the editor body the Icons pane and the dialog share.
/// The editor opened by a click is a seam here, so no dialog is shown. Every test works in a temp settings folder.
[Collection("L10n")]
public sealed class BuildToolsBoxTests
{
    private const string NoBuilds = "No builds seen yet. Turn on \"Show all\" to set icons ahead of time.";
    private const string Nothing = "Nothing to add: any running tool shows up. Click a tool to change its icon.";

    /// Writes the settings file with this JSON, then loads the store over it.
    private static SettingsStore StoreWith(string json)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        return SettingsStore.Load(AppPaths.SettingsFile);
    }

    /// The box in an off-screen window, so it lays out its tools.
    private static Window Show(UIElement content)
    {
        var window = new Window { Content = content };
        Fixture.ShowOffScreen(window);
        return window;
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// Sets a check box and raises its click, as the user's click does.
    private static void SetChecked(CheckBox box, bool on)
    {
        box.IsChecked = on;
        Click(box);
    }

    private static string Caption(FrameworkElement box) => Fixture.Named<TextBlock>(box, "CaptionText").Text;

    private static List<Button> ToolButtons(FrameworkElement box) =>
        Fixture.Named<WrapPanel>(box, "Tools").Children.OfType<Button>().ToList();

    private static Button ToolButton(FrameworkElement box, string tool) =>
        ToolButtons(box).Single(button => (string)button.Tag == tool);

    /// The changed dot of a tool button: the 5 px ellipse its content holds only when the tool has a custom icon.
    private static List<Ellipse> Dots(Button button) =>
        ((Panel)((Border)button.Content).Child).Children.OfType<Ellipse>().ToList();

    [Fact]
    public void Box_Caption_NoToolsVsTools() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var box = new BuildToolsBox(StoreWith("{}"), (_, _) => { });
        var window = Show(box);
        try
        {
            Assert.Equal(NoBuilds, Caption(box));

            SetChecked(Fixture.Named<CheckBox>(box, "ShowAllBox"), true);
            Assert.Equal(Nothing, Caption(box));
        }
        finally
        {
            window.Close();
        }

        var withTools = new BuildToolsBox(StoreWith("{\"seenTools\":[\"Git\"]}"), (_, _) => { });
        var second = Show(withTools);
        try
        {
            Assert.Equal(Nothing, Caption(withTools));
        }
        finally
        {
            second.Close();
        }
    });

    [Fact]
    public void Box_ShowAll_AddsKnownToolsSorted() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var box = new BuildToolsBox(StoreWith("{\"seenTools\":[\"npm\",\"Git\"]}"), (_, _) => { });
        var window = Show(box);
        try
        {
            // Without Show all: only the seen tools, newest first.
            Assert.Equal(new[] { "npm", "Git" }, ToolButtons(box).Select(b => (string)b.Tag));

            // With Show all: the seen tools, then the rest of the known tools sorted case-insensitively.
            SetChecked(Fixture.Named<CheckBox>(box, "ShowAllBox"), true);
            var expected = new[] { "npm", "Git" }
                .Concat(DevRules.KnownTools.Where(tool => tool is not ("npm" or "Git")).OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase))
                .ToList();
            Assert.Equal(expected, ToolButtons(box).Select(b => (string)b.Tag));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Box_ClickCreatesDefaultIcon() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var store = StoreWith("{\"toolIcons\":{\"Git\":{\"symbol\":\"seg:E735\",\"shape\":\"circle\"}}}");
        var opened = new List<(string Tool, string? Symbol)>();
        var box = new BuildToolsBox(store, (tool, badge) => opened.Add((tool, (string?)badge["symbol"])));
        var window = Show(box);
        try
        {
            SetChecked(Fixture.Named<CheckBox>(box, "ShowAllBox"), true);

            Click(ToolButton(box, "Docker"));

            var icon = store.ToolIcons["Docker"];
            Assert.Equal(DevRules.DefaultSymbol("Docker"), (string?)icon["symbol"]);
            Assert.Equal("rounded", (string?)icon["shape"]);
            Assert.Equal(new[] { ("Docker", (string?)DevRules.DefaultSymbol("Docker")) }, opened);

            // A tool that already has an icon keeps it: the click only opens its editor.
            Click(ToolButton(box, "Git"));
            Assert.Equal("seg:E735", (string?)store.ToolIcons["Git"]["symbol"]);
            Assert.Equal(2, opened.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Box_ChangedDot_OnlyForCustom() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var store = StoreWith("{\"seenTools\":[\"Git\",\"npm\"],\"toolIcons\":{\"Git\":{\"symbol\":\"seg:E735\"}}}");
        var box = new BuildToolsBox(store, (_, _) => { });
        var window = Show(box);
        try
        {
            var git = Dots(ToolButton(box, "Git"));
            Assert.Single(git);
            Assert.Equal("Changed", (string)git[0].ToolTip);

            Assert.Empty(Dots(ToolButton(box, "npm")));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void BadgesPane_StillEditsBadges() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var state = Fixture.NewState();
        var store = state.Settings;
        store.Set("badges", JsonNode.Parse("[{\"id\":\"b1\",\"symbol\":\"star\",\"right\":false,\"shape\":\"circle\",\"border\":false}]"));
        IconSelection.Select("b1");
        try
        {
            var pane = new BadgesPane(store, state);
            var window = Show(pane);
            try
            {
                Assert.Equal(Visibility.Visible, Fixture.Named<StackPanel>(pane, "Editor").Visibility);

                // The editor body is shared with the tool dialog: a symbol tile in it writes the selected badge.
                var body = Fixture.Named<BadgeEditorBody>(pane, "Body");
                var tile = Fixture.Named<WrapPanel>(body, "SymbolGrid").Children.OfType<Button>().Single(b => (string)b.Tag == "E735");
                Click(tile);
                Assert.Equal("seg:E735", store.Badges[0]!["symbol"]!.GetValue<string>());

                // A check box of the same body writes the border.
                SetChecked(Fixture.Named<CheckBox>(body, "BorderBox"), true);
                Assert.True(store.Badges[0]!["border"]!.GetValue<bool>());
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            IconSelection.Clear();
        }
    });

    [Fact]
    public void Dialog_EscapeCloses_ThroughTheDoneButton() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var store = StoreWith("{}");
        store.SetToolIcon("Docker", new JsonObject { ["symbol"] = DevRules.DefaultSymbol("Docker"), ["shape"] = "rounded" });
        var dialog = new BadgeEditorDialog(store, "Docker", store.ToolIcons["Docker"]);
        Fixture.ShowOffScreen(dialog);
        try
        {
            // Esc presses the window's cancel button; Done closes the dialog
            Assert.True(Fixture.Named<Button>(dialog, "DoneButton").IsCancel);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public void Dialog_EditAndResetWriteToolIcon() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var store = StoreWith("{}");
        store.SetToolIcon("Docker", new JsonObject { ["symbol"] = DevRules.DefaultSymbol("Docker"), ["shape"] = "rounded" });
        var dialog = new BadgeEditorDialog(store, "Docker", store.ToolIcons["Docker"]);
        Fixture.ShowOffScreen(dialog);
        try
        {
            var tile = Fixture.Named<WrapPanel>(Fixture.Named<BadgeEditorBody>(dialog, "Body"), "SymbolGrid")
                .Children.OfType<Button>().Single(b => (string)b.Tag == "E735");
            Click(tile);
            Assert.Equal("seg:E735", (string?)store.ToolIcons["Docker"]["symbol"]);

            Click(Fixture.Named<Button>(dialog, "ResetButton"));
            Assert.False(store.ToolIcons.ContainsKey("Docker"));
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public void Dialog_ResetIsLabelledUseTheDefaultIcon() => Sta.Run(() =>
    {
        using var root = new TempRoot();
        var store = StoreWith("{}");
        store.SetToolIcon("Docker", new JsonObject { ["symbol"] = DevRules.DefaultSymbol("Docker"), ["shape"] = "rounded" });
        var dialog = new BadgeEditorDialog(store, "Docker", store.ToolIcons["Docker"]);
        Fixture.ShowOffScreen(dialog);
        try
        {
            // The Mac's words for the reset (BadgesPane.swift), in the button's own width.
            var reset = Fixture.Named<Button>(dialog, "ResetButton");
            Assert.Equal(L10n.L("Use the default icon"), reset.Content);
            Assert.Equal(140, reset.MinWidth);
        }
        finally
        {
            dialog.Close();
        }
    });
}
