using System.Text.Json.Nodes;

namespace NotchTasks.Core.Settings;

/// The pictures of badges: each one a copy in the Badges folder (%APPDATA%\NotchTasks\Badges), so the file the user picked can
/// move or be deleted, and nothing keeps it locked. The copy is checked first: at most 8 MB, and a picture by its bytes (the
/// Mac's BadgesPane.importImage keeps a copy too; it does not check, which Windows does because a bad file would stall the pill).
public static class BadgeImages
{
    /// The largest picture a badge may use. A bigger file is refused rather than copied.
    public const long MaxBytes = 8L * 1024 * 1024;

    /// The Badges folder under the app's data folder.
    public static string Folder => Path.Combine(AppPaths.DataDir, "Badges");

    /// The result of a copy: the new file's path, or the reason it was refused (Path is then null).
    public sealed record ImportResult(string? Path, string? Refusal);

    /// Copies a picture into `folder` under a new name, and gives the copy's path. A file over MaxBytes, or one whose bytes are not
    /// a picture, is refused with the reason and nothing is copied.
    public static ImportResult Copy(string source, string folder)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(source);
            if (!info.Exists)
            {
                return Refused(L10n.L("That file can't be read."));
            }
            if (info.Length > MaxBytes)
            {
                return Refused(L10n.L("That picture is over 8 MB."));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(L10n.L("That file can't be read."));
        }

        byte[] head;
        try
        {
            using var stream = File.OpenRead(source);
            head = new byte[16];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            Array.Resize(ref head, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(L10n.L("That file can't be read."));
        }

        var extension = KindOf(head);
        if (extension is null)
        {
            return Refused(L10n.L("That file is not a picture."));
        }

        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
            File.Copy(source, copy, overwrite: false);
            return new ImportResult(copy, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(L10n.L("That file can't be read."));
        }
    }

    /// The extension of the picture format the bytes start with (".png", ".jpg", ".gif", ".bmp", ".tif" or ".webp"), or null when
    /// they are none of these.
    public static string? KindOf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return ".jpg";
        }
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return ".gif";
        }
        if (head.StartsWith("BM"u8))
        {
            return ".bmp";
        }
        if (head.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || head.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }))
        {
            return ".tif";
        }
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }
        return null;
    }

    /// Deletes a copied picture that no badge uses any more. Only a file inside `folder` is ever deleted; `referenced` holds the
    /// picture paths the settings still name (null entries are ignored).
    public static void DiscardUnused(string? path, string folder, IEnumerable<string?> referenced)
    {
        if (path is null || !IsInside(path, folder) || referenced.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a file still open elsewhere stays; the next discard tries again
        }
    }

    /// Deletes a copied picture that the settings no longer use: no badge on either side, and no tool's icon, still names it.
    public static void Discard(string? path, SettingsStore store)
    {
        var referenced = store.Badges.OfType<JsonObject>().Select(BadgeEdits.ImageOf)
            .Concat(store.ToolIcons.Values.Select(BadgeEdits.ImageOf))
            .ToList();
        DiscardUnused(path, Folder, referenced);
    }

    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static ImportResult Refused(string reason) => new(null, reason);
}
