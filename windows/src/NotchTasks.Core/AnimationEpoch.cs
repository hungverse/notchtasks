using System.Text.Json.Nodes;

namespace NotchTasks.Core;

/// The Sync motion moment, kept in state.json as "animationEpoch" (Unix ms). Every animated icon's frame 0 falls on it, so all
/// icons restart together. Read at start; written when Sync motion is used.
public static class AnimationEpoch
{
    public const string Key = "animationEpoch";

    /// The stored moment in Unix ms, or 0 when none is stored or the file cannot be read.
    public static long Read(string stateFile)
    {
        try
        {
            return StateJson.Get(stateFile, Key) is JsonValue value && value.TryGetValue(out long ms) ? ms : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// Stores the moment. Throws IOException or UnauthorizedAccessException when state.json cannot be written.
    public static void Write(string stateFile, long epochMs) => StateJson.Set(stateFile, Key, JsonValue.Create(epochMs));
}
