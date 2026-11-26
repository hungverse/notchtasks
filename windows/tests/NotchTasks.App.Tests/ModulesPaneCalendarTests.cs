using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Calendars;
using Xunit;

namespace NotchTasks.AppTests;

/// The Modules pane's calendar hint: the caption under the Calendar row when the store cannot be opened, and nothing otherwise.
public sealed class ModulesPaneCalendarTests
{
    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                yield return text;
            }
            foreach (var inner in Texts(child))
            {
                yield return inner;
            }
        }
    }

    private static string Hint => L10n.L("Calendar can't be opened. Check Settings > Privacy & security > Calendar.");

    /// The rows whose hint is shown, by the title of the row.
    private static List<string> RowsWithHint(ModulesPane pane)
    {
        Fixture.Call(pane, "Apply");
        var rows = Fixture.Named<StackPanel>(pane, "Rows");
        return rows.Children.OfType<Grid>()
            .Where(row => Texts(row).Any(t => t.Text == Hint && t.Visibility == Visibility.Visible))
            .Select(row => Texts(row).First(t => t.FontWeight == FontWeights.SemiBold).Text)
            .ToList();
    }

    [Fact]
    public void Pane_ShowsTheHintUnderTheCalendarRow_WhenTheStoreCannotOpen()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Calendar.SetUnavailable();
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => false), state);

            Assert.Equal(new[] { L10n.L("Calendar") }, RowsWithHint(pane));
        });
    }

    [Fact]
    public void Pane_ShowsNoHint_WhenTheStoreOpens()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Calendar.Apply(Array.Empty<CalendarRecord>(), DateTimeOffset.Now);
            var pane = Fixture.Create<ModulesPane>(state.Settings, new Func<bool>(() => false), state);

            Assert.Empty(RowsWithHint(pane));
        });
    }
}
