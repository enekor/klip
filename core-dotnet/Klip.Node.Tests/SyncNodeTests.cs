using System.Net;
using System.Net.Sockets;
using System.Text;
using Klip.Core;
using Klip.Node;

namespace Klip.Node.Tests;

internal sealed class MemoryClipboard : IClipboardWriter
{
    public List<string> Writes { get; } = [];
    public List<(byte[] Data, string Mime)> Images { get; } = [];
    public void WriteText(string text) { lock (Writes) Writes.Add(text); }
    public void WriteImage(byte[] data, string mimeType) { lock (Images) Images.Add((data, mimeType)); }
}

/// <summary>Two real nodes talking over HTTP on localhost.</summary>
public sealed class SyncNodeTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("klip-test").FullName;
    private readonly byte[] _secret = PairKeys.NewRandomSecret();
    private readonly MemoryClipboard _clipA = new();
    private readonly MemoryClipboard _clipB = new();
    private SyncNode _a = null!;
    private SyncNode _b = null!;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private SyncNode NewNode(string name, MemoryClipboard clip)
    {
        var store = new ConfigStore(Path.Combine(_dir, name + ".json"));
        var cfg = store.LoadOrCreate();
        cfg.DeviceName = name;
        cfg.HttpPort = FreePort();
        store.Save(cfg);
        return new SyncNode(store, new PlainSecretProtector(), clip);
    }

    public async Task InitializeAsync()
    {
        _a = NewNode("A", _clipA);
        _b = NewNode("B", _clipB);
        _a.AddOrUpdatePeer(_b.DeviceId, "B", "127.0.0.1", _b.HttpPort, _secret);
        _b.AddOrUpdatePeer(_a.DeviceId, "A", "127.0.0.1", _a.HttpPort, _secret);
        await _a.StartAsync();
        await _b.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task DeliversTextAndDoesNotBounceBack()
    {
        var outcome = await _a.SendClipboardAsync("hola\nmundo");
        Assert.Equal(OutgoingDecision.Send, outcome.Decision);
        Assert.True(outcome.AnyOk);
        Assert.Equal(["hola\nmundo"], _clipB.Writes);

        // B's clipboard monitor would now see the same text (maybe with CRLF): it must not send it back.
        var back = await _b.SendClipboardAsync("hola\r\nmundo");
        Assert.Equal(OutgoingDecision.SkipJustReceived, back.Decision);
        Assert.Empty(_clipA.Writes);

        // A copying the same text again is not resent.
        Assert.Equal(OutgoingDecision.SkipAlreadySent, (await _a.SendClipboardAsync("hola\nmundo")).Decision);
    }

    [Fact]
    public async Task TestMessagesRaiseEventAndCanRepeat()
    {
        var received = new List<string>();
        _b.TestReceived += received.Add;
        var items = new List<ReceivedItem>();
        _b.Received += items.Add;
        Assert.True((await _a.SendTestAsync()).AnyOk);
        Assert.True((await _a.SendTestAsync()).AnyOk);
        Assert.Equal(["A", "A"], received);

        // The UI can show exactly what arrived.
        Assert.Equal(2, items.Count);
        Assert.True(_b.LastReceived!.IsTest);
        Assert.Equal("A", _b.LastReceived.From);
        Assert.StartsWith("Prueba de conexión desde A - ", _b.LastReceived.Content);
        Assert.Equal(_clipB.Writes.Last(), _b.LastReceived.Content);

        Assert.True((await _a.SendClipboardAsync("línea 1\nlínea 2")).AnyOk);
        Assert.False(_b.LastReceived.IsTest);
        Assert.Equal("línea 1 ⏎ línea 2", _b.LastReceived.Preview());
    }

    [Fact]
    public async Task ImagesAreDeliveredAndDoNotBounce()
    {
        var png = new byte[300_000];
        Random.Shared.NextBytes(png);
        var outcome = await _a.SendImageAsync(png, "image/png");
        Assert.True(outcome.AnyOk);
        var (data, mime) = Assert.Single(_clipB.Images);
        Assert.Equal(png, data);
        Assert.Equal("image/png", mime);
        Assert.Empty(_clipB.Writes);

        Assert.True(_b.LastReceived!.IsImage);
        Assert.Equal("[Imagen PNG, 292 KB]", _b.LastReceived.Preview());
        // B now "has" the image on its clipboard: its monitor must not send it back.
        Assert.Equal(OutgoingDecision.SkipJustReceived, (await _b.SendImageAsync(png, "image/png")).Decision);
        Assert.Equal(OutgoingDecision.SkipAlreadySent, (await _a.SendImageAsync(png, "image/png")).Decision);
        Assert.Equal(OutgoingDecision.SkipBlank, (await _a.SendImageAsync([], "image/png")).Decision);
        await Assert.ThrowsAsync<ArgumentException>(() => _a.SendImageAsync(png, "image/gif"));
    }

    [Fact]
    public async Task SensitiveAndBlankAreNotSent()
    {
        Assert.Equal(OutgoingDecision.SkipSensitive, (await _a.SendClipboardAsync("p4ssw0rd", isSensitive: true)).Decision);
        Assert.Equal(OutgoingDecision.SkipBlank, (await _a.SendClipboardAsync("   ")).Decision);
        Assert.Empty(_clipB.Writes);
    }

    [Fact]
    public async Task SensitiveIsSentOnlyWhenEnabled()
    {
        Assert.False(_a.SendSensitive);
        _a.SendSensitive = true;
        Assert.True((await _a.SendClipboardAsync("p4ssw0rd", isSensitive: true)).AnyOk);
        Assert.Equal(["p4ssw0rd"], _clipB.Writes);
        _a.SendSensitive = false;
        Assert.Equal(OutgoingDecision.SkipSensitive, (await _a.SendClipboardAsync("0tr4", isSensitive: true)).Decision);
    }

    [Fact]
    public async Task DisablingStopsServerAndSending()
    {
        await _b.SetSyncEnabledAsync(false);
        Assert.False(_b.IsServerRunning);
        var toB = await _a.SendClipboardAsync("x");
        Assert.False(toB.AnyOk);
        Assert.StartsWith("Error de conexión", toB.Results[0].Error);
        Assert.Equal(OutgoingDecision.SkipDisabled, (await _b.SendClipboardAsync("y")).Decision);

        await _b.SetSyncEnabledAsync(true);
        Assert.True((await _a.SendClipboardAsync("x")).AnyOk, "failed send is retried when the content is copied again");
    }

    [Fact]
    public async Task SyncEnabledIsPersisted()
    {
        await _b.SetSyncEnabledAsync(false);
        var reloaded = new ConfigStore(Path.Combine(_dir, "B.json")).LoadOrCreate();
        Assert.False(reloaded.SyncEnabled);
    }

    [Fact]
    public async Task PingReturnsIdentity()
    {
        var ping = await _a.PingAsync(_b.DeviceId);
        Assert.True(ping.Ok, ping.Error);
        Assert.Equal(_b.DeviceId, ping.Info!.DeviceId);
        Assert.Equal("B", ping.Info.DeviceName);
    }

    [Fact]
    public async Task WrongSecretIsRejected()
    {
        _a.AddOrUpdatePeer(_b.DeviceId, "B", "127.0.0.1", _b.HttpPort, PairKeys.NewRandomSecret());
        var outcome = await _a.SendClipboardAsync("secreto");
        Assert.Equal(401, outcome.Results[0].HttpStatus);
        Assert.Empty(_clipB.Writes);
    }

    [Fact]
    public async Task ReplayedRequestIsRejected()
    {
        var keys = PairKeys.Derive(_secret);
        var env = EnvelopeCodec.Seal(keys.EncKey, _a.DeviceId, ClipboardPayload.Text("uno", "A"));
        var body = Encoding.UTF8.GetBytes(EnvelopeCodec.Encode(env));
        var headers = RequestSigner.Sign(keys.MacKey, _a.DeviceId, "POST", "/clipboard", body).ToDictionary();

        using var http = new HttpClient();
        async Task<HttpStatusCode> Post()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_b.HttpPort}/clipboard") { Content = new ByteArrayContent(body) };
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            return (await http.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Post());
        Assert.Equal(HttpStatusCode.Unauthorized, await Post());
        Assert.Single(_clipB.Writes);
    }

    [Fact]
    public async Task LargeContentIsDelivered()
    {
        var big = new string('x', 5 * 1024 * 1024);
        Assert.True((await _a.SendClipboardAsync(big)).AnyOk);
        Assert.Equal(big.Length, _clipB.Writes.Single().Length);
    }

    [Fact]
    public async Task OfflinePeerFailsFast()
    {
        _a.AddOrUpdatePeer("ghost", "Ghost", "127.0.0.1", FreePort(), PairKeys.NewRandomSecret());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await _a.SendClipboardAsync("hola");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Contains(outcome.Results, r => r.DeviceId == "ghost" && !r.Ok);
        Assert.Contains(outcome.Results, r => r.DeviceId == _b.DeviceId && r.Ok);
    }

    [Fact]
    public async Task SecretsAndStateSurviveRestart()
    {
        await _a.SendClipboardAsync("persistente");
        await _b.DisposeAsync();
        _b = new SyncNode(new ConfigStore(Path.Combine(_dir, "B.json")), new PlainSecretProtector(), _clipB);
        await _b.StartAsync();
        Assert.True((await _a.PingAsync(_b.DeviceId)).Ok);
        Assert.Equal(OutgoingDecision.SkipJustReceived, (await _b.SendClipboardAsync("persistente")).Decision);
    }
}
