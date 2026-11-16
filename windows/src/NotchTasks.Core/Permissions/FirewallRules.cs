namespace NotchTasks.Core.Permissions;

/// The app's inbound firewall rule, as netsh is asked for it (SystemActions.AddFirewallRule, PermissionProbe.FirewallRule).
public static class FirewallRules
{
    /// The arguments that add the rule: an inbound allow for the app's exe on the private and domain profiles (the LAN).
    public static string AddArguments(string ruleName, string appExe) =>
        $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow program=\"{appExe}\" profile=private,domain enable=yes";

    /// The arguments that show the rule by its name.
    public static string ShowArguments(string ruleName) => $"advfirewall firewall show rule name=\"{ruleName}\"";

    /// True when the rule that netsh shows is this app's: netsh exits 0, and its output names the app's exe. A rule with the same
    /// name but another program does not count.
    public static bool Shows(int exitCode, string output, string appExe) =>
        exitCode == 0 && !string.IsNullOrEmpty(appExe) && output.Contains(appExe, StringComparison.OrdinalIgnoreCase);
}
