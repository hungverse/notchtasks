namespace NotchTasksSensors;

/// A downloaded PawnIO setup held locked. The bytes are written under a new random name (no one else can have it open), then the file
/// is reopened read-only with FileShare.Read only: while the handle is held, no other process can open it for writing, rename it or
/// delete it. Dispose closes the handle and deletes the file.
internal sealed class LockedSetupFile : IPawnIOSetupFile
{
    private FileStream? _handle;

    private LockedSetupFile(string path, FileStream handle)
    {
        Path = path;
        _handle = handle;
    }

    public string Path { get; }

    /// Writes the source to a new file at path, then holds it locked. The write handle is closed before the lock is taken; the check after
    /// the lock reads the file that will run, so nothing written before the lock can pass.
    public static async Task<LockedSetupFile> CreateAsync(string path, Stream source, CancellationToken ct)
    {
        try
        {
            await using (var write = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
            {
                await source.CopyToAsync(write, ct);
            }
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: false);
            return new LockedSetupFile(path, handle);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
        TryDelete(Path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that stays in the temporary folder is not worth failing the step.
        }
    }
}
