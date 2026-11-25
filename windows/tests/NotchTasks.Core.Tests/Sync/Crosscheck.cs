namespace NotchTasks.Core.Tests.Sync;

/// Reads the Swift-sealed crosscheck fixtures, which the test project copies from electron/test/fixtures/crosscheck unchanged.
internal static class Crosscheck
{
    /// The fixture's text with its surrounding whitespace trimmed, as the Electron tests read it.
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "crosscheck", name)).Trim();
}
