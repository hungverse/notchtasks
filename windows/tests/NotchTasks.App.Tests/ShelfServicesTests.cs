using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The shelf's services and rules: the share sheet's failure, the folder thumbnail, the drop and the drop prompt's safety net, and a
/// failed save. Files live in a temp root; no share sheet is shown.
public sealed class ShelfServicesTests
{
    [Fact]
    public void Share_FailingWindow_ReturnsFalseWithoutThrowing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var file = Path.Combine(root.Folder, "a.txt");
            File.WriteAllText(file, "x");

            // A handle that is not a window: the sheet cannot be shown, and Share reports that rather than throwing.
            Assert.False(ShareSheet.Share(new nint(1), new[] { file }));
        });
    }

    [Fact]
    public void FolderThumbnail_IsReadAndHandedToReady()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var folder = Directory.CreateDirectory(Path.Combine(root.Folder, "photos")).FullName;
            var image = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
            image.Freeze();
            var original = FileThumbs.ReadThumbnail;
            var asked = new List<(string Path, bool Folder)>();
            FileThumbs.ReadThumbnail = (path, isFolder) =>
            {
                asked.Add((path, isFolder));
                return Task.FromResult<ImageSource?>(image);
            };
            try
            {
                ImageSource? ready = null;
                FileThumbs.Get(folder, shown => ready = shown);
                Fixture.PumpUntil(() => ready is not null);

                Assert.Same(image, ready);
                Assert.Equal((folder, true), Assert.Single(asked));
            }
            finally
            {
                FileThumbs.ReadThumbnail = original;
            }
        });
    }

    [Fact]
    public void Drop_AcceptedOnlyWithShelfOnFilesAndNotDraggingOut()
    {
        Assert.True(ShelfCard.AcceptsDrop(shelfOn: true, hasFiles: true, busy: false));
        Assert.False(ShelfCard.AcceptsDrop(shelfOn: false, hasFiles: true, busy: false));
        Assert.False(ShelfCard.AcceptsDrop(shelfOn: true, hasFiles: false, busy: false));
        Assert.False(ShelfCard.AcceptsDrop(shelfOn: true, hasFiles: true, busy: true));
    }

    [Fact]
    public void DropPrompt_ClearsWhenThePointerLeavesWithNoButtonDown()
    {
        Assert.True(ShelfCard.ClearsTarget(targeted: true, pointerOverPill: false, buttonDown: false));
        Assert.False(ShelfCard.ClearsTarget(targeted: true, pointerOverPill: false, buttonDown: true));
        Assert.False(ShelfCard.ClearsTarget(targeted: true, pointerOverPill: true, buttonDown: false));
        Assert.False(ShelfCard.ClearsTarget(targeted: false, pointerOverPill: false, buttonDown: false));
    }

    [Fact]
    public void ShelfSave_FailedWrite_IsReportedNotThrown()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();

            Assert.False(ShelfSave.TrySave(() => throw new IOException("disk full")));
            Assert.True(ShelfSave.TrySave(() => { }));
        });
    }
}
