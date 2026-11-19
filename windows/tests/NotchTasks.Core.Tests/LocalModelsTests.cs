using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/providers.test.js: "LM Studio and Ollama lists are parsed". The Ollama request goes to a fake
// IHttpGet; the LM Studio CLI runs through a fake runner, and the search uses temp folders for PATH, home and %APPDATA%.
public sealed class LocalModelsTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Still open elsewhere; the temp cleaner takes it later.
            }
        }
    }

    private string Temp()
    {
        var dir = AsksFixtures.NewTempDir();
        _dirs.Add(dir);
        return dir;
    }

    /// A GET stand-in: records the request and answers with a fixed body (null when the server did not answer).
    private sealed class FakeHttpGet : IHttpGet
    {
        public string? Url { get; private set; }
        public TimeSpan? Timeout { get; private set; }
        public string? Reply { get; init; }

        public Task<string?> GetAsync(string url, TimeSpan timeout)
        {
            Url = url;
            Timeout = timeout;
            return Task.FromResult(Reply);
        }
    }

    [Fact]
    public void LmStudioAndOllama_Lists_AreParsed()
    {
        Assert.Equal(new[] { "Gemma 4 26B" }, LocalModels.ParseLMStudio("[{\"displayName\":\"Gemma 4 26B\",\"identifier\":\"g\"}]"));
        Assert.Equal(new[] { "only-id" }, LocalModels.ParseLMStudio("[{\"identifier\":\"only-id\"}]"));
        Assert.Empty(LocalModels.ParseLMStudio("nope"));
        Assert.Equal(new[] { "qwen3:1.7b" }, LocalModels.ParseOllama("{\"models\":[{\"name\":\"qwen3:1.7b\"}]}"));
        Assert.Empty(LocalModels.ParseOllama("{}"));
    }

    [Fact]
    public void ParseLMStudio_ListWithANonObjectEntry_IsEmpty()
    {
        Assert.Empty(LocalModels.ParseLMStudio("[{\"displayName\":\"A\"}, 3]"));
        Assert.Empty(LocalModels.ParseLMStudio("{\"displayName\":\"A\"}"));
    }

    /// A runner stand-in: records the exe it was asked to run and answers with a fixed output (null: it failed).
    private sealed class FakeLmsRunner
    {
        public List<string> Runs { get; } = new();
        public string? Output { get; init; }

        public string? Run(string exe)
        {
            Runs.Add(exe);
            return Output;
        }
    }

    [Fact]
    public void LmStudioLoaded_NoCli_IsEmpty_AndTheRunnerIsNeverCalled()
    {
        // PATH is empty, and both the home folder and %APPDATA% are temp folders: no lms is found, so none is run.
        var runner = new FakeLmsRunner { Output = "[{\"displayName\":\"Gemma\"}]" };

        var models = LocalModels.LmStudioLoaded(Temp(), runner.Run, path: string.Empty, appData: Temp());

        Assert.Empty(models);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public void LmStudioLoaded_CliFound_RunsItAndParsesTheOutput()
    {
        var bin = Temp();
        var lms = Path.Combine(bin, "lms.exe");
        File.WriteAllText(lms, string.Empty);
        var runner = new FakeLmsRunner { Output = "[{\"displayName\":\"Gemma 4 26B\"}]" };

        var models = LocalModels.LmStudioLoaded(Temp(), runner.Run, path: bin, appData: Temp());

        Assert.Equal(new[] { "Gemma 4 26B" }, models);
        Assert.Equal(new[] { lms }, runner.Runs);
    }

    [Fact]
    public void LmStudioLoaded_RunnerFails_IsEmpty()
    {
        var bin = Temp();
        File.WriteAllText(Path.Combine(bin, "lms.exe"), string.Empty);
        var runner = new FakeLmsRunner { Output = null };

        Assert.Empty(LocalModels.LmStudioLoaded(Temp(), runner.Run, path: bin, appData: Temp()));
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task OllamaLoaded_AsksApiPs_WithAOnePointFiveSecondTimeout()
    {
        var http = new FakeHttpGet { Reply = "{\"models\":[{\"name\":\"qwen3:1.7b\"}]}" };

        var names = await LocalModels.OllamaLoadedAsync(http);

        Assert.Equal("http://127.0.0.1:11434/api/ps", http.Url);
        Assert.Equal(TimeSpan.FromSeconds(1.5), http.Timeout);
        Assert.Equal(new[] { "qwen3:1.7b" }, names);
    }

    [Fact]
    public async Task OllamaLoaded_NotRunning_IsEmpty()
    {
        Assert.Empty(await LocalModels.OllamaLoadedAsync(new FakeHttpGet { Reply = null }));
    }
}
