using System.Text.Json.Nodes;
using System.Windows.Controls;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class TimerSectionTests
{
    [Fact]
    public void TimerCard_StaysInTheSections_WhileAnUnchangedTimerPublishes()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                var sections = Fixture.Named<StackPanel>(pill, "SectionList");
                var card = Assert.Single(sections.Children.OfType<TimerCard>());
                var rebuilds = Fixture.Field<int>(pill, "_sectionRebuilds");

                // The timer ticks: each publish is the same timer, so the card is neither taken out nor replaced.
                state.Publish();
                state.Publish();

                Assert.Equal(rebuilds, Fixture.Field<int>(pill, "_sectionRebuilds"));
                Assert.Single(sections.Children);
                Assert.Same(card, sections.Children[0]);
                Assert.Same(sections, System.Windows.Media.VisualTreeHelper.GetParent(card));
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
