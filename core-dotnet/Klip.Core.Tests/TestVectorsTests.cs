using System.Text;
using System.Text.Json;
using Klip.Core;

namespace Klip.Core.Tests;

/// <summary>Checks the C# core against protocol/test-vectors.json (reference: tools/gen_test_vectors.py).</summary>
public class TestVectorsTests
{
    private static readonly JsonElement V = Load();

    private static JsonElement Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "protocol", "test-vectors.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }
        throw new FileNotFoundException("protocol/test-vectors.json not found above " + AppContext.BaseDirectory);
    }

    private static string S(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    private static byte[] Hex(JsonElement e, string key) => Convert.FromHexString(S(e, key));
    private static byte[] B64(JsonElement e, string key) => Convert.FromBase64String(S(e, key));

    [Fact]
    public void Normalization()
    {
        foreach (var e in V.GetProperty("normalization").EnumerateArray())
        {
            var input = S(e, "input");
            Assert.Equal(S(e, "normalized"), Content.Normalize(input));
            Assert.Equal(S(e, "contentHash"), Content.Hash(input));
            Assert.Equal(e.GetProperty("isBlank").GetBoolean(), Content.IsBlank(input));
        }
    }

    [Fact]
    public void PairingDerivation()
    {
        var p = V.GetProperty("pairing");
        byte[] pubI = B64(p, "initiatorPublic"), pubR = B64(p, "responderPublic");
        byte[] nonceI = B64(p, "initiatorNonce"), nonceR = B64(p, "responderNonce");
        using var kpI = Pairing.KeyPairFromRaw(Hex(p, "initiatorPrivate"), pubI);
        using var kpR = Pairing.KeyPairFromRaw(Hex(p, "responderPrivate"), pubR);

        Assert.Equal(pubI, kpI.PublicKey);
        var sharedI = Pairing.SharedSecret(kpI, pubR);
        Assert.Equal(S(p, "sharedSecret"), Convert.ToHexStringLower(sharedI));
        Assert.Equal(S(p, "sharedSecret"), Convert.ToHexStringLower(Pairing.SharedSecret(kpR, pubI)));

        var secret = Pairing.DerivePairSecret(sharedI, nonceI, nonceR);
        Assert.Equal(S(p, "pairSecret"), Convert.ToHexStringLower(secret));
        var keys = PairKeys.Derive(secret);
        Assert.Equal(S(p, "macKey"), Convert.ToHexStringLower(keys.MacKey));
        Assert.Equal(S(p, "encKey"), Convert.ToHexStringLower(keys.EncKey));
        Assert.Equal(S(p, "pin"), Pairing.Pin(pubI, pubR, nonceI, nonceR));
    }

    [Fact]
    public void RandomSecretDerivation()
    {
        var r = V.GetProperty("randomSecret");
        var keys = PairKeys.Derive(Hex(r, "pairSecret"));
        Assert.Equal(S(r, "macKey"), Convert.ToHexStringLower(keys.MacKey));
        Assert.Equal(S(r, "encKey"), Convert.ToHexStringLower(keys.EncKey));
    }

    [Fact]
    public void InvalidPublicKeysAreRejected()
    {
        using var own = Pairing.GenerateKeyPair();
        foreach (var e in V.GetProperty("invalidPublicKeys").EnumerateArray())
        {
            var key = B64(e, "publicKey");
            Assert.False(Pairing.IsValidPublicKey(key), S(e, "reason"));
            Assert.Throws<KlipCryptoException>(() => Pairing.SharedSecret(own, key));
        }
    }

    [Fact]
    public void Signatures()
    {
        foreach (var e in V.GetProperty("signatures").EnumerateArray())
        {
            var body = B64(e, "body");
            string m = S(e, "method"), path = S(e, "path"), ts = S(e, "timestamp"), nonce = S(e, "nonce");
            Assert.Equal(S(e, "canonical"), RequestSigner.Canonical(m, path, ts, nonce, body));
            Assert.Equal(S(e, "signature"), RequestSigner.Signature(Hex(e, "macKey"), m, path, ts, nonce, body));
        }
    }

    [Fact]
    public void AesGcm()
    {
        foreach (var e in V.GetProperty("aesGcm").EnumerateArray())
        {
            byte[] key = Hex(e, "encKey"), iv = B64(e, "iv");
            var aad = Encoding.UTF8.GetBytes(S(e, "aad"));
            var pt = Encoding.UTF8.GetBytes(S(e, "plaintext"));
            Assert.Equal(S(e, "ciphertext"), Convert.ToBase64String(KlipCrypto.AesGcmEncrypt(key, iv, aad, pt)));
            Assert.Equal(pt, KlipCrypto.AesGcmDecrypt(key, iv, aad, B64(e, "ciphertext")));
        }
    }

    [Fact]
    public void EnvelopeOpen()
    {
        var env = V.GetProperty("envelope");
        var key = Hex(env, "encKey");
        var valid = env.GetProperty("valid");
        var payload = EnvelopeCodec.Open(key, EnvelopeCodec.Decode(valid.GetProperty("envelope").GetRawText()));
        var expected = valid.GetProperty("payload");
        Assert.Equal(S(expected, "content"), payload.Content);
        Assert.Equal(S(expected, "contentHash"), payload.ContentHash);
        Assert.Equal(S(expected, "originDeviceName"), payload.OriginDeviceName);
        Assert.Equal(expected.GetProperty("isTest").GetBoolean(), payload.IsTest);

        foreach (var bad in env.GetProperty("mustReject").EnumerateArray())
        {
            var reason = S(bad, "reason");
            var ex = Record.Exception(() => EnvelopeCodec.Open(key, EnvelopeCodec.Decode(bad.GetProperty("envelope").GetRawText())));
            Assert.True(ex is EnvelopeException, $"must reject: {reason}");
        }
    }

    [Fact]
    public void EnvelopeRoundTrip()
    {
        var keys = PairKeys.Derive(PairKeys.NewRandomSecret());
        var sealedEnv = EnvelopeCodec.Seal(keys.EncKey, "dev-a", ClipboardPayload.Text("hola\r\nmundo", "PC", isTest: true));
        var opened = EnvelopeCodec.Open(keys.EncKey, EnvelopeCodec.Decode(EnvelopeCodec.Encode(sealedEnv)));
        Assert.Equal("hola\r\nmundo", opened.Content);
        Assert.True(opened.IsTest);
    }
}
