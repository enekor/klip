using System.Net;
using System.Net.Sockets;
using Klip.Core;
using Klip.Node;

namespace Klip.Node.Tests;

/// <summary>PIN pairing, group mesh and radar between real nodes on localhost.</summary>
public sealed class PairingTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("klip-pair").FullName;
    private readonly List<SyncNode> _nodes = [];
    private readonly Dictionary<SyncNode, MemoryClipboard> _clips = [];

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private async Task<SyncNode> NewNode(string name, bool autoAccept = true)
    {
        var store = new ConfigStore(Path.Combine(_dir, name + ".json"));
        var cfg = store.LoadOrCreate();
        cfg.DeviceName = name;
        cfg.HttpPort = FreeTcpPort();
        store.Save(cfg);
        var clip = new MemoryClipboard();
        var node = new SyncNode(store, new PlainSecretProtector(), clip) { PairingOpen = true };
        if (autoAccept) node.PairingRequested += p => node.RespondToPairing(p.SessionId, true);
        await node.StartAsync();
        _nodes.Add(node);
        _clips[node] = clip;
        return node;
    }

    private static async Task<PairingOutcome> Pair(SyncNode initiator, SyncNode responder)
    {
        var (pairing, error) = await initiator.StartPairingAsync("127.0.0.1", responder.HttpPort);
        Assert.True(pairing is not null, error);
        return await initiator.ConfirmPairingAsync(pairing);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }

    private static bool Knows(SyncNode a, SyncNode b) => a.Peers.Any(p => p.DeviceId == b.DeviceId);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task PinPairingCreatesWorkingPairOnBothSides()
    {
        var a = await NewNode("A");
        var b = await NewNode("B", autoAccept: false);
        string? responderPin = null;
        b.PairingRequested += p => { responderPin = p.Pin; b.RespondToPairing(p.SessionId, true); };

        var (pairing, error) = await a.StartPairingAsync("127.0.0.1", b.HttpPort);
        Assert.NotNull(pairing);
        Assert.Null(error);
        Assert.Equal("B", pairing.DeviceName);
        Assert.Matches("^[0-9]{6}$", pairing.Pin);
        Assert.Equal(responderPin, pairing.Pin);

        Assert.Equal(PairingStatus.Accepted, (await a.ConfirmPairingAsync(pairing)).Status);
        Assert.True(Knows(a, b) && Knows(b, a));

        Assert.True((await a.SendClipboardAsync("hola B")).AnyOk);
        Assert.True((await b.SendClipboardAsync("hola A")).AnyOk);
        Assert.Equal(["hola B"], _clips[b].Writes);
        Assert.Equal(["hola A"], _clips[a].Writes);
    }

    [Fact]
    public async Task ResponderCanDecideAfterConfirmArrives()
    {
        var a = await NewNode("A");
        var b = await NewNode("B", autoAccept: false);
        b.PairingRequested += p => _ = Task.Delay(700).ContinueWith(_ => b.RespondToPairing(p.SessionId, true));
        Assert.Equal(PairingStatus.Accepted, (await Pair(a, b)).Status);
        Assert.True(Knows(b, a));
    }

    [Fact]
    public async Task RejectionLeavesNoPair()
    {
        var a = await NewNode("A");
        var b = await NewNode("B", autoAccept: false);
        var finished = new List<PairingStatus>();
        b.IncomingPairingFinished += (_, s, _) => finished.Add(s);
        b.PairingRequested += p => b.RespondToPairing(p.SessionId, false);
        Assert.Equal(PairingStatus.Rejected, (await Pair(a, b)).Status);
        Assert.False(Knows(a, b) || Knows(b, a));
        Assert.Equal([PairingStatus.Rejected], finished);
    }

    [Fact]
    public async Task InitiatorCancelClosesResponderSession()
    {
        var a = await NewNode("A");
        var b = await NewNode("B", autoAccept: false);
        var finished = new TaskCompletionSource<PairingStatus>();
        b.IncomingPairingFinished += (_, s, _) => finished.TrySetResult(s);
        var (pairing, _) = await a.StartPairingAsync("127.0.0.1", b.HttpPort);
        await a.CancelPairingAsync(pairing!);
        Assert.Equal(PairingStatus.Cancelled, await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Knows(b, a));
    }

    [Fact]
    public async Task ClosedWindowAndBusyAreReported()
    {
        var a = await NewNode("A");
        var b = await NewNode("B", autoAccept: false);
        var c = await NewNode("C");

        b.PairingOpen = false;
        var (p1, e1) = await a.StartPairingAsync("127.0.0.1", b.HttpPort);
        Assert.Null(p1);
        Assert.Contains("abierta la ventana", e1);

        b.PairingOpen = true;
        var (p2, _) = await a.StartPairingAsync("127.0.0.1", b.HttpPort);
        Assert.NotNull(p2);
        var (p3, e3) = await c.StartPairingAsync("127.0.0.1", b.HttpPort);
        Assert.Null(p3);
        Assert.Contains("otra vinculación", e3);
    }

    [Fact]
    public async Task ForgedConfirmWithoutSessionIsRejected()
    {
        var a = await NewNode("A");
        var b = await NewNode("B");
        using var http = new HttpClient();
        var keys = PairKeys.Derive(PairKeys.NewRandomSecret());
        var headers = RequestSigner.Sign(keys.MacKey, a.DeviceId, "POST", "/pair/confirm", "{}"u8.ToArray()).ToDictionary();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{b.HttpPort}/pair/confirm") { Content = new ByteArrayContent("{}"u8.ToArray()) };
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(req)).StatusCode);
        Assert.False(Knows(b, a));
    }

    [Fact]
    public async Task PairRequestsAreRateLimited()
    {
        var a = await NewNode("A");
        var b = await NewNode("B", autoAccept: false);
        string? last = null;
        for (var i = 0; i < Protocol.PairRequestsPerMinute + 1; i++) (_, last) = await a.StartPairingAsync("127.0.0.1", b.HttpPort);
        Assert.Contains("Demasiados intentos", last);
    }

    [Fact]
    public async Task NewMemberIsIntroducedToTheWholeGroup()
    {
        var a = await NewNode("A");
        var b = await NewNode("B");
        var c = await NewNode("C");
        Assert.True((await Pair(a, b)).Ok);
        Assert.True((await Pair(c, a)).Ok); // C joins through A

        await WaitUntil(() => Knows(b, c) && Knows(c, b), "B and C introduced");
        Assert.True((await c.SendClipboardAsync("de C a todos")).AnyOk);
        await WaitUntil(() => _clips[b].Writes.Contains("de C a todos") && _clips[a].Writes.Contains("de C a todos"), "delivery to A and B");
    }

    [Fact]
    public async Task IntroductionIsRetriedWhenReceiverComesBack()
    {
        var a = await NewNode("A");
        var b = await NewNode("B");
        var c = await NewNode("C");
        Assert.True((await Pair(a, b)).Ok);
        await b.SetSyncEnabledAsync(false); // B offline

        Assert.True((await Pair(c, a)).Ok);
        await WaitUntil(() => Knows(c, b), "C learns about B");
        await Task.Delay(300);
        Assert.False(Knows(b, c));

        await b.SetSyncEnabledAsync(true);
        Assert.True((await b.PingAsync(a.DeviceId)).Ok); // contact with A triggers the pending delivery
        await WaitUntil(() => Knows(b, c), "pending introduction delivered to B");
        Assert.True((await c.SendClipboardAsync("ya me conoces")).AnyOk);
        await WaitUntil(() => _clips[b].Writes.Contains("ya me conoces"), "C -> B");
    }

    [Fact]
    public async Task UnpairAndExpel()
    {
        var a = await NewNode("A");
        var b = await NewNode("B");
        var c = await NewNode("C");
        Assert.True((await Pair(a, b)).Ok);
        Assert.True((await Pair(a, c)).Ok);
        await WaitUntil(() => Knows(b, c) && Knows(c, b), "mesh");

        // Expel reaches every member of the expeller's group, including the expelled device.
        await a.ExpelAsync(c.DeviceId);
        Assert.False(Knows(a, c));
        Assert.False(Knows(b, c));
        Assert.Empty(c.Peers);
        Assert.True(Knows(a, b) && Knows(b, a), "expel does not affect the others");

        // Unpair only breaks one pair, on both sides.
        await a.UnpairAsync(b.DeviceId);
        Assert.False(Knows(a, b));
        Assert.False(Knows(b, a));
    }

    [Fact]
    public async Task SecretVersionResolvesConcurrentIntroductions()
    {
        // Same pair introduced twice with different versions must end with the same secret on both sides.
        var a = await NewNode("A");
        var b = await NewNode("B");
        var c = await NewNode("C");
        var d = await NewNode("D");
        Assert.True((await Pair(a, b)).Ok);
        Assert.True((await Pair(a, c)).Ok);
        Assert.True((await Pair(d, b)).Ok);
        Assert.True((await Pair(d, c)).Ok);
        await WaitUntil(() => new[] { a, b, c, d }.All(n => n.Peers.Count == 3), "full mesh of 4");
        await Task.Delay(1000); // let late introductions (secret replacements) settle
        foreach (var x in new[] { a, b, c, d })
        {
            var outcome = await x.SendTestAsync();
            Assert.True(outcome.Results.All(r => r.Ok), $"{x.DeviceName}: " + string.Join(", ", outcome.Results.Select(r => $"{r.Name}={r.Error}")));
        }
    }

    [Fact]
    public async Task RadarFindsDevicesAndFixesAddressOfKnownOnes()
    {
        var a = await NewNode("A");
        var b = await NewNode("B");
        int udpA = FreeUdpPort(), udpB = FreeUdpPort();
        var loopback = () => new[] { IPAddress.Loopback };
        a.StartDiscovery(new DiscoveryOptions { ListenPort = udpA, SendPort = udpB, Targets = loopback, IntervalMs = 100 });
        b.StartDiscovery(new DiscoveryOptions { ListenPort = udpB, SendPort = udpA, Targets = loopback, IntervalMs = 100 });

        await WaitUntil(() => a.Discovered.Any(d => d.DeviceId == b.DeviceId && d.PairingOpen && d.HttpPort == b.HttpPort), "A sees B");
        var seen = a.Discovered.Single(d => d.DeviceId == b.DeviceId);
        Assert.True((await a.ConfirmPairingAsync((await a.StartPairingAsync(seen.Host, seen.HttpPort)).Pairing!)).Ok);
        await WaitUntil(() => a.Discovered.All(d => d.DeviceId != b.DeviceId), "paired device leaves the radar list");

        // A has a stale port for B: the next beacon + signed ping fixes it.
        a.AddOrUpdatePeer(b.DeviceId, "B", "127.0.0.1", 1, PairKeysSecretOf(b, a));
        await WaitUntil(() => a.Peers.Single(p => p.DeviceId == b.DeviceId).Port == b.HttpPort, "port corrected from beacon");

        a.StopDiscovery();
        Assert.Empty(a.Discovered);
    }

    /// <summary>Reads the pair secret B holds for A from B's config file (plain protector in tests).</summary>
    private byte[] PairKeysSecretOf(SyncNode holder, SyncNode peer)
    {
        var cfg = new ConfigStore(Path.Combine(_dir, holder.DeviceName + ".json")).LoadOrCreate();
        return Convert.FromBase64String(cfg.Peers.Single(p => p.DeviceId == peer.DeviceId).ProtectedSecret!);
    }
}
