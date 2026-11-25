using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;

// NotchTasksHook.exe --hook <agent>: an agent runs this on each permission prompt, with the prompt's JSON on stdin.
// It writes the ask to the Asks folder, waits for the app's reply, and prints the answer in the agent's own format.
// It never prints an error or a stack trace: any failure exits 0 with no output, so the agent asks as usual.
try
{
    return await RunAsync(args);
}
catch
{
    return 0;
}

static async Task<int> RunAsync(string[] args)
{
    // NotchTasksHook.exe --uninstall-hooks: the installer runs this before it removes the app. It removes our hook from each
    // agent whose settings point at this exe, and shows nothing.
    if (args is ["--uninstall-hooks"])
    {
        HookInstaller.UninstallPointingAt(Path.Combine(AppContext.BaseDirectory, "NotchTasksHook.exe"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return 0;
    }

    if (args.Length != 2 || args[0] != "--hook" || AgentKinds.Parse(args[1]) is not { } kind)
    {
        return 0;
    }

    InitLocalization();
    var stdin = await ReadStdinAsync();
    var (stdout, _) = await HookRunner.RunAsync(kind, stdin, new HookOptions { Dir = AppPaths.AsksDir });
    if (stdout.Length > 0)
    {
        using var output = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(stdout);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }
    return 0;
}

/// The whole of stdin as UTF-8 (the agent writes its payload and closes the pipe).
static async Task<string> ReadStdinAsync()
{
    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    return await reader.ReadToEndAsync();
}

/// The language from settings.json ("" when unreadable: L10n then follows this user's UI culture).
static void InitLocalization()
{
    var language = string.Empty;
    try
    {
        if (AsksJson.ReadJsonOrNull(AppPaths.SettingsFile) is JsonObject settings && AsksJson.Str(settings["language"]) is { } code)
        {
            language = code;
        }
    }
    catch
    {
        language = string.Empty;
    }
    L10n.Init(Path.Combine(AppContext.BaseDirectory, "l10n"), language);
}
