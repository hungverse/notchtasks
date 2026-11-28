using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The helper's error report: the first failure of a run goes to stderr and to the Application event log (event 1001); the rest are not
/// reported. No log file is written in any mode (see SensorLog).
internal static class HelperLog
{
    private static bool _reported;

    /// Where the first failure goes. The real one is the event log; a test passes a fake.
    public static IFailureSink Sink { get; set; } = new EventLogFailureSink();

    public static void Once(Exception error)
    {
        if (_reported)
        {
            return;
        }
        _reported = true;
        SensorLog.Report(error, Console.Error);
        try
        {
            Sink.Report(error, HelperExit.HelperLogEvent);
        }
        catch (Exception)
        {
            // Ignored by design: the event is best effort.
        }
    }
}
