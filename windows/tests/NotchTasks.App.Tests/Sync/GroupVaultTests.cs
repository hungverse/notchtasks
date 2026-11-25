using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core.Sync;
using NotchTasks.Services.Sync;
using Xunit;

namespace NotchTasks.AppTests;

/// The pairing vault: DPAPI on the Windows file, the Electron group.bin migrated, anything unreadable is unpaired.
/// Every test works in its own temp folder; the DPAPI calls are the test user's, on those temp files only.
public sealed class GroupVaultTests : IDisposable
{
    private static readonly SyncGroup Group = new("g-one", "k-one", "uid-one", "refresh-one");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nt-vault-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public GroupVaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private GroupVault Vault() => new(_dir, _log.Add);

    private string FilePath(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Vault_SaveLoad_RoundTrip()
    {
        Assert.Null(Vault().Save(Group));

        Assert.Equal(Group, Vault().Load());
        Assert.True(File.Exists(FilePath("pairing.bin")));
        Assert.Empty(_log);
    }

    [Fact]
    public void Vault_ElectronMigration_ReadsV10()
    {
        var aesKey = RandomNumberGenerator.GetBytes(32);
        WriteLocalState(Electron.Dpapi(aesKey));
        File.WriteAllBytes(FilePath("group.bin"), GroupVault.SealOsCrypt(Encoding.UTF8.GetBytes(Electron.GroupJson(Group)), aesKey));

        Assert.Equal(Group, Vault().Load());

        Assert.True(File.Exists(FilePath("pairing.bin")), "a migrated group is saved to pairing.bin at once");
        Assert.True(File.Exists(FilePath("group.bin")), "group.bin is left in place");
        Assert.Equal(Group, Vault().Load());
        Assert.Empty(_log);
    }

    [Fact]
    public void Vault_CorruptPairing_IsUnpairedAndKeepsFile()
    {
        var garbage = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(FilePath("pairing.bin"), garbage);

        var vault = Vault();
        Assert.Null(vault.Load());
        Assert.Null(vault.Load());

        Assert.Equal(garbage, File.ReadAllBytes(FilePath("pairing.bin")));
        Assert.Single(_log);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("not-json")]
    [InlineData("no-dpapi-prefix")]
    public void Vault_BadLocalState_IsUnpaired(string kind)
    {
        var aesKey = RandomNumberGenerator.GetBytes(32);
        switch (kind)
        {
            case "missing-key":
                File.WriteAllText(FilePath("Local State"), "{\"os_crypt\":{}}");
                break;
            case "not-json":
                File.WriteAllText(FilePath("Local State"), "{not json");
                break;
            default:
                var unprefixed = Encoding.ASCII.GetBytes("XXXXX")
                    .Concat(ProtectedData.Protect(aesKey, null, DataProtectionScope.CurrentUser))
                    .ToArray();
                WriteLocalState(Convert.ToBase64String(unprefixed));
                break;
        }
        File.WriteAllBytes(FilePath("group.bin"), GroupVault.SealOsCrypt(Encoding.UTF8.GetBytes(Electron.GroupJson(Group)), aesKey));

        Assert.Null(Vault().Load());
        Assert.False(File.Exists(FilePath("pairing.bin")));
    }

    [Fact]
    public void Vault_SaveNull_DeletesBoth()
    {
        Assert.Null(Vault().Save(Group));
        File.WriteAllBytes(FilePath("group.bin"), new byte[] { 1, 2, 3 });
        File.WriteAllText(FilePath("Local State"), "{}");

        Assert.Null(Vault().Save(null));

        Assert.False(File.Exists(FilePath("pairing.bin")));
        Assert.False(File.Exists(FilePath("group.bin")));
        Assert.True(File.Exists(FilePath("Local State")), "Local State belongs to the Electron app and is never touched");
        Assert.Null(Vault().Load());
    }

    [Fact]
    public void Vault_SaveNull_ThenElectronGroupIsNotBroughBack()
    {
        var aesKey = RandomNumberGenerator.GetBytes(32);
        WriteLocalState(Electron.Dpapi(aesKey));
        File.WriteAllBytes(FilePath("group.bin"), GroupVault.SealOsCrypt(Encoding.UTF8.GetBytes(Electron.GroupJson(Group)), aesKey));

        Assert.Null(Vault().Save(null));

        Assert.Null(Vault().Load());
    }

    private void WriteLocalState(string encryptedKeyBase64)
    {
        var state = new JsonObject { ["os_crypt"] = new JsonObject { ["encrypted_key"] = encryptedKeyBase64 } };
        File.WriteAllText(FilePath("Local State"), state.ToJsonString());
    }

    /// Builds the Electron fixtures: the DPAPI-wrapped AES key and the group JSON.
    private static class Electron
    {
        public static string Dpapi(byte[] aesKey) =>
            Convert.ToBase64String(Encoding.ASCII.GetBytes("DPAPI")
                .Concat(ProtectedData.Protect(aesKey, null, DataProtectionScope.CurrentUser))
                .ToArray());

        public static string GroupJson(SyncGroup g) =>
            new JsonObject { ["g"] = g.G, ["k"] = g.K, ["uid"] = g.Uid, ["refresh"] = g.Refresh }.ToJsonString();
    }
}
