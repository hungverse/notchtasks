using System.Text.Json;
using NotchTasks.Core.Asks;

namespace NotchTasks.Core.Agents;

/// Which models LM Studio and Ollama have loaded (Electron: providers.js lmStudioLoaded, parseLMStudio, ollamaLoaded,
/// parseOllama; Mac: Providers.swift LocalModels). Core only parses and decides; the app starts `lms` (Services/LmsRunner).
public static class LocalModels
{
    private const string OllamaPsUrl = "http://127.0.0.1:11434/api/ps";
    private static readonly TimeSpan OllamaTimeout = TimeSpan.FromSeconds(1.5);

    /// The models in `lms ps --json`. The lms CLI is found by AgentSetup.FindCli, else at ~/.lmstudio/bin/lms.exe; when there
    /// is none, runLmsPsJson is not called and the list is empty. runLmsPsJson takes the lms path and returns the command's
    /// stdout, or null when it fails. `path` is the PATH to search (null: this process's PATH); `appData` as in FindCli.
    public static IReadOnlyList<string> LmStudioLoaded(string home, Func<string, string?> runLmsPsJson, string? path = null, string? appData = null)
    {
        var lms = AgentSetup.FindCli("lms", home, path, appData) ?? Path.Combine(home, ".lmstudio", "bin", "lms.exe");
        if (!File.Exists(lms))
        {
            return Array.Empty<string>();
        }
        var output = runLmsPsJson(lms);
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

}
