using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Updates;

/// Checks the feed for a newer build and downloads its installer. Port of electron/src/core/updater.js (checkForUpdate,
/// download, fetchInstaller) and Updater.swift (fetch, UpdateDownload).
// shortcut: no Authenticode check on the installer; the sha256 from the https manifest is the only check.
public sealed class UpdateClient
{
    /// The Windows feed. A feed URL that is not allowed falls back to this one, as in updater.js.
    public const string DefaultFeed = "https://notchtasks.web.app/version-win.json";

    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;

    /// The handler is a test's fake. The app passes none and gets a socket handler that does not follow redirects, because
    /// they are followed here one by one, each target checked.
    public UpdateClient(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan, // each request sets its own limits
        };
    }

    /// The manifest when it is newer than appBuild and this Windows (os) meets its minOS; otherwise null. Null too for a 404,
    /// a body that is not JSON or is over 64 KiB, or an entry that is not valid. Throws UpdateFailure(Download) when the feed
    /// cannot be reached or answers with an error status.
    public async Task<UpdateManifest?> CheckAsync(string feed, int appBuild, Version os, CancellationToken ct)
    {
        var raw = await ReadManifestAsync(UpdateRules.IsAllowedUrl(feed) ? feed : DefaultFeed, ct);
        UpdateManifest manifest;
        try
        {
            manifest = UpdateManifest.Parse(raw);
        }
        catch (UpdateFailure)
        {
            return null;
        }
        var current = new[] { os.Major, os.Minor, Math.Max(os.Build, 0) };
        if (!UpdateRules.IsNewer(manifest.Build, appBuild) || UpdateRules.CompareOS(current, UpdateRules.ParseOS(manifest.MinOS)!) < 0)
        {
            return null;
        }
        return manifest;
    }

    /// Downloads the installer into tmpDir as NotchTasks-update-{build}.exe and checks its sha256 against the manifest. Returns
    /// the path. The file is removed when the download fails, and on a checksum mismatch (UpdateFailure(Hash)). progress reports
    /// 0 to 1 while the length is known.
    public async Task<string> DownloadAsync(UpdateManifest manifest, string tmpDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(tmpDir);
        var dest = Path.Combine(tmpDir, $"NotchTasks-update-{manifest.Build}.exe");
        DeleteQuietly(dest);
        using var response = await GetAsync(manifest.Url, DownloadTimeout, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateFailure(UpdateFailureKind.Download);
        }
        try
        {
            await WriteBodyAsync(response, dest, response.Content.Headers.ContentLength ?? 0, progress, ct);
        }
        catch (Exception e)
        {
            DeleteQuietly(dest);
            if (IsNetworkFailure(e, ct))
            {
                throw new UpdateFailure(UpdateFailureKind.Download);
            }
            throw;
        }
        if (!await HashMatchesAsync(dest, manifest.Sha256, ct))
        {
            DeleteQuietly(dest);
            throw new UpdateFailure(UpdateFailureKind.Hash);
        }
        return dest;
    }

    /// Deletes the installers an earlier run downloaded and did not start (Mac removeLeftoversAtLaunch). Run at launch.
    public static void RemoveLeftovers(string tmpDir)
    {
        if (!Directory.Exists(tmpDir))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(tmpDir, "NotchTasks-update-*.exe"))
        {
            DeleteQuietly(file);
        }
    }

    // The feed body as parsed JSON, or null for a 404, a body over the cap, or text that is not JSON.
    private async Task<JsonNode?> ReadManifestAsync(string url, CancellationToken ct)
    {
        using var response = await GetAsync(url, ManifestTimeout, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateFailure(UpdateFailureKind.Download);
        }
        var body = await ReadCappedAsync(response, ct);
        if (body is null)
        {
            return null;
        }
        // A UTF-8 byte order mark (a feed written by Windows PowerShell) is skipped; the decoder keeps it as U+FEFF otherwise.
        var text = Encoding.UTF8.GetString(body);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }
        try
        {
            var node = JsonNode.Parse(text);
            // A duplicate key throws only when its object is read, so read the top level here: such a feed is then "no update", as
            // other text that is not JSON is.
            if (node is JsonObject top)
            {
                foreach (var property in top)
                {
                    _ = property.Key;
                }
            }
            return node;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }

    // GET with the redirects followed by hand: each target must be https (or loopback http), and there are at most five.
    // Returns the response with its headers; the body is read by the caller.
    private async Task<HttpResponseMessage> GetAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var target = url;
        for (var hops = 0; ; hops++)
        {
            var response = await GetHeadersAsync(target, timeout, ct);
            var code = (int)response.StatusCode;
            var location = response.Headers.Location;
            if (code < 300 || code > 399 || location is null)
            {
                return response;
            }
            response.Dispose();
            var next = new Uri(new Uri(target), location).ToString();
            if (hops >= MaxRedirects || !UpdateRules.IsAllowedUrl(next))
            {
                throw new UpdateFailure(UpdateFailureKind.Download);
            }
            target = next;
        }
    }

    private async Task<HttpResponseMessage> GetHeadersAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = Limit(ct, timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            throw new UpdateFailure(UpdateFailureKind.Download);
        }
    }

    // The body up to the 64 KiB cap; null when it is over the cap.
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            while (true)
            {
                var read = await ReadIdleAsync(stream, buffer, ManifestTimeout, ct);
                if (read == 0)
                {
                    return ms.ToArray();
                }
                if (ms.Length + read > MaxManifestBytes)
                {
                    return null;
                }
                ms.Write(buffer, 0, read);
            }
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            throw new UpdateFailure(UpdateFailureKind.Download);
        }
    }

    private static async Task WriteBodyAsync(HttpResponseMessage response, string dest, long total, IProgress<double>? progress,
        CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        var buffer = new byte[81920];
        long got = 0;
        while (true)
        {
            var read = await ReadIdleAsync(body, buffer, DownloadTimeout, ct);
            if (read == 0)
            {
                return;
            }
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            got += read;
            if (total > 0)
            {
                progress?.Report(Math.Min(1, got / (double)total));
            }
        }
    }

    // One read, given the idle limit: a stream that stalls for that long is a download failure.
    private static async Task<int> ReadIdleAsync(Stream stream, byte[] buffer, TimeSpan idle, CancellationToken ct)
    {
        using var limit = Limit(ct, idle);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(), limit.Token);
        }
        catch (Exception e) when (IsNetworkFailure(e, ct))
        {
            throw new UpdateFailure(UpdateFailureKind.Download);
        }
    }

    private static async Task<bool> HashMatchesAsync(string file, string sha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    // A network failure or a request time limit (not the caller's own cancellation) is a download failure.
    private static bool IsNetworkFailure(Exception e, CancellationToken ct) =>
        e is HttpRequestException or IOException or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private static CancellationTokenSource Limit(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Left for the next launch's RemoveLeftovers.
        }
        catch (UnauthorizedAccessException)
        {
            // Left for the next launch's RemoveLeftovers.
        }
    }
}
