using System.IO;

namespace NotchTasks.Core.Sensors;

/// The helper's error report. It goes to stderr only. The helper never writes a log file: a file under a folder that ordinary users
/// can create (or a junction in one) would let a user redirect the SYSTEM helper's write, and the pipe could trigger the write.
public static class SensorLog
{
    /// The line for one error at a time.
    public static string Line(Exception error, DateTime at) =>
        $"[{at:yyyy-MM-dd HH:mm:ss}] {error}";

    /// Writes the line to stderr. Nothing else: no file, no folder.
    public static void Report(Exception error, TextWriter stderr)
    {
        try
        {
            stderr.WriteLine(Line(error, DateTime.Now));
        }
        catch (IOException)
        {
            // No stderr to write to: the report is dropped.
        }
    }
}
