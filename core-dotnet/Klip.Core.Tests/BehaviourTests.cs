using System.Text;
using Klip.Core;

namespace Klip.Core.Tests;

public class RequestVerifierTests
{
    private readonly byte[] _macKey = PairKeys.Derive(Enumerable.Repeat((byte)7, 32).ToArray()).MacKey;
    private long _now = 1_763_930_000_000;
    private readonly RequestVerifier _verifier;
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"a":1}""");

    public RequestVerifierTests()
    {
        _verifier = new RequestVerifier(id => id == "dev-a" ? _macKey : null, () => _now);
    }

    private IReadOnlyDictionary<string, string> Signed(long? ts = null, string device = "dev-a", byte[]? key = null) =>
        RequestSigner.Sign(key ?? _macKey, device, "POST", "/clipboard", Body, nowMs: ts ?? _now).ToDictionary();

    private VerifyResult Verify(IReadOnlyDictionary<string, string> h, string method = "POST", string path = "/clipboard", byte[]? body = null) =>
        _verifier.Verify(h.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)), method, path, body ?? Body);

    [Fact]
    public void AcceptsValidRequestOnce()
    {
        var h = Signed();
        Assert.Equal(VerifyResult.Ok, Verify(h));
        Assert.Equal(VerifyResult.Replay, Verify(h));
    }

    [Fact]
    public void HeaderNamesAreCaseInsensitive()
    {
        var h = Signed().ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
        Assert.Equal(VerifyResult.Ok, Verify(h));
    }

    [Fact]
    public void RejectsTamperedBodyPathOrMethod()
    {
        Assert.Equal(VerifyResult.BadSignature, Verify(Signed(), body: "x"u8.ToArray()));
        Assert.Equal(VerifyResult.BadSignature, Verify(Signed(), path: "/unpair"));
        Assert.Equal(VerifyResult.BadSignature, Verify(Signed(), method: "GET"));
    }

    [Fact]
    public void RejectsWrongKeyUnknownDeviceAndClockSkew()
    {
        Assert.Equal(VerifyResult.BadSignature, Verify(Signed(key: new byte[32])));
        Assert.Equal(VerifyResult.UnknownDevice, Verify(Signed(device: "dev-x")));
        Assert.Equal(VerifyResult.Expired, Verify(Signed(ts: _now - 30_001)));
        Assert.Equal(VerifyResult.Expired, Verify(Signed(ts: _now + 30_001)));
        Assert.Equal(VerifyResult.Ok, Verify(Signed(ts: _now - 30_000)));
    }

    [Fact]
    public void RejectsMalformedHeaders()
    {
        var h = new Dictionary<string, string>(Signed());
        h.Remove(Protocol.HeaderNonce);
        Assert.Equal(VerifyResult.Malformed, Verify(h));
        var bad = new Dictionary<string, string>(Signed()) { [Protocol.HeaderTimestamp] = "abc" };
        Assert.Equal(VerifyResult.Malformed, Verify(bad));
    }

    [Fact]
    public void NonceCanBeReusedAfterTtl()
    {
        var nonce = Convert.ToBase64String(KlipCrypto.RandomBytes(16));
        var first = RequestSigner.Sign(_macKey, "dev-a", "GET", "/ping", [], nowMs: _now, nonce: nonce).ToDictionary();
        Assert.Equal(VerifyResult.Ok, Verify(first, "GET", "/ping", []));
        _now += Protocol.NonceTtlMs + 1;
        var again = RequestSigner.Sign(_macKey, "dev-a", "GET", "/ping", [], nowMs: _now, nonce: nonce).ToDictionary();
        Assert.Equal(VerifyResult.Ok, Verify(again, "GET", "/ping", []));
    }

    [Fact]
    public void HttpStatusMapping()
    {
        Assert.Equal(200, VerifyResult.Ok.HttpStatus());
        Assert.Equal(400, VerifyResult.Malformed.HttpStatus());
        Assert.Equal(403, VerifyResult.UnknownDevice.HttpStatus());
        Assert.Equal(401, VerifyResult.Replay.HttpStatus());
    }
}

public class SyncStateTests
{
    private readonly SyncState _state = new("local");

    [Fact]
    public void OutgoingRulesInOrder()
    {
        Assert.Equal(OutgoingDecision.SkipDisabled, _state.EvaluateOutgoing("x", isSensitive: true, syncEnabled: false));
        Assert.Equal(OutgoingDecision.SkipSensitive, _state.EvaluateOutgoing("x", isSensitive: true));
        Assert.Equal(OutgoingDecision.SkipBlank, _state.EvaluateOutgoing(" \n\t"));
        Assert.Equal(OutgoingDecision.Send, _state.EvaluateOutgoing("hola"));
        _state.MarkSent(Content.Hash("hola"));
        Assert.Equal(OutgoingDecision.SkipAlreadySent, _state.EvaluateOutgoing("hola"));
        _state.MarkSent(Content.Hash("café\nx"));
        Assert.Equal(OutgoingDecision.SkipAlreadySent, _state.EvaluateOutgoing("café\r\nx"));
        Assert.Equal(OutgoingDecision.Send, _state.EvaluateOutgoing("hola", isTest: true));
    }

    [Fact]
    public void ReceivedContentIsNotSentBackEvenWithCrlf()
    {
        Assert.Equal(IncomingDecision.Accept, _state.AcceptIncoming("pc", "m1", Content.Hash("a\nb")));
        Assert.Equal(OutgoingDecision.SkipJustReceived, _state.EvaluateOutgoing("a\r\nb"));
    }

    [Fact]
    public void IncomingRules()
    {
        Assert.Equal(IncomingDecision.IgnoreSelf, _state.AcceptIncoming("local", "m1", "h"));
        Assert.Equal(IncomingDecision.Accept, _state.AcceptIncoming("pc", "m1", "h"));
        Assert.Equal(IncomingDecision.IgnoreDuplicate, _state.AcceptIncoming("pc", "m1", "h"));
    }

    [Fact]
    public void ProcessedIdsAreBounded()
    {
        for (var i = 0; i <= Protocol.ProcessedIdsCapacity; i++) _state.AcceptIncoming("pc", $"m{i}", "h");
        Assert.Equal(Protocol.ProcessedIdsCapacity, _state.Snapshot().ProcessedMessageIds!.Count);
        Assert.Equal(IncomingDecision.Accept, _state.AcceptIncoming("pc", "m0", "h"));
    }

    [Fact]
    public void SnapshotRoundTrip()
    {
        _state.MarkSent("s");
        _state.AcceptIncoming("pc", "m1", "r");
        var snap = _state.Snapshot();
        var restored = new SyncState("local", snap).Snapshot();
        Assert.Equal(snap.LastSentHash, restored.LastSentHash);
        Assert.Equal(snap.LastReceivedHash, restored.LastReceivedHash);
        Assert.Equal(snap.ProcessedMessageIds, restored.ProcessedMessageIds);
    }
}
