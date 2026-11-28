using NotchTasks.Core.Sensors;
using NotchTasksSensors;

// NotchTasksSensors.exe: reads the temperatures for the notch. It runs elevated (the CPU sensors need the PawnIO driver, and WMI's
// thermal zones need administrator), only when the user turned the feature on. It never shows a window or a message: a failure is an
// exit code, and its readable line goes to stderr and to the Application event log ("NotchTasks Sensors").
//   --install-pawnio                    installs the PawnIO driver when it is missing, then exits (the code is PawnIOExit)
//   --install-pawnio <serve or install-logon> runs that step first in the same process; a failed step ends it with its code
//   --serve [--parent pid] --user sid   answers the app's pipe; exits when pid ends, else stays until logoff or until stopped
//   --install-logon sid [--parent pid]  copies the helper to Program Files, registers the logon task, then serves
//   --uninstall-logon [sid]             stops the helper, removes the logon task and the Program Files copy
//   --uninstall-own                     not elevated: asks for one UAC prompt, then --uninstall-logon for this user (the installer's opt-in)
// Exit codes: 1 anything else; 10 the arguments; 11 the reader (the hardware library, the WMI fallback); 12 the pipe server; 13 the
// sign-in install. A PawnIO step failure is its PawnIOExit code (40 to 44).
internal static class Program
{
    private static Task<int> Main(string[] args) =>
        HelperExit.RunAsync(HelperStage.Other, () => RunAsync(args), HelperLog.Sink);

    private static async Task<int> RunAsync(string[] args)
    {
        SensorCommand? command = null;
        var parsed = HelperExit.Run(HelperStage.Arguments, () =>
        {
            command = SensorArgs.Parse(args);
            return 0;
        }, HelperLog.Sink);
        if (parsed != 0)
        {
            return parsed;
        }
        if (command is null)
        {
            return 2;
        }

        return command.Mode switch
        {
            SensorMode.InstallPawnIO => await PawnIOStepAsync(),
            SensorMode.Serve => await ServeAsync(command),
            SensorMode.InstallLogon => await InstallLogonAsync(command),
            SensorMode.UninstallLogon => await SensorInstaller.UninstallLogonAsync(command.Sid),
            SensorMode.UninstallOwn => await SensorInstaller.RemoveOwnSignInAsync(),
            _ => 2,
        };
    }

    /// The PawnIO step a command asks for (--install-pawnio before it). A failed step ends the command with its code, and its line goes
    /// to stderr; the command does not run then.
    private static async Task<int> PawnIOStepAsync(SensorCommand command)
    {
        return command.InstallPawnIO ? await PawnIOStepAsync() : PawnIOExit.Installed;
    }

    private static async Task<int> PawnIOStepAsync()
    {
        var code = await new PawnIOInstaller(new WindowsPawnIOSystem()).RunAsync();
        if (code != PawnIOExit.Installed)
        {
            Console.Error.WriteLine(PawnIOExit.Message(code));
        }
        return code;
    }

    private static async Task<int> InstallLogonAsync(SensorCommand command)
    {
        var step = await PawnIOStepAsync(command);
        if (step != PawnIOExit.Installed)
        {
            return step;
        }
        HardwareThermalReader? made = null;
        var code = HelperExit.Run(HelperStage.Reader, () =>
        {
            made = MakeReader();
            return 0;
        }, HelperLog.Sink);
        if (code != 0)
        {
            return code;
        }
        using var reader = made!;
        return await HelperExit.RunAsync(HelperStage.Install,
            () => SensorInstaller.InstallLogonAsync(command.Sid!, command.Parent, reader), HelperLog.Sink);
    }

    private static async Task<int> ServeAsync(SensorCommand command)
    {
        var step = await PawnIOStepAsync(command);
        if (step != PawnIOExit.Installed)
        {
            return step;
        }
        HardwareThermalReader? made = null;
        var code = HelperExit.Run(HelperStage.Reader, () =>
        {
            made = MakeReader();
            return 0;
        }, HelperLog.Sink);
        if (code != 0)
        {
            return code;
        }
        using var reader = made!;
        return await HelperExit.RunAsync(HelperStage.Serve,
            () => SensorHost.ServeAsync(command.Sid!, command.Parent, reader), HelperLog.Sink);
    }

    /// The reader for the real machine: LibreHardwareMonitor (opened here), with the WMI zones as the fallback.
    private static HardwareThermalReader MakeReader() =>
        HelperReaders.CreateReader(HelperReaders.OpenLibreHardware, new WmiThermalReader());
}
