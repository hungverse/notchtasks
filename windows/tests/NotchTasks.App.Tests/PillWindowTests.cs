using System.Windows;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class PillWindowTests
{
    [Fact]
    public void PillConstructsAndLaysOut()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // Not shown: Show places the pill on the real top edge of the screen. The content is measured and arranged
            // directly, which is the layout pass the XAML and its bindings need.
            var pill = new PillWindow(state);
            try
            {
                var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
                content.Measure(new Size(1200, 600));
                content.Arrange(new Rect(0, 0, 1200, 600));
                Assert.True(content.IsArrangeValid);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
