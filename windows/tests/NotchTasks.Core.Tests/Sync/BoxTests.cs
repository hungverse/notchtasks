using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core.Sync;
using Xunit;

namespace NotchTasks.Core.Tests.Sync;

/// Port of electron/test/box.test.js: the boxes, frames and pairing code must match the Mac byte for byte.
public sealed class BoxTests
{
    private static readonly byte[] Key = Box.FromB64Url(Crosscheck.Read("key.txt"));
    private const string SampleKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string SampleG = "group123456789012";
    private const string Sample = "{\"v\":1,\"g\":\"group123456789012\",\"k\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"n\":\"MacBook Pro\"}";

    private static byte[] Filled(int count, byte value) => Enumerable.Repeat(value, count).ToArray();

    private static string Hex(IEnumerable<byte[]> frames) => string.Join(",", frames.Select(f => Convert.ToHexString(f)));

    /// A pairing JSON object with the given fields changed (or removed) by the edit.
    private static string PairingJson(Action<JsonObject>? edit = null)
    {
        var o = new JsonObject { ["v"] = 1, ["g"] = SampleG, ["k"] = SampleKey, ["n"] = "x" };
        edit?.Invoke(o);
        return SyncJson.Write(o);
    }

    [Fact]
    public void Base64url_NoPadding_StrictDecoding()
    {
        Assert.Equal("-_8", Box.B64Url(new byte[] { 0xfb, 0xff }));
        Assert.Equal(new byte[] { 0xfb, 0xff }, Box.FromB64Url("-_8"));
        Assert.Equal(new byte[] { 0xfb, 0xff }, Box.FromB64Url("+/8")); // Swift's decoder takes both alphabets too
        Assert.Throws<FormatException>(() => Box.FromB64Url("a*b"));
        Assert.Throws<FormatException>(() => Box.FromB64Url("abcde"));
        Assert.Throws<FormatException>(() => Box.FromB64Url("ab=="));
    }

    [Fact]
    public void RandomGroup_SixteenByteGAndThirtyTwoByteK_UnpaddedAndFresh()
    {
        var a = Box.RandomGroup();
        Assert.Equal(16, Box.FromB64Url(a.G).Length);
        Assert.Equal(32, Box.FromB64Url(a.K).Length);
        Assert.DoesNotContain("=", a.G + a.K);
        Assert.NotEqual(Box.RandomGroup().G, a.G);
    }

    [Fact]
    public void SealAndOpen_RoundTrip_LayoutIsNonceCiphertextTag()
    {
        var obj = new JsonObject { ["a"] = 1, ["t"] = "Hùng ✓ \"q\"", ["arr"] = new JsonArray(1, 2) };
        var sealedBox = Box.Seal(obj, Key);
        Assert.Equal(SyncJson.Write(obj), SyncJson.Write(Box.Open(sealedBox, Key)));
        Assert.Equal(12 + Encoding.UTF8.GetByteCount(SyncJson.Write(obj)) + 16, Convert.FromBase64String(sealedBox).Length);
        Assert.NotEqual(Box.Seal(obj, Key), sealedBox); // a fresh nonce each time
    }

    [Fact]
    public void Tampered_Truncated_WrongKey_AndMalformedBoxes_DoNotOpen()
    {
        var b = Convert.FromBase64String(Box.Seal(new JsonObject { ["x"] = 1 }, Key));
        string Flip(int i)
        {
            var t = (byte[])b.Clone();
            t[i] ^= 1;
            return Convert.ToBase64String(t);
        }

        Assert.ThrowsAny<CryptographicException>(() => Box.Open(Flip(12), Key)); // ciphertext byte
        Assert.ThrowsAny<CryptographicException>(() => Box.Open(Flip(b.Length - 1), Key)); // tag byte
        Assert.ThrowsAny<CryptographicException>(() => Box.Open(Flip(0), Key)); // nonce byte
        Assert.ThrowsAny<CryptographicException>(() => Box.Open(Box.Seal(new JsonObject { ["x"] = 1 }, Key), Filled(32, 9))); // wrong key
        Assert.ThrowsAny<CryptographicException>(() => Box.Open(Convert.ToBase64String(b[..20]), Key)); // too short
        Assert.ThrowsAny<FormatException>(() => Box.Open("@@@@", Key)); // not base64
        Assert.Throws<ArgumentException>(() => Box.Seal(new JsonObject(), Filled(16, 0))); // key length
    }

