using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The loopback HTTP: only http on 127.0.0.1 or localhost, with no user information in the URL.
public sealed class HttpLoopbackTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434/api/ps", true)]
    [InlineData("http://localhost/x", true)]
    [InlineData("https://127.0.0.1/", false)]
    [InlineData("http://192.168.0.10/", false)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://localhost.example.com/", false)]
    [InlineData("http://user@127.0.0.1:8080/", false)]
    [InlineData("http://user@localhost/", false)]
    [InlineData("http://127.0.0.1@example.com/", false)]
    [InlineData("http://localhost@example.com/", false)]
    [InlineData("ftp://127.0.0.1/", false)]
    [InlineData("127.0.0.1:8080", false)]
    [InlineData("", false)]
    public void IsLoopback_OnlyTheLoopbackHttpAddresses(string url, bool expected) =>
        Assert.Equal(expected, HttpLoopback.IsLoopback(url, out _));

    [Fact]
    public async Task NonLoopbackUrls_GiveNullWithoutARequest()
    {
        Assert.Null(await HttpLoopback.Shared.GetAsync("http://example.com/", TimeSpan.FromSeconds(1)));
        Assert.Null(await HttpLoopback.Shared.PostAsync("http://user@127.0.0.1/", new Dictionary<string, string>(), "{}", TimeSpan.FromSeconds(1)));
    }
}
