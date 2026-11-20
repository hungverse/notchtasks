using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The shelf's tile pictures never touch the disk on the UI thread: Get returns the type icon at once, and the file or folder check, the
/// modified time and the thumbnail are read in the background. An offline share therefore cannot freeze the pill.
public sealed class FileThumbsTests
{
    [Fact]
    public void Get_ReturnsAtOnce_AndTheProbeRunsOffTheUiThread()
    {
        Sta.Run(() =>
        {
            var originalProbe = FileThumbs.Probe;
            var originalThumb = FileThumbs.ReadThumbnail;
            var uiThread = Environment.CurrentManagedThreadId;
            var probedOn = new List<int>();
            var release = new ManualResetEventSlim(false);
            var probed = new ManualResetEventSlim(false);
            try
            {
                // A probe that blocks until the test lets it go, as an offline share does: Get must return while it is still blocked.
                FileThumbs.Probe = _ =>
                {
                    probedOn.Add(Environment.CurrentManagedThreadId);
                    release.Wait(TimeSpan.FromSeconds(5));
                    probed.Set();
                    return (false, 0);
                };
                FileThumbs.ReadThumbnail = (_, _) => Task.FromResult<ImageSource?>(null);

                var icon = FileThumbs.Get(@"\offline-share\photos\holiday.jpg", _ => { });

                Assert.False(probed.IsSet);  // Get did not wait for the disk: the type icon came back while the probe was blocked
                Assert.NotNull(icon);
                release.Set();
                Assert.True(probed.Wait(TimeSpan.FromSeconds(5)));
                Assert.NotEqual(uiThread, Assert.Single(probedOn));  // and the probe ran on a background thread
            }
            finally
            {
                FileThumbs.Probe = originalProbe;
                FileThumbs.ReadThumbnail = originalThumb;
            }
        });
    }

    [Fact]
    public void Get_ThumbnailArrivesOnTheUiThread()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var file = Path.Combine(root.Folder, "note.txt");
            File.WriteAllText(file, "thumb");
            var originalProbe = FileThumbs.Probe;
            var originalThumb = FileThumbs.ReadThumbnail;
            var image = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
            image.Freeze();
            var uiThread = Environment.CurrentManagedThreadId;
            int? readyOn = null;
            ImageSource? ready = null;
            try
            {
                FileThumbs.ReadThumbnail = (_, _) => Task.FromResult<ImageSource?>(image);

                FileThumbs.Get(file, shown =>
                {
                    readyOn = Environment.CurrentManagedThreadId;
                    ready = shown;
                });
                Fixture.PumpUntil(() => ready is not null);

                Assert.Same(image, ready);
                Assert.Equal(uiThread, readyOn);
            }
            finally
            {
                FileThumbs.Probe = originalProbe;
                FileThumbs.ReadThumbnail = originalThumb;
            }
        });
    }
}
