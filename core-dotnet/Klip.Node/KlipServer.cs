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

        // Casts to Delegate: otherwise the method group binds to RequestDelegate and the IResult is never executed.
        app.MapPost("/clipboard", (Delegate)((HttpContext ctx) => Signed(ctx, "/clipboard", node.Verifier, (id, body, ip) => node.ReceiveClipboard(id, body, ip))));
        app.MapGet("/ping", (Delegate)((HttpContext ctx) => Signed(ctx, "/ping", node.Verifier, (id, _, ip) =>
        {
            node.MarkSeen(id, ip);
            return new Reply(200, node.PingInfo);
        })));
        app.MapPost("/pair/request", (Delegate)(async (HttpContext ctx) =>
            ToResult(node.HandlePairRequest(await ReadBodyAsync(ctx.Request), RemoteIp(ctx)))));
        app.MapPost("/pair/confirm", (Delegate)((HttpContext ctx) => SignedAsync(ctx, "/pair/confirm", node.SessionVerifier, (id, _, ip) => node.HandlePairConfirmAsync(id, ip))));
        app.MapPost("/pair/cancel", (Delegate)((HttpContext ctx) => Signed(ctx, "/pair/cancel", node.SessionVerifier, (id, _, _) => node.HandlePairCancel(id))));
        app.MapPost("/group/introduce", (Delegate)((HttpContext ctx) => Signed(ctx, "/group/introduce", node.Verifier, (id, body, ip) =>
        {
            node.MarkSeen(id, ip, notify: false);
            return node.HandleIntroduce(id, body);
        })));
        app.MapPost("/unpair", (Delegate)((HttpContext ctx) => Signed(ctx, "/unpair", node.Verifier, (id, _, _) => node.HandleUnpair(id))));
        app.MapPost("/group/remove", (Delegate)((HttpContext ctx) => Signed(ctx, "/group/remove", node.Verifier, (id, body, _) => node.HandleGroupRemove(id, body))));

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

    private Task<IResult> Signed(HttpContext ctx, string path, RequestVerifier verifier, Func<string, byte[], string?, Reply> handler) =>
        SignedAsync(ctx, path, verifier, (id, body, ip) => Task.FromResult(handler(id, body, ip)));

    private async Task<IResult> SignedAsync(HttpContext ctx, string path, RequestVerifier verifier, Func<string, byte[], string?, Task<Reply>> handler)
    {
        var body = await ReadBodyAsync(ctx.Request);
        var auth = verifier.Verify(Headers(ctx), ctx.Request.Method, path, body);
        if (auth != VerifyResult.Ok)
        {
            node.Log($"{ctx.Request.Method} {path} rechazado desde {RemoteIp(ctx)}: {auth}");
            return Results.StatusCode(auth.HttpStatus());
        }
        var deviceId = ctx.Request.Headers[Protocol.HeaderDevice].ToString();
        return ToResult(await handler(deviceId, body, RemoteIp(ctx)));
    }

    private static IResult ToResult(Reply reply) =>
        reply.Body is null ? Results.StatusCode(reply.Status) : Results.Json(reply.Body, ProtocolJson.Options, statusCode: reply.Status);

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
