using System.Text.Json.Nodes;
using System.Windows.Controls;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class CallSectionTests
{
    [Fact]
    public void CallCard_KeepsItsElement_AndItsTimeFollowsThePublish()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"calls\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                // A call that started 125.2 s ago: its time reads 2:05 or 2:06 for the next second or so.
                state.Sensors.Apply(new SensorState(new[] { "zoom.exe" }, false, false), DateTimeOffset.Now.AddSeconds(-125.2));
                state.Publish();
                var sections = Fixture.Named<StackPanel>(pill, "SectionList");
                var card = Assert.Single(sections.Children.OfType<CallCard>());
                var time = Fixture.Named<TextBlock>(card, "TimeText");
                Assert.StartsWith("2:0", time.Text);
                var rebuilds = Fixture.Field<int>(pill, "_sectionRebuilds");

                // Each publish (the source's once-a-second publish while a call is live) refreshes the time in place.
                state.Publish();
                state.Publish();

                Assert.Equal(rebuilds, Fixture.Field<int>(pill, "_sectionRebuilds"));
                Assert.Same(card, sections.Children[0]);
                Assert.StartsWith("2:0", time.Text);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
