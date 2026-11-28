using System.Text;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The lock on a downloaded setup, on a real file in a temp folder: while it is held, another process cannot write, rename or delete it;
/// reading stays possible; disposing it deletes the file.
public sealed class LockedSetupFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nt-locked-" + Guid.NewGuid().ToString("N"));

    public LockedSetupFileTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task WhileHeld_TheFileCannotBeWrittenRenamedOrDeleted()
    {
        var path = Path.Combine(_dir, "setup.exe");
        var bytes = Encoding.UTF8.GetBytes("signed setup bytes");
        using var stream = new MemoryStream(bytes);

        using var locked = await LockedSetupFile.CreateAsync(path, stream, CancellationToken.None);

        Assert.Equal(path, locked.Path);
        Assert.Equal(bytes, File.ReadAllBytes(path));  // reading is allowed
        Assert.ThrowsAny<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
        Assert.ThrowsAny<Exception>(() => File.Delete(path));
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));  // unchanged
    }

    [Fact]
    public async Task Dispose_ReleasesAndDeletesTheFile()
    {
        var path = Path.Combine(_dir, "setup.exe");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var locked = await LockedSetupFile.CreateAsync(path, stream, CancellationToken.None);

        locked.Dispose();

        Assert.False(File.Exists(path));
    }
}
