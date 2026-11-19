using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace NotchTasks.Services;

/// How a CLI is started. Direct: FileName runs with Prefix then the arguments, with no shell. Shim: an npm .cmd or .bat run by
/// cmd.exe, which only ever takes constant arguments (the Claude usage call).
public enum CliKind { Direct, Shim }

/// A command to start: its kind, the program (the exe, the shim, or node.exe), and for a Direct node run the script first.
public sealed record CliCommand(CliKind Kind, string FileName, IReadOnlyList<string> Prefix);

/// Finds and runs the agent CLIs (claude, codex) on Windows. Every candidate in the usual folders is collected; a real .exe is
/// preferred. An npm shim is replaced by its node.exe and JS entry when those are found, so the shim never runs. A .cmd or .bat
/// that is still used runs only through cmd.exe, and only when its path is fully qualified, exists and is clean, and only with
/// constant arguments. A .ps1 is never run. Nothing here starts a shell for user text.
public static partial class CliLauncher
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".ps1"];
    private static readonly char[] Unsafe = { '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n' };

    [GeneratedRegex("\"%dp0%\\\\([^\"\\r\\n]+?\\.js)\"")]
    private static partial Regex ShimScriptRe();

    /// The command that runs name, or null. A .exe wins over every shim. Of the shims, a clean .cmd or .bat is the choice only
    /// when its node.exe and JS entry cannot be found. home and appData are as in AgentSetup.FindCli; path is the PATH to search
    /// (null: this process's PATH).
    public static CliCommand? Resolve(string name, string home, string? path = null, string? appData = null)
    {
        var found = Candidates(name, home, path, appData);
        var exe = found.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (exe is not null)
        {
            return new CliCommand(CliKind.Direct, exe, Array.Empty<string>());
        }
        var shim = found.FirstOrDefault(IsShim);
        if (shim is null)
        {
            return null;
        }
        return NodeEntry(shim, path) ?? new CliCommand(CliKind.Shim, shim, Array.Empty<string>());
    }

    /// Like Resolve, but never a shim: for a call whose arguments are user text. Null when the CLI is only a shim with no node entry.
    public static CliCommand? ResolveDirect(string name, string home, string? path = null, string? appData = null)
    {
        var command = Resolve(name, home, path, appData);
        return command is { Kind: CliKind.Direct } ? command : null;
    }

    /// Every existing file called name with one of the extensions, in the search order: PATH, then the npm folder under
    /// %APPDATA%, then ~/.local/bin, ~/.npm-global/bin and ~/.opencode/bin. Extensions are tried in order within a folder.
    public static IReadOnlyList<string> Candidates(string name, string home, string? path = null, string? appData = null)
    {
        var dirs = (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        appData ??= Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            dirs.Add(Path.Combine(appData, "npm"));
        }
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(home, ".npm-global", "bin"));
        dirs.Add(Path.Combine(home, ".opencode", "bin"));

        var list = new List<string>();
        foreach (var dir in dirs)
        {
            foreach (var extension in Extensions)
            {
                var file = Path.Combine(dir, name + extension);
                if (File.Exists(file))
                {
                    list.Add(file);
                }
            }
        }
        return list;
    }

    /// True for a clean .cmd or .bat: fully qualified, existing, with none of the characters cmd.exe reads.
    public static bool IsShim(string path) =>
        (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        && IsClean(path);

    /// True when the path is fully qualified, exists, and has none of the characters cmd.exe treats specially.
    public static bool IsClean(string path) =>
        Path.IsPathFullyQualified(path) && File.Exists(path) && path.IndexOfAny(Unsafe) < 0;

    /// The node.exe and JS entry an npm shim runs ("%dp0%\...\bin\codex.js"), or null when they are not both there. node.exe is
    /// the one beside the shim, else the first on PATH. The script must be inside the shim's folder.
    public static CliCommand? NodeEntry(string shim, string? path = null)
    {
        string text;
        try
        {
            text = File.ReadAllText(shim);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;  // a path Windows cannot open, or one with characters it refuses: there is no entry
        }
        var match = ShimScriptRe().Match(text);
        if (!match.Success)
        {
            return null;
        }
        string dir;
        string script;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(shim)) ?? string.Empty;
            script = Path.GetFullPath(Path.Combine(dir, match.Groups[1].Value));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;  // the script name the shim gives is not a path Windows accepts
        }
        if (!script.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
        {
            return null;
        }
        var node = File.Exists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : FindOnPath("node.exe", path);
        if (node is null || !node.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new CliCommand(CliKind.Direct, node, new[] { script });
    }

    private static string? FindOnPath(string file, string? path)
    {
        foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, file);
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }

    /// Runs the command and returns its standard output, or null when it cannot start, fails to finish within limit, or ct is
    /// cancelled. The process tree is killed on timeout or cancel, and partial output is never returned. Standard input is
    /// closed at once; there is no window; the working folder is the temp folder. A shim takes only constant arguments.
    /// With requireZeroExit, a run that exits with any other code is null too, so a failed call reads as not done.
    public static string? Run(CliCommand command, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct, bool requireZeroExit = false)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }
        try
        {
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            if (command.Kind == CliKind.Shim)
            {
                // cmd.exe reads the arguments: only the constants the app defines, never user text
                if (!IsShim(command.FileName) || !args.All(IsConstant))
                {
                    return null;
                }
                info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                info.Arguments = $"/d /s /c \"\"{command.FileName}\" {string.Join(' ', args)}\"";
            }
            else
            {
                info.FileName = command.FileName;
                foreach (var word in command.Prefix.Concat(args))
                {
                    info.ArgumentList.Add(word);
                }
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var stop = ct.Register(() => KillTree(process));
            if (!process.WaitForExit(limit))
            {
                KillTree(process);
                return null;
            }
            if (ct.IsCancellationRequested || (requireZeroExit && process.ExitCode != 0))
            {
                return null;
            }
            return output.Wait(TimeSpan.FromSeconds(1)) ? output.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// A constant argument: letters, digits and a few punctuation marks, with no space or quote.
    private static bool IsConstant(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=');

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // it has just exited
        }
    }
}
