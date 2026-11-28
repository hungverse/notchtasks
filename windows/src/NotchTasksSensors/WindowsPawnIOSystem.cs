using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The real machine for the PawnIO step: the service check (sc.exe), the winget package folder, Process, the GitHub release, the locked
/// download and WinVerifyTrust. It never searches PATH or the user's own folders for a program it runs.
internal sealed class WindowsPawnIOSystem : IPawnIOSystem
{
    private const string ReleaseUrl = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
    private const string SetupAsset = "PawnIO_setup.exe";
    private const string MicrosoftOrganization = "O=Microsoft Corporation";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// sc query: exit 0 when the service is registered, 1060 when it is not.
    public bool ServiceExists(string name) => Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "query " + name) == 0;

    /// The winget of the Windows package in %ProgramFiles%\WindowsApps (admin-only), the newest version. Null when that folder has none.
    public string? FindWinget()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            var folders = Directory.EnumerateDirectories(root, "Microsoft.DesktopAppInstaller_*")
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .ToList();
            var newest = PawnIOInstaller.NewestWingetFolder(folders);
            if (newest is null)
            {
                return null;
            }
            var exe = Path.Combine(root, newest, "winget.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Valid Authenticode, and the signer is Microsoft Corporation (its certificate's O field).
    public bool IsMicrosoftSigned(string path) => HasValidSignature(path) && SignerIsMicrosoft(path);

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

    /// The setup asset of the latest PawnIO.Setup release, over HTTPS, written to a new temporary file and held locked. Any failure gives null.
    public async Task<IPawnIOSetupFile?> DownloadSetupAsync(CancellationToken ct)
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
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            return await LockedSetupFile.CreateAsync(path, source, ct);
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

    /// True when the signer's certificate subject has O=Microsoft Corporation. Unsigned files give false.
    private static bool SignerIsMicrosoft(string path)
    {
        try
        {
            // The signer certificate of an Authenticode signature is read from the file itself; X509CertificateLoader has no such entry point.
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return signer.Subject.Split(',').Select(part => part.Trim()).Contains(MicrosoftOrganization, StringComparer.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
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
