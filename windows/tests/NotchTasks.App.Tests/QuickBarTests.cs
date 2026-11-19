using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class QuickBarTests
{
    [Fact]
    public void QuickBar_TimerPills_ShowWhenTimerOnAndIdle_HideWhileTimerRuns()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            // The pill is not shown: it is built and kept to receive the app state's changes, as the app does.
            var pill = new PillWindow(state);
            try
            {
                state.Publish();
                var bar = Fixture.Named<QuickBar>(pill, "Bar");
                var pills = Fixture.Named<StackPanel>(bar, "TimerPills");

                Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(bar, "TimerBar").Visibility);
                Assert.Equal(4, pills.Children.Count);
                Assert.Equal(new[] { "5′", "15′", "25′", "45′" },
                    pills.Children.Cast<Button>().Select(b => (string)b.Content).ToArray());

                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(bar, "TimerBar").Visibility);

                state.Timer.Stop();
                state.Publish();
                Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(bar, "TimerBar").Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
