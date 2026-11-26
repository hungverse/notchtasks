using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// "Answer in Codex" finds the Codex desktop app's window: only a process from the Codex package counts, not the plain
/// ChatGPT desktop app (another package).
public sealed class CodexCliTests
{
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1101.1234.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2025.1.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", false)]
    [InlineData(@"C:\Users\Hung\AppData\Local\Programs\ChatGPT\ChatGPT.exe", false)]
    [InlineData(@"C:\Users\Hung\AppData\Local\Microsoft\WindowsApps\codex.exe", false)]
    [InlineData("", false)]
    public void IsCodexDesktopPath_OnlyTheCodexPackage(string path, bool expected)
    {
        Assert.Equal(expected, CodexCli.IsCodexDesktopPath(path));
    }
}
