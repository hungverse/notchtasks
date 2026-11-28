using NotchTasks.Core.Sensors;
using NotchTasksSensors;

// NotchTasksSensors.exe: reads the temperatures for the notch. It runs elevated (the CPU sensors need the PawnIO driver, and WMI's
// thermal zones need administrator), only when the user turned the feature on. It never shows a window or a message: a failure is an
// exit code.
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
                SensorMode.Serve => await ServeAsync(command),
                SensorMode.InstallLogon => await InstallLogonAsync(command),
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

    private static async Task<int> InstallLogonAsync(SensorCommand command)
    {
        using var reader = CreateReader();
        return await SensorInstaller.InstallLogonAsync(command.Sid!, command.Parent, reader);
    }

    private static async Task<int> ServeAsync(SensorCommand command)
    {
        using var reader = CreateReader();
        return await SensorHost.ServeAsync(command.Sid!, command.Parent, reader);
    }

    /// The CPU and GPU readings from the hardware library, with the WMI zones as the fallback. The library's Computer is closed with the
    /// reader. A library that cannot open gives no hardware, so the zones answer alone.
    internal static HardwareThermalReader CreateReader()
    {
        IHardwareSource hardware;
        try
        {
            hardware = new LibreHardwareSource();
        }
        catch (Exception e)
        {
            HelperLog.Once(e);
            hardware = new NoHardwareSource();
        }
        return new HardwareThermalReader(hardware, new WmiThermalReader());
    }
}
