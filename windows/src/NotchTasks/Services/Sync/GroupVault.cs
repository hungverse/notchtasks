using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Sync;

namespace NotchTasks.Services.Sync;

/// The pairing group on this PC (P4-R4). pairing.bin is the group protected with DPAPI for this user. When there is none,
/// the Electron app's group.bin is read (Chromium OSCrypt: its AES key is in Local State, DPAPI-wrapped) and moved to pairing.bin.
/// Anything unreadable is unpaired: logged once per instance, never thrown, the file is left as it is.
public sealed class GroupVault : IGroupVault
{
    private const string PairingFile = "pairing.bin";
    private const string ElectronGroupFile = "group.bin";
    private const string ElectronStateFile = "Local State";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NotchTasks pairing");
    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");
    private static readonly byte[] V10Prefix = Encoding.ASCII.GetBytes("v10");

    private readonly string _dataDir;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private bool _logged;

    public GroupVault(string dataDir, Action<string> log)
    {
        _dataDir = dataDir;
        _log = log;
    }

    private string PairingPath => Path.Combine(_dataDir, PairingFile);

    private string ElectronGroupPath => Path.Combine(_dataDir, ElectronGroupFile);

    private string ElectronStatePath => Path.Combine(_dataDir, ElectronStateFile);

    public SyncGroup? Load()
    {
        lock (_gate)
        {
            if (File.Exists(PairingPath))
            {
                return Unreadable(() => ReadPairing(File.ReadAllBytes(PairingPath)), PairingFile);
            }
            if (!File.Exists(ElectronGroupPath))
            {
                return null;
            }
            var migrated = Unreadable(MigrateElectronGroup, ElectronGroupFile);
            if (migrated is not null)
            {
                SavePairing(migrated);
            }
            return migrated;
        }
    }

    public string? Save(SyncGroup? group)
    {
        lock (_gate)
        {
            return group is null ? Forget() : SavePairing(group);
        }
    }

    /// The DPAPI bytes of a group as pairing.bin holds them (the test fixtures are built with it).
    internal static byte[] ProtectPairing(SyncGroup g) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(GroupJson(g)), Entropy, DataProtectionScope.CurrentUser);

    /// The Chromium OSCrypt v10 layout: "v10" | 12-byte nonce | ciphertext | 16-byte tag, AES-256-GCM with no associated data.
    internal static byte[] SealOsCrypt(byte[] plain, byte[] aesKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(aesKey, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        return [.. V10Prefix, .. nonce, .. cipher, .. tag];
    }

    /// Opens a v10 blob with the AES key. Throws when the blob is not v10 or does not authenticate.
    private static byte[] OpenOsCrypt(byte[] data, byte[] aesKey)
    {
        if (data.Length < V10Prefix.Length + NonceSize + TagSize || !data.AsSpan(0, V10Prefix.Length).SequenceEqual(V10Prefix))
        {
            throw new FormatException("group.bin is not a v10 blob");
        }
        var nonce = data.AsSpan(V10Prefix.Length, NonceSize);
        var cipher = data.AsSpan(V10Prefix.Length + NonceSize, data.Length - V10Prefix.Length - NonceSize - TagSize);
        var tag = data.AsSpan(data.Length - TagSize, TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(aesKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private SyncGroup MigrateElectronGroup()
    {
        var state = JsonNode.Parse(File.ReadAllText(ElectronStatePath, Encoding.UTF8));
        var encrypted = state?["os_crypt"]?["encrypted_key"]?.GetValue<string>()
            ?? throw new FormatException("Local State has no os_crypt key");
        var blob = Convert.FromBase64String(encrypted);
        if (blob.Length <= DpapiPrefix.Length || !blob.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
        {
            throw new FormatException("the os_crypt key has no DPAPI prefix");
        }
        var aesKey = ProtectedData.Unprotect(blob[DpapiPrefix.Length..], null, DataProtectionScope.CurrentUser);
        if (aesKey.Length != 32)
        {
            throw new CryptographicException("the os_crypt key is not 32 bytes");
        }
        var plain = OpenOsCrypt(File.ReadAllBytes(ElectronGroupPath), aesKey);
        return ParseGroup(Encoding.UTF8.GetString(plain));
    }

    private static SyncGroup ReadPairing(byte[] protectedBytes)
    {
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return ParseGroup(Encoding.UTF8.GetString(plain));
    }

    private static SyncGroup ParseGroup(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("the group is not an object");
        string Field(string name) =>
            o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
                ? s
                : throw new FormatException($"the group has no {name}");
        return new SyncGroup(Field("g"), Field("k"), Field("uid"), Field("refresh"));
    }

    private static string GroupJson(SyncGroup g) =>
        SyncJson.Write(new JsonObject { ["g"] = g.G, ["k"] = g.K, ["uid"] = g.Uid, ["refresh"] = g.Refresh });

    /// Runs a read; a failure is logged (once per instance) and the group is unpaired.
    private SyncGroup? Unreadable(Func<SyncGroup> read, string file)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            if (!_logged)
            {
                _logged = true;
                _log($"vault: {file} cannot be read ({e.GetType().Name}); the PC is unpaired");
            }
            return null;
        }
    }

    private string? SavePairing(SyncGroup group)
    {
        try
        {
            AtomicFile.WriteAllBytes(PairingPath, ProtectPairing(group));
            return null;
        }
        catch (Exception e)
        {
            _log($"vault: pairing not saved ({e.GetType().Name})");
            return L10n.L("Couldn't save the pairing on this computer.");
        }
    }

    /// Save(null): the group is gone on the server, so both files go, the Electron one too, or an old build would bring it back.
    private string? Forget()
    {
        string? problem = null;
        foreach (var path in new[] { PairingPath, ElectronGroupPath })
        {
            // A file that is not there (or a folder that is not there) is already forgotten.
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"vault: {Path.GetFileName(path)} not deleted ({e.GetType().Name})");
                problem = L10n.L("Couldn't forget the pairing on this computer.");
            }
        }
        return problem;
    }
}
