using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using System.Text.Json.Nodes;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ModulesPaneRowTests
{
    /// The row of one module: the Grid in the Rows panel that shows its title.
    private static Grid RowFor(ModulesPane pane, string id)
    {
        Fixture.Call(pane, "Apply");
        var title = L10n.L(ModuleCatalog.Title(id));
        var rows = Fixture.Named<StackPanel>(pane, "Rows");
        return rows.Children.OfType<Grid>()
            .First(row => Descendants<TextBlock>(row).Any(t => t.Text == title));
    }

    /// The row's colour swatch, the round button whose tooltip is "Color".
    private static Button SwatchOf(Grid row) =>
        Descendants<Button>(row).First(button => button.ToolTip as string == L10n.L("Color"));

    [Fact]
    public void Drag_WritesTheSameOrderAsTheArrow()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => true));
            Fixture.Call(pane, "Apply");
            var start = state.Settings.Modules.ToList();
            var index = start.IndexOf("timer");

            // The arrow moves timer one place down.
            Fixture.Call(pane, "Move", "timer", 1);
            var byArrow = state.Settings.Modules.ToList();

            // The same move by a drag: a drop on the slot below timer (slot index + 2, counted with timer still in place)
            // lands timer at index + 1, which is the final index MoveRow takes.
            state.Settings.Set("modules", new JsonArray(start.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));
            Fixture.Call(pane, "Apply");
            Fixture.Call(pane, "MoveRow", "timer", index + 1);

            Assert.Equal(byArrow, state.Settings.Modules);
        });
    }

    [Fact]
    public void Drag_OntoItself_WritesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => true));
            Fixture.Call(pane, "Apply");
            var start = state.Settings.Modules.ToList();

            Fixture.Call(pane, "MoveRow", "timer", start.IndexOf("timer"));

            Assert.Equal(start, state.Settings.Modules);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
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
    public void Row_IsOneLine_WithTheColourInASwatch()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => true));

            var row = RowFor(pane, "media");

            Assert.Single(row.RowDefinitions);
            Assert.Equal(6, row.ColumnDefinitions.Count);
            // The hex field lives in a popup, so the row itself holds no ColorField.
            Assert.Empty(Descendants<ColorField>(row));
            Assert.NotNull(SwatchOf(row));
        });
    }

    [Fact]
    public void Swatch_ShowsTheStoredColour_AndFollowsChanges()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var colors = new JsonObject { ["media"] = "#112233" };
            state.Settings.Set("moduleColors", colors);
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => true));

            var swatch = SwatchOf(RowFor(pane, "media"));
            Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), ((SolidColorBrush)swatch.Background).Color);

            // A reset (or any change made outside the pane) leaves the swatch unfilled, with the "none" mark.
            state.Settings.Set("moduleColors", new JsonObject());
            var reset = SwatchOf(RowFor(pane, "media"));
            Assert.Same(Brushes.Transparent, reset.Background);  // an unset colour has no fill: the outlined "none" circle
            Assert.IsType<System.Windows.Controls.Canvas>(reset.Content);
        });
    }
}
