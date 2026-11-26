using System.Text;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.Core.Tests;

/// A picture for a badge is copied into the Badges folder, checked first: at most 8 MB, and a picture by its bytes, not its name.
public sealed class BadgeImagesTests : IDisposable
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NotchTasks.CoreTests", Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "Badges");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a stray handle leaves the temp folder for the system to clear
        }
    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Pad(byte[] head, int length)
    {
        var bytes = new byte[length];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void Copy_APicture_IsCopiedIntoTheFolder_AndTheSourceIsFree()
    {
        var source = Write("shot.png", Pad(PngHeader, 4096));

        var result = BadgeImages.Copy(source, Folder);

        Assert.Null(result.Refusal);
        var copy = Assert.IsType<string>(result.Path);
        Assert.StartsWith(Folder, copy);
        Assert.EndsWith(".png", copy);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
        File.Delete(source);  // the picked file is not held open
    }

    [Fact]
    public void Copy_FileOverEightMegabytes_IsRefused()
    {
        var source = Write("big.png", Pad(PngHeader, 8 * 1024 * 1024 + 1));

        var result = BadgeImages.Copy(source, Folder);

        Assert.Null(result.Path);
        Assert.Equal(L10n.L("That picture is over 8 MB."), result.Refusal);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Copy_FileOfExactlyEightMegabytes_IsAccepted()
    {
        var source = Write("edge.png", Pad(PngHeader, 8 * 1024 * 1024));

        var result = BadgeImages.Copy(source, Folder);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Path);
    }

    [Fact]
    public void Copy_TextNamedAsPicture_IsRefused()
    {
        var source = Write("notes.png", Encoding.UTF8.GetBytes("just some words, not a picture"));

        var result = BadgeImages.Copy(source, Folder);

        Assert.Null(result.Path);
        Assert.Equal(L10n.L("That file is not a picture."), result.Refusal);
    }

    [Fact]
    public void Copy_JpegWithATextName_IsKeptAsAJpeg()
    {
        var source = Write("photo.txt", Pad([0xFF, 0xD8, 0xFF, 0xE0], 512));

        var result = BadgeImages.Copy(source, Folder);

        Assert.Null(result.Refusal);
        Assert.EndsWith(".jpg", result.Path);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ".gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0 }, ".bmp")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0, 0 }, ".tif")]
    public void KindOf_ReadsThePictureFormatFromItsBytes(byte[] head, string extension)
    {
        Assert.Equal(extension, BadgeImages.KindOf(head));
    }

    [Fact]
    public void KindOf_ReadsWebpFromItsRiffHeader()
    {
        var head = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");

        Assert.Equal(".webp", BadgeImages.KindOf(head));
    }

    [Fact]
    public void KindOf_RejectsWhatIsNotAPicture()
    {
        Assert.Null(BadgeImages.KindOf(Encoding.ASCII.GetBytes("hello world")));
        Assert.Null(BadgeImages.KindOf([]));
    }

    private string WriteCopy(string name)
    {
        Directory.CreateDirectory(Folder);
        var copy = Path.Combine(Folder, name);
        File.WriteAllBytes(copy, Pad(PngHeader, 64));
        return copy;
    }

    [Fact]
    public void DiscardUnused_DeletesACopyNoBadgeUses()
    {
        var copy = WriteCopy("gone.png");

        BadgeImages.DiscardUnused(copy, Folder, referenced: [null, "/some/other.png"]);

        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void DiscardUnused_KeepsACopyAnotherBadgeUses()
    {
        var copy = WriteCopy("kept.png");

        BadgeImages.DiscardUnused(copy, Folder, referenced: [copy]);

        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void DiscardUnused_NeverDeletesOutsideTheFolder()
    {
        var outside = Write("mine.png", Pad(PngHeader, 64));

        BadgeImages.DiscardUnused(outside, Folder, referenced: []);

        Assert.True(File.Exists(outside));
    }
}
