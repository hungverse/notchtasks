namespace NotchTasks.Core.Sounds;

/// The Mac sound names of the sound packs, as the Windows files in C:\Windows\Media (electron/src/main/main.js SOUND_FILES).
public static class SoundFiles
{
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Basso"] = "Windows Error.wav",
        ["Blow"] = "Speech On.wav",
        ["Bottle"] = "chord.wav",
        ["Frog"] = "notify.wav",
        ["Funk"] = "Windows Exclamation.wav",
        ["Glass"] = "Windows Ding.wav",
        ["Hero"] = "tada.wav",
        ["Morse"] = "Windows Notify System Generic.wav",
        ["Ping"] = "ding.wav",
        ["Pop"] = "Windows Message Nudge.wav",
        ["Purr"] = "Windows Proximity Notification.wav",
        ["Sosumi"] = "recycle.wav",
        ["Submarine"] = "Windows Background.wav",
        ["Tink"] = "chimes.wav",
    };

    /// The sound names, sorted (the choices of the Sound pane, after "None").
    public static IReadOnlyList<string> Names { get; } = Files.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();

    /// The file name in C:\Windows\Media for a Mac sound name, or null when the name has no Windows file.
    public static string? FileName(string? macName) =>
        macName is not null && Files.TryGetValue(macName, out var file) ? file : null;
}
