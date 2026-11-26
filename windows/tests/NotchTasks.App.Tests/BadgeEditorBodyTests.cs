using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.AppTests;

/// The editor of one badge: its kind (symbol, letters, picture or GIF), and the pictures it may take (copied, and refused when too
/// big or not the right kind of file).
public sealed class BadgeEditorBodyTests
{
    /// The edits the body raised, applied to a copy of the badge, in order.
    private static List<JsonObject> Edits(BadgeEditorBody body, JsonObject badge)
    {
        var result = new List<JsonObject>();
        body.Edit += change =>
        {
            var next = (JsonObject)badge.DeepClone();
            change(next);
            result.Add(next);
        };
        return result;
    }

    private static JsonObject Plain(string id) => new() { ["id"] = id, ["symbol"] = "star" };

    [Fact]
    public void Show_PicksTheKindTheBadgeHas()
    {
        Sta.Run(() =>
        {
            var body = new BadgeEditorBody();

            body.Show(new JsonObject { ["id"] = "a", ["text"] = "Hi" }, Colors.Gray);
            Assert.Equal("text", Assert.IsType<Choice>(body.KindBox.SelectedItem).Value);
            Assert.Equal(Visibility.Visible, body.TextPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, body.SymbolPanel.Visibility);

            body.Show(new JsonObject { ["id"] = "b", ["image"] = "C:/x/pic.png" }, Colors.Gray);
            Assert.Equal("picture", Assert.IsType<Choice>(body.KindBox.SelectedItem).Value);
            Assert.Equal(Visibility.Visible, body.PicturePanel.Visibility);

            body.Show(new JsonObject { ["id"] = "c", ["image"] = "C:/x/cat.gif", ["plain"] = true }, Colors.Gray);
            Assert.Equal("animation", Assert.IsType<Choice>(body.KindBox.SelectedItem).Value);
            Assert.Equal(Visibility.Visible, body.AnimationPanel.Visibility);

            body.Show(Plain("d"), Colors.Gray);
            Assert.Equal(Visibility.Visible, body.SymbolPanel.Visibility);
        });
    }

    [Fact]
    public void Show_RaisesNothing()
    {
        Sta.Run(() =>
        {
            var body = new BadgeEditorBody();
            var raised = Edits(body, Plain("a"));

            body.Show(new JsonObject { ["id"] = "a", ["text"] = "Hi" }, Colors.Gray);

            Assert.Empty(raised);
        });
    }

    [Fact]
    public void TypingLetters_RaisesAnEditThatSetsThem()
    {
        Sta.Run(() =>
        {
            var badge = Plain("a");
            var body = new BadgeEditorBody();
            body.Show(badge, Colors.Gray);
            var raised = Edits(body, badge);

            body.LetterBox.Text = "Hey";

            var next = Assert.Single(raised);
            Assert.Equal("Hey", next["text"]!.GetValue<string>());
            Assert.Null(next["image"]);
        });
    }

    [Fact]
    public void PictureOverEightMegabytes_IsRefused_WithTheReason()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Folder);
        var big = Path.Combine(root.Folder, "big.png");
        using (var stream = File.Create(big))
        {
            stream.SetLength(BadgeImages.MaxBytes + 1);
        }

        Sta.Run(() =>
        {
            var badge = Plain("a");
            var body = new BadgeEditorBody();
            body.Show(badge, Colors.Gray);
            var raised = Edits(body, badge);

            body.UsePicture(big);

            Assert.Empty(raised);
            Assert.Equal(Visibility.Visible, body.ErrorText.Visibility);
            Assert.Equal(L10n.L("That picture is over 8 MB."), body.ErrorText.Text);
        });
    }

    [Fact]
    public void Picture_IsCopiedIntoTheBadgesFolder_AndRaisesAnEditWithTheCopy()
    {
        using var root = new TempRoot();
        var png = BadgeMediaTests.WritePng(root.Folder, "shot.png", 64, 64);

        Sta.Run(() =>
        {
            var badge = Plain("a");
            var body = new BadgeEditorBody();
            body.Show(badge, Colors.Gray);
            var raised = Edits(body, badge);

            body.UsePicture(png);

            var next = Assert.Single(raised);
            var copy = next["image"]!.GetValue<string>();
            Assert.StartsWith(BadgeImages.Folder, copy);
            Assert.True(File.Exists(copy));
            Assert.Null(next["text"]);
            Assert.Equal(Visibility.Collapsed, body.ErrorText.Visibility);
        });

        File.Delete(png);  // the picked file is free to go
    }

    [Fact]
    public void Gif_ThatIsNotAGif_IsRefused()
    {
        using var root = new TempRoot();
        var png = BadgeMediaTests.WritePng(root.Folder, "still.png", 16, 16);

        Sta.Run(() =>
        {
            var badge = Plain("a");
            var body = new BadgeEditorBody();
            body.Show(badge, Colors.Gray);
            var raised = Edits(body, badge);

            body.UseAnimation(png);

            Assert.Empty(raised);
            Assert.Equal(L10n.L("Choose a GIF."), body.ErrorText.Text);
        });
    }

    [Fact]
    public void Gif_IsCopied_AndTakesTheFillOff()
    {
        using var root = new TempRoot();
        var gif = BadgeMediaTests.WriteGif(root.Folder, "cat.gif", Colors.Red, Colors.Blue);

        Sta.Run(() =>
        {
            var badge = Plain("a");
            var body = new BadgeEditorBody();
            body.Show(badge, Colors.Gray);
            var raised = Edits(body, badge);

            body.UseAnimation(gif);

            var next = Assert.Single(raised);
            Assert.EndsWith(".gif", next["image"]!.GetValue<string>());
            Assert.True(next["plain"]!.GetValue<bool>());
        });
    }

    [Fact]
    public void InkRow_ShowsForSymbolsAndLetters_OnlyAndNamesTheRightColour()
    {
        Sta.Run(() =>
        {
            var body = new BadgeEditorBody();

            body.Show(new JsonObject { ["id"] = "a", ["text"] = "Hi" }, Colors.Gray);
            Assert.Equal(Visibility.Visible, body.InkRow.Visibility);
            Assert.Equal(L10n.L("Text color"), body.InkLabel.Text);

            body.Show(new JsonObject { ["id"] = "b", ["image"] = "C:/x/pic.png" }, Colors.Gray);
            Assert.Equal(Visibility.Collapsed, body.InkRow.Visibility);
        });
    }
}
