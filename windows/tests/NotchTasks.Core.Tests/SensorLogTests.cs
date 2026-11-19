using System.IO;
using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The helper's error report writes to stderr only, in every mode. These tests pin that: no file is created, in the Program Files
/// copy's folder, in the app's folder, or under %ProgramData%.
public sealed class SensorLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nt-sensorlog-" + Guid.NewGuid().ToString("N"));

    public SensorLogTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// Every path under the folder (files and folders), as a sorted list. Read only.
    private static List<string> Snapshot(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : new List<string>();

    private static string ProgramDataLog =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NotchTasks", "sensors.log");

    /// Reports one error and returns what went to stderr.
    private static string Report(Exception error)
    {
        using var stderr = new StringWriter();
        SensorLog.Report(error, stderr);
        return stderr.ToString();
    }

    [Fact]
    public void FromProgramFilesCopy_WritesNoFile()
    {
        // The Program Files copy's folder: the report goes to stderr and creates nothing there.
        var programFilesCopy = Path.Combine(_root, "Program Files", "NotchTasks Sensors");
        Directory.CreateDirectory(programFilesCopy);
        var before = Snapshot(programFilesCopy);
        var programDataBefore = File.Exists(ProgramDataLog);

        var line = Report(new InvalidOperationException("boom"));

        Assert.Contains("boom", line);
        Assert.Equal(before, Snapshot(programFilesCopy));
        Assert.Equal(programDataBefore, File.Exists(ProgramDataLog));
    }

    [Fact]
    public void FromTheAppFolder_WritesNoFile()
    {
        // A runas helper, started from the user's own app folder: nothing is written next to the exe or under ProgramData.
        var appFolder = Path.Combine(_root, "app");
        Directory.CreateDirectory(appFolder);
        var before = Snapshot(appFolder);
        var programDataBefore = File.Exists(ProgramDataLog);

        var line = Report(new InvalidOperationException("boom"));

        Assert.Contains("boom", line);
        Assert.Equal(before, Snapshot(appFolder));
        Assert.Equal(programDataBefore, File.Exists(ProgramDataLog));
    }

    [Fact]
    public void Line_HoldsTheTimeAndTheError()
    {
        var at = new DateTime(2026, 11, 20, 9, 30, 15);

        Assert.Equal("[2026-11-20 09:30:15] System.InvalidOperationException: boom",
            SensorLog.Line(new InvalidOperationException("boom"), at));
    }
}