    [Fact]
    public void SealBytesOpenBytes_RoundTripRawBuffers()
    {
        var raw = new byte[] { 0, 1, 2, 255 };
        Assert.Equal(raw, Box.OpenBytes(Box.SealBytes(raw, Key), Key));
    }

    [Fact]
    public void Frame_FourByteBigEndianLengthThenBytes()
    {
        var f = Box.Frame(Filled(300, 7));
        Assert.Equal(304, f.Length);
        Assert.Equal(300u, BinaryPrimitives.ReadUInt32BigEndian(f));
        Assert.Throws<ArgumentException>(() => Box.Frame(Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => Box.Frame(new byte[Box.MaxFrame + 1]));
    }

    [Fact]
    public void FrameReader_SplitAndMergedChunks_AndSealedBoxThroughFrame()
    {
        var bodies = new[] { Filled(300, 7), Encoding.UTF8.GetBytes("hello") };
        var stream = bodies.SelectMany(body => Box.Frame(body)).ToArray();

        var bytewise = new FrameReader();
        var got = new List<byte[]>();
        foreach (var b in stream)
        {
            got.AddRange(bytewise.Push(new[] { b }));
        }
        Assert.Equal(Hex(bodies), Hex(got));
        Assert.Equal(Hex(bodies), Hex(new FrameReader().Push(stream)));

        var r = new FrameReader();
        Assert.Empty(r.Push(stream[..2]));
        Assert.Equal(Hex(bodies), Hex(r.Push(stream[2..])));

        var sealedBytes = Convert.FromBase64String(Box.Seal(new JsonObject { ["ok"] = true }, Key));
        var payload = new FrameReader().Push(Box.Frame(sealedBytes))[0];
        Assert.Equal("{\"ok\":true}", SyncJson.Write(Box.Open(Convert.ToBase64String(payload), Key)));
    }

    [Fact]
    public void FrameReader_DropsZeroLengthOrOverFourMegabytes_ByThrowing()
    {
        static byte[] Head(uint n)
        {
            var h = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(h, n);
            return h;
        }

        Assert.Throws<InvalidDataException>(() => new FrameReader().Push(Head(0)));
        Assert.Throws<InvalidDataException>(() => new FrameReader().Push(Head(4u * 1024 * 1024 + 1)));
        Assert.Empty(new FrameReader().Push(Head(4u * 1024 * 1024))); // exactly 4 MB waits for its body
    }

    [Fact]
    public void ParsePairing_EveryCaseOfSelfTest()
    {
        var frag = Uri.EscapeDataString(Sample);
        var want = new PairingCode(1, SampleG, SampleKey, "MacBook Pro");
        Assert.Equal(want, Box.ParsePairing(Sample)); // bare text
        Assert.Equal(want, Box.ParsePairing("https://notchtasks.web.app/pair#" + frag)); // https URL
        Assert.Equal(want, Box.ParsePairing("notchtasks://pair#" + frag)); // notchtasks:// URL
        Assert.Null(Box.ParsePairing("https://example.com/pair#" + frag)); // foreign URL
        Assert.Null(Box.ParsePairing("otherapp://pair#" + frag)); // foreign scheme
        Assert.Null(Box.ParsePairing("notchtasks://pair#")); // empty fragment
        Assert.Null(Box.ParsePairing("https://notchtasks.web.app/pair#")); // empty fragment https
    }

    [Fact]
    public void ParsePairing_SmartQuotesWhitespaceAndRulesOnVGKN()
    {
        var want = new PairingCode(1, SampleG, SampleKey, "MacBook Pro");
        Assert.Equal(want, Box.ParsePairing("  " + Sample.Replace('"', '\u201C') + "\n")); // typed smart quotes
        Assert.Null(Box.ParsePairing(PairingJson(o => o["v"] = 2))); // v is 1
        Assert.Null(Box.ParsePairing(PairingJson(o => o["v"] = "1"))); // v is a number
        Assert.Null(Box.ParsePairing(PairingJson(o => o["g"] = new string('g', 15)))); // g of 15 characters
        Assert.Null(Box.ParsePairing(PairingJson(o => o["g"] = "group 123456789012"))); // g with a space
        Assert.Equal("nhómHùng123456789", Box.ParsePairing(PairingJson(o => o["g"] = "nhómHùng123456789"))!.G); // g may hold letters
        Assert.Null(Box.ParsePairing(PairingJson(o => o["k"] = new string('A', 42)))); // k of 31 bytes
        Assert.Null(Box.ParsePairing(PairingJson(o => o["k"] = SampleKey + "="))); // k with padding
        Assert.Null(Box.ParsePairing(PairingJson(o => o["k"] = SampleKey[..^1] + "*"))); // k with a bad character
        Assert.Null(Box.ParsePairing(PairingJson(o => o.Remove("n")))); // n is required
        foreach (var junk in new[] { "", "hello", "a://b", "[]", "{", "null" })
        {
            Assert.Null(Box.ParsePairing(junk));
        }
    }

    [Fact]
    public void ParsePairing_ReadsSwiftFixturesPairAndPairUrl()
    {
        var want = new PairingCode(1, SampleG, Crosscheck.Read("key.txt"), "Hùng's Mac");
        Assert.Equal(want, Box.ParsePairing(Crosscheck.Read("pair.txt")));
        Assert.Equal(want, Box.ParsePairing(Crosscheck.Read("pair-url.txt")));
    }

    [Fact]
    public void PairCodeAndPairUrl_ReproduceSwiftFixturesExactly()
    {
        var p = Box.ParsePairing(Crosscheck.Read("pair.txt"))!;
        Assert.Equal(Crosscheck.Read("pair.txt"), Box.PairCode(p.G, p.K, p.N));
        Assert.Equal(Crosscheck.Read("pair-url.txt"), Box.PairUrl(Crosscheck.Read("pair.txt")));
        Assert.Equal("{\"v\":1,\"g\":\"g\",\"k\":\"k\",\"n\":\"Mac 'Pro'\"}", Box.PairCode("g", "k", "Mac \"Pro\""));
        var code = Box.PairCode(SampleG, Crosscheck.Read("key.txt"), "Hùng \"Pro\" 100%");
        Assert.Equal(new PairingCode(1, SampleG, Crosscheck.Read("key.txt"), "Hùng 'Pro' 100%"), Box.ParsePairing(Box.PairUrl(code)));
    }

    [Fact]
    public void PairCode_PairUrl_EqualFixturesByteForByte()
    {
        var p = Box.ParsePairing(Crosscheck.Read("pair.txt"))!;
        Assert.Equal(Crosscheck.Read("pair.txt"), Box.PairCode(p.G, p.K, p.N));
        Assert.Equal(Crosscheck.Read("pair-url.txt"), Box.PairUrl(Crosscheck.Read("pair.txt")));
    }

    [Fact]
    public void ServiceName_RevealsNothingOfGroup()
    {
        Assert.Matches("^NotchTasks [0-9a-f]{12}$", Box.ServiceName(SampleG));
        Assert.NotEqual(Box.ServiceName("abc"), Box.ServiceName("abd"));
    }

    [Fact]
    public void ServiceName_EqualsSwiftFixture()
    {
        Assert.Equal(Crosscheck.Read("service.txt"), Box.ServiceName("group123456789012"));
    }

    [Fact]
    public void Open_SwiftSnapshotFixture_OpensToJsonWithMac()
    {
        Assert.Equal("Hùng\u2019s MacBook \"Pro\"", Box.Open(Crosscheck.Read("snapshot.box"), Key)["mac"]!.GetValue<string>());
    }
}
