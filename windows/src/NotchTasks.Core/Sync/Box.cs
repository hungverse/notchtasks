using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotchTasks.Core.Sync;

/// Sealed boxes, pairing and frames (docs/sync-v1.md), as the Mac (Sources/Sync.swift) and electron/src/core/box.js do them.
/// A box is base64 of nonce (12) | ciphertext | tag (16), AES-256-GCM.
public static partial class Box
{
    public const int MaxFrame = 4 * 1024 * 1024; // a frame longer than this is not ours: the connection is dropped
    public const string PairPrefix = "https://notchtasks.web.app/pair#";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// base64url without padding, the form the group id and key take in the pairing code.
    public static string B64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// Strict: base64 or base64url characters only, padding not allowed. Throws FormatException on anything else.
    public static byte[] FromB64Url(string s)
    {
        if (s.Length % 4 == 1 || !Base64UrlChars().IsMatch(s))
        {
            throw new FormatException("not base64url");
        }
        var standard = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard.PadRight(standard.Length + (4 - standard.Length % 4) % 4, '='));
    }

    /// A fresh group: g is 16 random bytes, k the 32-byte key that seals the boxes.
    public static (string G, string K) RandomGroup() =>
        (B64Url(RandomNumberGenerator.GetBytes(16)), B64Url(RandomNumberGenerator.GetBytes(KeyBytes)));

    /// JSON text of the object, sealed and base64 (the form the REST bodies and the frames carry).
    public static string Seal(JsonNode obj, byte[] key) =>
        Convert.ToBase64String(SealBytes(Encoding.UTF8.GetBytes(SyncJson.Write(obj)), key));

    /// Opens a base64 box to its JSON. Throws FormatException for bad base64 or bad JSON, CryptographicException when the tag does not match (wrong key, tampered or truncated box).
    public static JsonNode Open(string sealedBase64, byte[] key)
    {
        if (sealedBase64.Length % 4 != 0 || !StandardBase64().IsMatch(sealedBase64))
        {
            throw new FormatException("not a base64 box");
        }
        var json = Encoding.UTF8.GetString(OpenBytes(Convert.FromBase64String(sealedBase64), key));
        try
        {
            return JsonNode.Parse(json) ?? throw new FormatException("the box holds no object");
        }
        catch (JsonException e)
        {
            throw new FormatException("the box holds no JSON", e);
        }
    }

    /// nonce | AES-256-GCM ciphertext | tag, with a fresh random nonce.
    public static byte[] SealBytes(byte[] plain, byte[] key)
    {
        CheckKey(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, ciphertext, tag);
        var box = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(box, 0);
        ciphertext.CopyTo(box, NonceBytes);
        tag.CopyTo(box, NonceBytes + ciphertext.Length);
        return box;
    }

    /// Throws CryptographicException when the tag does not match (wrong key, tampered or truncated box).
    public static byte[] OpenBytes(ReadOnlySpan<byte> box, byte[] key)
    {
        CheckKey(key);
        if (box.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("box too short");
        }
        var ciphertext = box[NonceBytes..^TagBytes];
        var plain = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(box[..NonceBytes], ciphertext, box[^TagBytes..], plain);
        return plain;
    }

    /// 4-byte big-endian length, then the bytes (one box). Throws ArgumentException for an empty body or one over MaxFrame.
    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || body.Length > MaxFrame)
        {
            throw new ArgumentException($"frame length {body.Length} out of range", nameof(body));
        }
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// Port of Pairing.parse (iOS/Shared/Seal.swift) as box.js has it. Returns null for anything that is not a pairing.
    public static PairingCode? ParsePairing(string text)
    {
        var raw = text.Trim();
        if (raw.StartsWith("https://", StringComparison.Ordinal) || raw.StartsWith("notchtasks://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u))
            {
                return null;
            }
            if (raw.StartsWith("https://", StringComparison.Ordinal))
            {
                if (u.Host != "notchtasks.web.app" || u.Port != 443 || u.AbsolutePath != "/pair")
                {
                    return null;
                }
            }
            else if (u.Host != "pair" && u.AbsolutePath != "/pair")
            {
                return null;
            }
            var hash = raw.IndexOf('#');
            var frag = hash < 0 ? string.Empty : raw[(hash + 1)..];
            if (frag.Length == 0 || !TryDecodeUriComponent(frag, out raw))
            {
                return null;
            }
        }
        else if (raw.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        // typed codes get iOS smart quotes
        var json = raw.Replace('“', '"').Replace('”', '"').Replace('„', '"')
            .Replace('‘', '\'').Replace('’', '\'');
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (parsed is not JsonObject p)
        {
            return null;
        }

        if (!IsNumberOne(p["v"]) || StringField(p, "g") is not { } g || StringField(p, "k") is not { } k || StringField(p, "n") is not { } n)
        {
            return null;
        }
        if (CountRunes(g) < 16 || !AllGroupRunes(g))
        {
            return null;
        }
        try
        {
            if (FromB64Url(k).Length != KeyBytes)
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }
        return new PairingCode(1, g, k, n);
    }

    /// The QR text, as SyncGroup.code builds it (the name's quotes become apostrophes).
    public static string PairCode(string g, string k, string name) =>
        $"{{\"v\":1,\"g\":\"{g}\",\"k\":\"{k}\",\"n\":\"{name.Replace('"', '\'')}\"}}";

    /// As SyncHub.pairURL: the QR text inside the https fragment. Bytes URLComponents' .urlFragmentAllowed keeps stay as they are; the rest is %XX.
    public static string PairUrl(string code)
    {
        var sb = new StringBuilder(PairPrefix);
        foreach (var b in Encoding.UTF8.GetBytes(code))
        {
            if (b < 128 && FragmentKeep[b])
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    /// The Bonjour name: "NotchTasks " + the first 6 bytes of SHA-256(g), in hex. Reveals nothing about the group.
    public static string ServiceName(string g) =>
        "NotchTasks " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(g))[..6]).ToLowerInvariant();

    // Characters URLComponents' .urlFragmentAllowed leaves as they are (checked against Swift on the Mac). Everything else, '%' and '#' included, is encoded.
    private static readonly bool[] FragmentKeep = BuildFragmentKeep();

    private static bool[] BuildFragmentKeep()
    {
        var keep = new bool[128];
        foreach (var c in "!$&'()*+,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz~")
        {
            keep[c] = true;
        }
        return keep;
    }

    private static void CheckKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException("key must be 32 bytes", nameof(key));
        }
    }

    private static bool IsNumberOne(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && d == 1;

    private static string? StringField(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// JS [...g].length: counts code points, so an astral letter is one.
    private static int CountRunes(string s) => s.EnumerateRunes().Count();

    /// The JS regex [\p{L}\p{N}_-]: letters and numbers of any script, plus '_' and '-'.
    private static bool AllGroupRunes(string s)
    {
        foreach (var r in s.EnumerateRunes())
        {
            if (!(Rune.IsLetter(r) || Rune.IsNumber(r) || r.Value == '_' || r.Value == '-'))
            {
                return false;
            }
        }
        return true;
    }

    /// decodeURIComponent: every % must start two hex digits, and the bytes must be valid UTF-8. Returns false where JS throws.
    private static bool TryDecodeUriComponent(string s, out string result)
    {
        result = string.Empty;
        var bytes = new List<byte>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] != '%')
            {
                // A run of plain characters is encoded as a whole, so a surrogate pair stays one character.
                var run = i;
                while (i < s.Length && s[i] != '%')
                {
                    i++;
                }
                bytes.AddRange(Encoding.UTF8.GetBytes(s[run..i]));
                continue;
            }
            if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2]))
            {
                return false;
            }
            bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
            i += 3;
        }
        try
        {
            result = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9+/_-]*\z")]
    private static partial Regex Base64UrlChars();

    [GeneratedRegex(@"\A[A-Za-z0-9+/]*={0,2}\z")]
    private static partial Regex StandardBase64();
}
