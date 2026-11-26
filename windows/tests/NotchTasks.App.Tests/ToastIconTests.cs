using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// A toast of an agent or an app draws that item's icon on the left and the tint symbol on the right (Mac toastIcon and the
/// trailing symbol); a toast with no item draws its glyph as before.
public sealed class ToastIconTests
{
    private static readonly WatchItem Claude = new("claude-1", "Claude", WatchKind.Claude);

    [Fact]
    public void IconOf_ToastWithAnItem_DrawsTheItemsIcon()
    {
        Sta.Run(() =>
        {
            var toast = new Toast("check", "#30D158", "Claude done", Icon: Claude);

            // an agent with no program icon draws the sparkles glyph (the item's icon), not the toast's check
            var icon = Assert.IsType<TextBlock>(ToastView.IconOf(toast, Colors.White, 18, null));
            Assert.Equal(ModuleGlyphs.Get("sparkles").Glyph, icon.Text);
        });
    }

    [Fact]
    public void IconOf_ToastWithoutAnItem_IsNull()
    {
        Sta.Run(() =>
        {
            var toast = new Toast("check", "#30D158", "Plain");

            Assert.Null(ToastView.IconOf(toast, Colors.White, 18, null));
        });
    }

    [Fact]
    public void TrailOf_ToastWithAnItem_IsTheTintSymbol()
    {
        Sta.Run(() =>
        {
            var toast = new Toast("check", "#30D158", "Claude done", Icon: Claude);

            var symbol = Assert.IsType<TextBlock>(ToastView.TrailOf(toast, Brushes.White, 14));
            Assert.Equal(ModuleGlyphs.Get("check").Glyph, symbol.Text);
        });
    }

    [Fact]
    public void TrailOf_ToastWithoutAnItem_IsNull()
    {
        Sta.Run(() =>
        {
            var toast = new Toast("check", "#30D158", "Plain");

            Assert.Null(ToastView.TrailOf(toast, Brushes.White, 14));
        });
    }
}
