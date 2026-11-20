using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The pill never takes the keyboard focus (its window never activates, and its buttons are not focusable), and the pill's
/// effects are not made again on each publish.
public sealed class PillFocusAndCostTests
{
    /// Every button under the element, through the visual tree.
    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                yield return button;
            }
            foreach (var nested in Buttons(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void EveryButtonUnderThePill_IsNotFocusable()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var now = DateTimeOffset.Now;
            state.Timer.Start(5, now);  // the timer card and the quick bar
            state.Sensors.Apply(new SensorState(new[] { "zoom.exe" }, false, false), now);  // a call: the call card
            var ask = new AgentAsk("focus1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), now, "claude");
            Fixture.Call(state, "SyncAsks", new List<AgentAsk> { ask });  // an ask: its card and its toast's buttons
            state.Publish();
            var pill = new PillWindow(state);
            try
            {
                var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
                content.Measure(new Size(1200, 600));
                content.Arrange(new Rect(0, 0, 1200, 600));
                content.UpdateLayout();

                var buttons = Buttons(content).ToList();
                Assert.NotEmpty(buttons);
                foreach (var button in buttons)
                {
                    Assert.False(button.Focusable, $"A pill button takes the keyboard focus: {button.Name} ({button.Content})");
                }
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void ApplyAppearance_KeepsTheShadowEffect_WhenNothingChanged()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                var shadow = Fixture.Named<System.Windows.Shapes.Path>(pill, "ShadowShape");  // the shadow is a path on the shape
                Fixture.Call(pill, "ApplyAppearance");
                var first = shadow.Effect;
                Assert.IsType<DropShadowEffect>(first);

                Fixture.Call(pill, "ApplyAppearance");  // a publish with the same state: the same effect object, not a new one
                Assert.Same(first, shadow.Effect);

                // The same goes for the brushes: a publish with nothing changed assigns the instances it already had.
                var fill = Fixture.Named<System.Windows.Shapes.Path>(pill, "ShapeFill");
                var glowStroke = Fixture.Named<System.Windows.Shapes.Path>(pill, "GlowRing");
                var fillBefore = fill.Fill;
                var strokeBefore = glowStroke.Stroke;
                Fixture.Call(pill, "ApplyAppearance");
                Assert.Same(fillBefore, fill.Fill);
                Assert.Same(strokeBefore, glowStroke.Stroke);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
