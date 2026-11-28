namespace NotchTasksSensors;

/// The stages of the helper's run. Each one that throws ends the run with its own exit code, and reports the exception (event 1000).
internal enum HelperStage
{
    /// The command line is parsed.
    Arguments,

    /// The temperature reader is made (the hardware library and the WMI fallback).
    Reader,

    /// The pipe is served (SensorHost.ServeAsync).
    Serve,

    /// The sign-in install runs (SensorInstaller.InstallLogonAsync).
    Install,

    /// Anything else: the outermost catch.
    Other,
}

/// The helper's exit codes per stage, and the stage runner that maps a failure to its code. The app's log line prints the code.
internal static class HelperExit
{
    public const int Other = 1;
    public const int Arguments = 10;
    public const int Reader = 11;
    public const int Serve = 12;
    public const int Install = 13;

    /// The event id of a stage's failure (every stage uses it: the code says which one).
    public const int StageEvent = 1000;

    /// The event id of HelperLog's first failure of a run.
    public const int HelperLogEvent = 1001;

    /// The exit code of a stage that threw.
    public static int CodeOf(HelperStage stage) => stage switch
    {
        HelperStage.Arguments => Arguments,
        HelperStage.Reader => Reader,
        HelperStage.Serve => Serve,
        HelperStage.Install => Install,
        _ => Other,
    };

    /// Runs one stage. Its result is returned; an exception is reported to the sink and becomes the stage's code. An out-of-memory
    /// exception is not a stage failure and is not caught.
    public static int Run(HelperStage stage, Func<int> body, IFailureSink sink)
    {
        try
        {
            return body();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(sink, e);
            return CodeOf(stage);
        }
    }

    /// The same for an asynchronous stage.
    public static async Task<int> RunAsync(HelperStage stage, Func<Task<int>> body, IFailureSink sink)
    {
        try
        {
            return await body();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(sink, e);
            return CodeOf(stage);
        }
    }

    /// Hands the failure to the sink. A sink that throws is ignored: the exit code is the answer.
    private static void Report(IFailureSink sink, Exception error)
    {
        try
        {
            sink.Report(error, StageEvent);
        }
        catch (Exception)
        {
            // Ignored by design (see EventLogFailureSink).
        }
    }
}
