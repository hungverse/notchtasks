using NotchTasks.Core.Sensors;
using NotchTasksSensors;

// NotchTasksSensors.exe: reads the temperatures for the notch. It runs elevated (WMI's thermal zones need administrator), only
// when the user turned the feature on. It never shows a window or a message: a failure is an exit code.
//   --serve [--parent pid] --user sid   answers the app's pipe; exits when pid ends, else stays until logoff or until stopped
//   --install-logon sid [--parent pid]  copies the helper to Program Files, registers the logon task, then serves
//   --uninstall-logon [sid]             stops the helper, removes the logon task and the Program Files copy
//   --uninstall-own                     not elevated: asks for one UAC prompt, then --uninstall-logon for this user (the installer's opt-in)
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var command = SensorArgs.Parse(args);
            if (command is null)
            {
                return 2;
            }
            return command.Mode switch
            {
                SensorMode.Serve => await SensorHost.ServeAsync(command.Sid!, command.Parent, new WmiThermalReader()),
                SensorMode.InstallLogon => await SensorInstaller.InstallLogonAsync(command.Sid!, command.Parent),
                SensorMode.UninstallLogon => await SensorInstaller.UninstallLogonAsync(command.Sid),
                SensorMode.UninstallOwn => await SensorInstaller.RemoveOwnSignInAsync(),
                _ => 2,
            };
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
