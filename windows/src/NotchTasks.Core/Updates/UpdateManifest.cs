using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Updates;

/// Why an update step failed. Each kind has the text the Mac shows (Updater.swift UpdateFailure).
public enum UpdateFailureKind { InvalidManifest, Download, Hash, OldBuild, Pending, Launch }

/// An update step failed. The message is the user's text for the kind, through L10n.
public sealed class UpdateFailure(UpdateFailureKind kind) : Exception(Text(kind))
{
    public UpdateFailureKind Kind { get; } = kind;

    private static string Text(UpdateFailureKind kind) => kind switch
    {
        UpdateFailureKind.InvalidManifest => L10n.L("The update information is invalid."),
        UpdateFailureKind.Download => L10n.L("Couldn't download the update."),
        UpdateFailureKind.Hash => L10n.L("The downloaded file doesn't match its checksum."),
        UpdateFailureKind.OldBuild => L10n.L("The update isn't newer than the running app."),
        UpdateFailureKind.Pending => L10n.L("Another update is unfinished. Try again later."),
        UpdateFailureKind.Launch => L10n.L("Couldn't start the installer."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// The feed's entry for this Windows build: electron/src/core/updater.js parseManifest, and the Mac's UpdateManifest.
/// Sha256 is stored lower-case.
public sealed record UpdateManifest(string Version, int Build, string Url, string Sha256, string MinOS,
    IReadOnlyDictionary<string, string> Notes)
{
    /// Reads and checks one feed entry. Throws UpdateFailure(InvalidManifest) for a missing or mistyped field, a build of
    /// zero or less, a URL that is not https (or loopback http), a checksum that is not 64 hex digits, a minOS that is not
    /// a version, or notes that are not all text.
    public static UpdateManifest Parse(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            throw new UpdateFailure(UpdateFailureKind.InvalidManifest);
        }
        var version = Text(o, "version");
        var url = Text(o, "url");
        var sha = Text(o, "sha256");
        var minOS = Text(o, "minOS");
        var build = o["build"] is JsonValue b && b.TryGetValue(out int n) ? n : 0;
        if (string.IsNullOrEmpty(version) || build <= 0 || url is null || !UpdateRules.IsAllowedUrl(url)
            || sha is null || sha.Length != 64 || !sha.All(Uri.IsHexDigit) || minOS is null || UpdateRules.ParseOS(minOS) is null)
        {
            throw new UpdateFailure(UpdateFailureKind.InvalidManifest);
        }
        if (o["notes"] is not JsonObject notesNode)
        {
            throw new UpdateFailure(UpdateFailureKind.InvalidManifest);
        }
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lang, value) in notesNode)
        {
            if (value is not JsonValue v || !v.TryGetValue(out string? text))
            {
                throw new UpdateFailure(UpdateFailureKind.InvalidManifest);
            }
            notes[lang] = text;
        }
        return new UpdateManifest(version, build, url, sha.ToLowerInvariant(), minOS, notes);
    }

    /// The release note in this language, else the English one, else "No notes.".
    public string NoteFor(string lang)
    {
        if (Notes.TryGetValue(lang, out var note))
        {
            return note;
        }
        return Notes.TryGetValue("en", out var english) ? english : L10n.L("No notes.");
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
