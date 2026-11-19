using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using NotchTasks.Core.Asks;

namespace NotchTasks.Core.Agents;

/// Which models LM Studio and Ollama have loaded (Electron: providers.js lmStudioLoaded, parseLMStudio, ollamaLoaded,
/// parseOllama; Mac: Providers.swift LocalModels).
public static class LocalModels
{
    private const string OllamaPsUrl = "http://127.0.0.1:11434/api/ps";
    private static readonly TimeSpan OllamaTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LmsTimeout = TimeSpan.FromSeconds(5);

    /// The models in `lms ps --json`. The lms CLI is found by AgentSetup.FindCli, else at ~/.lmstudio/bin/lms.exe. Empty when
    /// there is no lms or it fails. Call it only while LM Studio runs. `path` is the PATH to search (null: this process's PATH).
    public static IReadOnlyList<string> LmStudioLoaded(string home, string? path = null)
    {
        var lms = AgentSetup.FindCli("lms", home, path) ?? Path.Combine(home, ".lmstudio", "bin", "lms.exe");
        if (!File.Exists(lms))
        {
            return Array.Empty<string>();
        }
        var output = RunLms(lms);
        return output is null ? Array.Empty<string>() : ParseLMStudio(output);
    }

    /// `lms ps --json`: [{displayName, identifier, modelKey}]. An entry gives its displayName, else its identifier, else its
    /// modelKey. Any entry that is not an object makes the whole list empty.
    public static IReadOnlyList<string> ParseLMStudio(string text)
    {
        using var doc = JsonFields.TryParse(text);
        if (doc is null || !JsonFields.IsObjectList(doc.RootElement))
        {
            return Array.Empty<string>();
        }
        return doc.RootElement.EnumerateArray()
            .Select(m => JsonFields.Str(JsonFields.Field(m, "displayName"))
                ?? JsonFields.Str(JsonFields.Field(m, "identifier"))
                ?? JsonFields.Str(JsonFields.Field(m, "modelKey")))
            .OfType<string>()
            .ToList();
    }

    /// Ollama's /api/ps: {"models": [{"name": "qwen3:1.7b"}]}. Empty when it is not running (the request fails or times out).
    public static async Task<IReadOnlyList<string>> OllamaLoadedAsync(IHttpGet http, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? body;
        try
        {
            body = await http.GetAsync(OllamaPsUrl, OllamaTimeout);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
        return body is null ? Array.Empty<string>() : ParseOllama(body);
    }

    /// The model names in Ollama's /api/ps reply. Any entry that is not an object makes the list empty.
    public static IReadOnlyList<string> ParseOllama(string text)
    {
        using var doc = JsonFields.TryParse(text);
        if (doc is null)
        {
            return Array.Empty<string>();
        }
        var models = JsonFields.Field(doc.RootElement, "models");
        if (!JsonFields.IsObjectList(models))
        {
            return Array.Empty<string>();
        }
        return models.EnumerateArray()
            .Select(m => JsonFields.Str(JsonFields.Field(m, "name")))
            .OfType<string>()
            .ToList();
    }

    /// Runs `lms ps --json` and returns its output, or null when it fails or does not finish in time. Output and errors are
    /// read while it runs, so a chatty process cannot stall on a full pipe.
    private static string? RunLms(string exe)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("ps");
            info.ArgumentList.Add("--json");
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(LmsTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return output.Wait(TimeSpan.FromSeconds(1)) ? output.Result : null;
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
