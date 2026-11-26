using System.IO;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ErrorLogTests
{
    [Fact]
    public void Write_AppendsToTheLogFileOfTheTest_NotTheAppDataLog()
    {
        using var root = new TempRoot();
        var file = Path.Combine(root.Folder, "logs", "windows-errors.log");
        var saved = ErrorLog.FileOverride;
        ErrorLog.FileOverride = file;
        try
        {
            ErrorLog.Write(new InvalidOperationException("error-log-seam-probe"));

            var text = File.ReadAllText(file);
            Assert.Contains("error-log-seam-probe", text);
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(file));
        }
        finally
        {
            ErrorLog.FileOverride = saved;
        }
    }

    [Fact]
    public void TheSuite_NeverWritesToTheAppDataLog()
    {
        // The test assembly points the log at a temp folder before any test runs (AssemblyInfo.cs), so no test reaches %APPDATA%.
        Assert.NotNull(ErrorLog.FileOverride);
        Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(ErrorLog.FileOverride!));
    }
}
