using System.Net;
using Klip.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klip.Node;

/// <summary>Embedded HTTP server (Kestrel). Transport only; the logic lives in <see cref="SyncNode"/>.</summary>
internal sealed class KlipServer(SyncNode node) : IAsyncDisposable
{
    private WebApplication? _app;

    public bool IsRunning => _app is not null;

    public async Task StartAsync(int port)
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null; // no app-level size limit (plan §0)
            o.ListenAnyIP(port);
        });
        var app = builder.Build();
        // Cast to Delegate: otherwise the method group binds to RequestDelegate and the IResult is never executed.
        app.MapPost("/clipboard", (Delegate)HandleClipboardAsync);
        app.MapGet("/ping", (Delegate)HandlePingAsync);
        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private async Task<IResult> HandleClipboardAsync(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request);
        var auth = node.Verifier.Verify(Headers(ctx), "POST", "/clipboard", body);
        if (auth != VerifyResult.Ok)
        {
            node.Log($"POST /clipboard rechazado desde {RemoteIp(ctx)}: {auth}");
            return Results.StatusCode(auth.HttpStatus());
        }
        var deviceId = ctx.Request.Headers[Protocol.HeaderDevice].ToString();
        return Results.StatusCode(node.ReceiveClipboard(deviceId, body, RemoteIp(ctx)));
    }

    private async Task<IResult> HandlePingAsync(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request);
        var auth = node.Verifier.Verify(Headers(ctx), "GET", "/ping", body);
        if (auth != VerifyResult.Ok) return Results.StatusCode(auth.HttpStatus());
        node.MarkSeen(ctx.Request.Headers[Protocol.HeaderDevice].ToString(), RemoteIp(ctx));
        return Results.Json(new PingInfo(node.DeviceId, node.DeviceName, node.Platform));
    }

    private static IEnumerable<KeyValuePair<string, string?>> Headers(HttpContext ctx) =>
        ctx.Request.Headers.Select(h => new KeyValuePair<string, string?>(h.Key, h.Value.ToString()));

    private static string? RemoteIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
