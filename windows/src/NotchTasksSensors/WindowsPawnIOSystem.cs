using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NotchTasksSensors;

/// The real machine for the PawnIO step: the service controller, PATH, Process, the GitHub release and WinVerifyTrust.
internal sealed class WindowsPawnIOSystem : IPawnIOSystem
{
    private const string ReleaseUrl = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
    private const string SetupAsset = "PawnIO_setup.exe";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// sc query: exit 0 when the service is registered, 1060 when it is not.
    public bool ServiceExists(string name) => Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "query " + name) == 0;

    public bool HasProgram(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(folder.Trim('"'), fileName)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters a path cannot hold is skipped.
            }
        }
        return false;
    }

    public int Run(string fileName, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException(fileName + " did not start.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// The setup asset of the latest PawnIO.Setup release, over HTTPS, into a new temporary file. Any failure gives null.
    public async Task<string?> DownloadSetupAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NotchTasksSensors");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var release = await http.GetAsync(ReleaseUrl, ct);
            release.EnsureSuccessStatusCode();
            var downloadUrl = SetupUrl(await release.Content.ReadAsStringAsync(ct));
            if (downloadUrl is null)
            {
                return null;
            }

            var path = Path.Combine(Path.GetTempPath(), "PawnIO_setup-" + Guid.NewGuid().ToString("N") + ".exe");
            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(path))
            {
                await source.CopyToAsync(target, ct);
            }
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException
                                  or UnauthorizedAccessException)
        {
            HelperLog.Once(e);
            return null;
        }
    }

    /// The download address of the setup asset in a release's JSON: the asset named exactly PawnIO_setup.exe, over HTTPS. Null when it is
    /// not there.
    internal static string? SetupUrl(string releaseJson)
    {
        using var document = JsonDocument.Parse(releaseJson);
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() == SetupAsset
                && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { } address
                && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                return address;
            }
        }
        return null;
    }

    public bool HasValidSignature(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = pathPointer,
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = WtdStateActionVerify,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    public void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that stays in the temporary folder is not worth failing the step.
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    /// WINTRUST_ACTION_GENERIC_VERIFY_V2: the Authenticode check of a file.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
