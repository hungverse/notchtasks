using System.Diagnostics;
using System.Text;

namespace NotchTasksSensors;

/// Where a failure of the helper is written. The real one is the Windows Application event log; the tests pass a fake.
internal interface IFailureSink
{
    void Report(Exception error, int eventId);
}

/// The helper's failures in the Application event log, under the source "NotchTasks Sensors" (created when missing: the helper is elevated
/// when it fails, and so it may create the source). No log file is written. A failure to write the event is ignored: the exit code still
/// says which stage failed.
internal sealed class EventLogFailureSink : IFailureSink
{
    public const string Source = "NotchTasks Sensors";

    public void Report(Exception error, int eventId)
    {
        try
        {
            if (!EventLog.SourceExists(Source))
            {
                EventLog.CreateEventSource(Source, "Application");
            }
            EventLog.WriteEntry(Source, FailureText.Describe(error), EventLogEntryType.Error, eventId);
        }
        catch (Exception)
        {
            // Ignored by design: the event is best effort, and the exit code is the answer the app reads.
        }
    }
}

/// The text of a failure for the event log: every exception in the chain (its type, message, HResult and stack), inner exceptions and
/// aggregate members included, cut to what an event holds. Pure.
internal static class FailureText
{
    /// The most characters an event message takes (the Event Viewer keeps about 31 KB; the rest is cut).
    public const int MaxLength = 30000;

    private const int MaxDepth = 8;

    public static string Describe(Exception error)
    {
        var builder = new StringBuilder();
        Append(builder, error, string.Empty, 0);
        var text = builder.ToString();
        return text.Length <= MaxLength ? text : text[..MaxLength] + Environment.NewLine + "(cut)";
    }

    private static void Append(StringBuilder builder, Exception error, string label, int depth)
    {
        builder.Append(label).Append(error.GetType().FullName).Append(": ").AppendLine(error.Message);
        builder.Append(label).Append("HResult: 0x").AppendLine(error.HResult.ToString("X8"));
        builder.AppendLine(string.IsNullOrEmpty(error.StackTrace) ? "(no stack trace)" : error.StackTrace);
        if (depth >= MaxDepth)
        {
            return;
        }
        if (error is AggregateException aggregate)
        {
            foreach (var member in aggregate.InnerExceptions)
            {
                Append(builder, member, "Aggregate member: ", depth + 1);
            }
        }
        if (error.InnerException is { } inner)
        {
            Append(builder, inner, "Inner: ", depth + 1);
        }
    }
}
