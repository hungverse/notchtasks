using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/providers.test.js: "LM Studio and Ollama lists are parsed". The Ollama request goes to a fake
// IHttpGet; the LM Studio CLI is never run (the home folder is a temp folder with no lms, and PATH is empty).
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

    [Fact]
    public void LmStudioLoaded_NoCli_IsEmpty_AndNothingRuns()
    {
        // PATH is passed empty and the home folder is a temp folder, so no lms is found and none is run.
        Assert.Empty(LocalModels.LmStudioLoaded(Temp(), path: string.Empty));
    }

    [Fact]
    public async Task OllamaLoaded_AsksApiPs_WithATwoSecondTimeout()
    {
        var http = new FakeHttpGet { Reply = "{\"models\":[{\"name\":\"qwen3:1.7b\"}]}" };

        var names = await LocalModels.OllamaLoadedAsync(http);

        Assert.Equal("http://127.0.0.1:11434/api/ps", http.Url);
        Assert.Equal(TimeSpan.FromSeconds(2), http.Timeout);
        Assert.Equal(new[] { "qwen3:1.7b" }, names);
    }

    [Fact]
    public async Task OllamaLoaded_NotRunning_IsEmpty()
    {
        Assert.Empty(await LocalModels.OllamaLoadedAsync(new FakeHttpGet { Reply = null }));
    }
}
