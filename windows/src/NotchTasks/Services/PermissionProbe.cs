using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;

namespace NotchTasks.Services;

/// Reads what the Permissions pane shows. The registry and the agents' settings are read at once; the firewall rule is a
/// netsh call, so it is read off the UI thread with FirewallRule.
public static class PermissionProbe
{
    /// The firewall rule the app adds for LAN pairing (Phase 4).
    public const string FirewallRuleName = "NotchTasks";

    private const string PushKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string MicKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string CameraKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    private const string NonPackaged = @"\NonPackaged";

    /// The facts the registry and the agents' settings give. The firewall is unknown here (null): FirewallRule fills it in.
    public static PermissionFacts Facts(IRegistry registry, string home, string hookExe, string appExe)
    {
        // The per-app notification value does not exist until the installer gives the app an identity: only the global switch
        // is known, so the rows read Checking while it is on.
        var toastsOff = registry.GetString(PushKey, "ToastEnabled") == "0";

        var hooks = new Dictionary<AgentKind, bool?>();
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            hooks[kind] = HookWorks(kind, home, hookExe);
        }

        return new PermissionFacts(
            ToastsOn: toastsOff ? false : null,
            LoginOn: StartAtLogin.IsOn(registry, appExe),
            MicAllowed: Consent(registry, MicKey),
            CameraAllowed: Consent(registry, CameraKey),
            FirewallRule: null,
            Hooks: hooks);
    }

    /// True when the app's firewall rule exists (netsh exit code 0). Null when netsh does not answer within 3 s or cannot run.
    /// Call off the UI thread.
    public static bool? FirewallRule()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "netsh.exe", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                TryKill(process);
                return null;
            }
            process.WaitForExit();
            Task.WaitAll(output, errors);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// The mic or camera consent: the app's own value and its NonPackaged value (desktop apps keep theirs there).
    private static bool? Consent(IRegistry registry, string key) =>
        PermissionItems.ConsentAllowed(registry.GetString(key, "Value"), registry.GetString(key + NonPackaged, "Value"));

    /// True when the agent's hook is installed and runs this exe. Null when its settings cannot be read.
    private static bool? HookWorks(AgentKind kind, string home, string hookExe)
    {
        try
        {
            return HookInstaller.IsInstalled(kind, home) && HookInstaller.PointsAt(kind, hookExe, home);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // It ended on its own just now.
        }
    }
}
