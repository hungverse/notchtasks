using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;

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
    // agent whose settings point at this exe, and our Claude status line when it points at this exe. It shows nothing.
    if (args is ["--uninstall-hooks"])
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "NotchTasksHook.exe");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        HookInstaller.UninstallPointingAt(exe, home);
        try
        {
            StatuslineInstaller.UninstallPointingAt(exe, home);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Claude's settings file is left as it is; the uninstall goes on.
        }
        return 0;
    }

    // NotchTasksHook.exe --statusline: Claude Code runs this on each statusline update, with the statusline JSON on stdin. The
    // JSON is saved for the app's plan limits, and the limits line is printed ("5h 12% · 7d 3%", nothing when there are none).
    if (args is ["--statusline"])
    {
        return await StatuslineAsync();
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

/// Saves the statusline JSON in the Claude folder and prints the limits line. A save that fails is not reported: the line still
/// prints, from what the JSON says.
static async Task<int> StatuslineAsync()
{
    var json = await ReadStdinAsync();
    try
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ClaudeStatusline.Save(AgentKinds.ClaudeDir(home), json);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        // The folder cannot be written now; the next statusline update tries again.
    }

    var line = ClaudeStatusline.Summary(json);
    if (line.Length > 0)
    {
        using var output = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
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
