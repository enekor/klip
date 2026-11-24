using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Klip.Core;

namespace Klip.Node;

public sealed record PeerSendResult(string DeviceId, string Name, bool Ok, int? HttpStatus, string? Error);

public sealed record PingInfo(string DeviceId, string DeviceName, string Platform);

public sealed record PingResult(bool Ok, long ElapsedMs, PingInfo? Info, int? HttpStatus, string? Error);

/// <summary>Outgoing HTTP calls to paired devices. See docs/protocol.md §2.6 for timeouts.</summary>
public sealed class PeerClient : IDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts below
    };

    /// <summary>10 s base + 1 s per MB of body.</summary>
    public static TimeSpan TotalTimeout(long bodyBytes) =>
        TimeSpan.FromSeconds(10 + bodyBytes / (1024 * 1024));

    public static Uri BaseUri(string host, int port) => new UriBuilder("http", host, port).Uri;

    public async Task<PeerSendResult> SendClipboardAsync(PeerConfig peer, PairKeys keys, string localDeviceId, Envelope envelope, CancellationToken ct = default)
    {
        var body = Encoding.UTF8.GetBytes(EnvelopeCodec.Encode(envelope));
        try
        {
            using var response = await SendSignedAsync(HttpMethod.Post, peer, keys, localDeviceId, "/clipboard", body, ct);
            var status = (int)response.StatusCode;
            return new PeerSendResult(peer.DeviceId, peer.Name, response.IsSuccessStatusCode, status,
                response.IsSuccessStatusCode ? null : $"HTTP {status}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new PeerSendResult(peer.DeviceId, peer.Name, false, null, Describe(e));
        }
    }

    public async Task<PingResult> PingAsync(PeerConfig peer, PairKeys keys, string localDeviceId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await SendSignedAsync(HttpMethod.Get, peer, keys, localDeviceId, "/ping", [], ct);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return new PingResult(false, sw.ElapsedMilliseconds, null, status, $"HTTP {status}");
            var info = await response.Content.ReadFromJsonAsync<PingInfo>(ct);
            return new PingResult(true, sw.ElapsedMilliseconds, info, status, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new PingResult(false, sw.ElapsedMilliseconds, null, null, Describe(e));
        }
    }

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, PeerConfig peer, PairKeys keys, string localDeviceId,
        string path, byte[] body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TotalTimeout(body.Length));
        var request = new HttpRequestMessage(method, new Uri(BaseUri(peer.Host, peer.Port), path));
        foreach (var (name, value) in RequestSigner.Sign(keys.MacKey, localDeviceId, method.Method, path, body).ToDictionary())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (body.Length > 0 || method == HttpMethod.Post)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        return await _http.SendAsync(request, timeout.Token);
    }

    private static string Describe(Exception e) => e switch
    {
        TaskCanceledException or OperationCanceledException => "Timeout",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException se } => $"Error de conexión ({se.SocketErrorCode})",
        HttpRequestException h => $"Error de conexión ({h.Message})",
        _ => e.Message,
    };

    public void Dispose() => _http.Dispose();
}
