using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// A toast's link opens only when it is a file on this PC or an http(s) link (P3c M7). The launch itself is not tested here:
/// the check is what a toast click may do.
public sealed class ToastActionsTests
{
    [Theory]
    [InlineData("https://github.com/acme/app/actions/runs/1")]
    [InlineData("http://example.com/log")]
    [InlineData(@"C:\Users\hung\Downloads\setup.exe")]
    [InlineData("file:///C:/temp/x.txt")]
    public void WebLinksAndFiles_Open(string url) => Assert.True(ToastActions.IsOpenable(url));

    [Fact]
    public void TaskManager_Opens_OnlyTheTaskManagerLink()
    {
        // The heat toast's "See apps" opens Task Manager; no other program name opens from a toast.
        Assert.True(ToastActions.IsOpenable(ToastLinks.TaskManager));
        Assert.Equal("taskmgr", ToastLinks.TaskManager);
        Assert.False(ToastActions.IsOpenable("taskmgr.exe"));
    }

    [Theory]
    [InlineData("calc.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("cmd:/c calc")]
    public void ProgramsAndOtherSchemes_DoNotOpen(string url) => Assert.False(ToastActions.IsOpenable(url));
}
