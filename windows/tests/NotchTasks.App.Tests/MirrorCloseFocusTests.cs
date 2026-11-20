using System.Windows.Controls;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The pill never takes the keyboard focus: the mirror's close button is not focusable (a click still closes the mirror).
public sealed class MirrorCloseFocusTests
{
    [Fact]
    public void CloseButton_IsNotFocusable()
    {
        Sta.Run(() =>
        {
            var card = new MirrorCard();

            var close = Fixture.Field<Button>(card, "_close");
            Assert.False(close.Focusable);
        });
    }
}
