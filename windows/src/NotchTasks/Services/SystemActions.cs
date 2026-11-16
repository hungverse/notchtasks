using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NotchTasks.Core.Asks;

namespace NotchTasks.Services;

/// The actions the permission and agent panes start: a Settings page, the firewall rule (with the UAC prompt) and a terminal.
public static class SystemActions
{
    /// Opens a ms-settings: page.
    public static void OpenSettings(string uri)
    {
        try
        {
            Launch(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// Adds the inbound firewall rule for the app, through an elevated netsh (one UAC prompt). Returns true when netsh succeeded.
    /// A UAC prompt the user cancels is not an error. Call off the UI thread: it waits for netsh.
    public static bool AddFirewallRule(string appExe)
    {
        var args = $"advfirewall firewall add rule name=\"{PermissionProbe.FirewallRuleName}\" dir=in action=allow program=\"{appExe}\" enable=yes";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user said no to the UAC prompt.
            return false;
        }
    }

    /// Runs an agent's command (codex, gemini) in a terminal: Windows Terminal when it is installed, else a cmd window.
    public static void OpenInTerminal(string command)
    {
        try
        {
            var script = AgentSetup.WriteTerminalScript(command);
            try
            {
                Launch(new ProcessStartInfo("wt.exe", $"cmd /k \"{script}\"") { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                Launch(new ProcessStartInfo("cmd.exe", $"/k \"{script}\"") { UseShellExecute = true });
            }
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(e);
        }
    }

    /// Starts a program without waiting for it, and disposes the Process handle at once.
    private static void Launch(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
    }
}
