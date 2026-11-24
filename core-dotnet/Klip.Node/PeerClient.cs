using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Klip.Core;

namespace Klip.Node;

public sealed record PeerSendResult(string DeviceId, string Name, bool Ok, int? HttpStatus, string? Error)
{
    /// <summary>True when the peer could not be reached at all (no HTTP response).</summary>
    public bool Unreachable => HttpStatus is null;
}

public sealed record PingInfo(string DeviceId, string DeviceName, string Platform);

public sealed record PingResult(bool Ok, long ElapsedMs, PingInfo? Info, int? HttpStatus, string? Error);

/// <summary>Raw HTTP outcome: status + body, or a transport error.</summary>
public sealed record HttpOutcome(int? Status, byte[] Body, string? Error)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>Outgoing HTTP calls to other nodes. See docs/protocol.md §2.6 for timeouts.</summary>
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
        var r = await SendAsync(HttpMethod.Post, peer.Host, peer.Port, "/clipboard", body, keys, localDeviceId, null, ct);
        return new PeerSendResult(peer.DeviceId, peer.Name, r.IsSuccess, r.Status, r.Error ?? (r.IsSuccess ? null : $"HTTP {r.Status}"));
    }

    public async Task<PingResult> PingAsync(string host, int port, PairKeys keys, string localDeviceId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var r = await SendAsync(HttpMethod.Get, host, port, "/ping", [], keys, localDeviceId, null, ct);
        if (!r.IsSuccess) return new PingResult(false, sw.ElapsedMilliseconds, null, r.Status, r.Error ?? $"HTTP {r.Status}");
        try
        {
            return new PingResult(true, sw.ElapsedMilliseconds, ProtocolJson.Deserialize<PingInfo>(r.Body), r.Status, null);
        }
        catch (System.Text.Json.JsonException e)
        {
            return new PingResult(false, sw.ElapsedMilliseconds, null, r.Status, "Respuesta inválida: " + e.Message);
        }
    }

    /// <summary>
    /// Sends a request. Signed when <paramref name="keys"/> is given (all routes except /pair/request).
    /// Never throws for network errors: they are reported in <see cref="HttpOutcome.Error"/>.
    /// </summary>
    public async Task<HttpOutcome> SendAsync(HttpMethod method, string host, int port, string path, byte[] body,
        PairKeys? keys, string localDeviceId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TotalTimeout(body.Length));
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseUri(host, port), path));
            if (keys is not null)
            {
                foreach (var (name, value) in RequestSigner.Sign(keys.MacKey, localDeviceId, method.Method, path, body).ToDictionary())
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
            if (method != HttpMethod.Get)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }
            using var response = await _http.SendAsync(request, cts.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            return new HttpOutcome((int)response.StatusCode, bytes, null);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return new HttpOutcome(null, [], Describe(e));
        }
    }

    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => "Timeout",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException se } => $"Error de conexión ({se.SocketErrorCode})",
        HttpRequestException h => $"Error de conexión ({h.Message})",
        _ => e.Message,
    };

    public void Dispose() => _http.Dispose();
}
