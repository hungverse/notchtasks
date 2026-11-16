using System.Text.Json.Nodes;
using NotchTasks.Core;

namespace NotchTasks.Core.Tests;

/// Shared helpers for the Asks tests: the agent payload fixtures and the English L10n tables live in the repo.
internal static class AsksFixtures
{
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "electron", "test", "fixtures", "agents")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("The repo root with electron/test/fixtures/agents was not found.");
    }

    /// One agent payload, parsed fresh (tests may change it).
    public static JsonObject Fixture(string name)
    {
        var path = Path.Combine(RepoRoot(), "electron", "test", "fixtures", "agents", name + ".json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    public static void InitEnglish() => L10n.Init(Path.Combine(RepoRoot(), "mac", "Sources"), "en");

    public static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nt-asks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
