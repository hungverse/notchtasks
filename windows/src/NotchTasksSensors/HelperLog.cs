using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The helper's error report: the first failure of a run goes to stderr, the rest are not. No log file is written in any mode (see
/// SensorLog).
internal static class HelperLog
{
    private static bool _reported;

    public static void Once(Exception error)
    {
        if (_reported)
        {
            return;
        }
        _reported = true;
        SensorLog.Report(error, Console.Error);
    }
}
