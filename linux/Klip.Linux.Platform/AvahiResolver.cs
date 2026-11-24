using System.Diagnostics;
using Klip.Core;
using Klip.Node;

namespace Klip.Linux.Platform;

/// <summary>
/// mDNS/DNS-SD through avahi-utils (docs/protocol.md §8.7): announces with avahi-publish-service and resolves with
/// avahi-browse. Optional: without avahi-utils the radar and passive IP updates still work.
/// </summary>
public sealed class AvahiResolver(Action<string> log, Func<System.Net.IPAddress?>? preferredIPv4 = null) : IPeerResolver, IDisposable
{
    private Process? _publisher;

    public static bool Available => Shell.Exists("avahi-publish-service") && Shell.Exists("avahi-browse");

    public void Register(string deviceId, int port)
    {
        if (_publisher is not null || !Available) return;
        var psi = new ProcessStartInfo("avahi-publish-service") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { deviceId, Protocol.MdnsServiceType, port.ToString(), $"id={deviceId}" }) psi.ArgumentList.Add(a);
        try
        {
            _publisher = Process.Start(psi);
            _publisher?.BeginErrorReadLine();
            _publisher?.BeginOutputReadLine();
            log("mDNS (avahi): servicio anunciado");
        }
        catch (Exception e)
        {
            log($"mDNS (avahi) no disponible: {e.Message}");
        }
    }

    public void Unregister()
    {
        if (_publisher is null) return;
        try { _publisher.Kill(); } catch { /* gone */ }
        _publisher.Dispose();
        _publisher = null;
    }

    public async Task<string?> ResolveAsync(string deviceId, CancellationToken ct)
    {
        if (!Available) return null;
        // -r resolve, -p parsable, -t terminate after dumping the cache, -4 IPv4 only.
        var r = await Shell.RunAsync("avahi-browse", ["-rpt4", Protocol.MdnsServiceType], timeoutMs: 1500);
        ct.ThrowIfCancellationRequested();
        // Resolved lines: =;iface;IPv4;name;type;domain;host;address;port;txt
        var candidates = r.Text.Split('\n')
            .Select(l => l.Split(';'))
            .Where(f => f.Length >= 9 && f[0] == "=" && f[3] == deviceId)
            .Select(f => f[7])
            .Distinct()
            .ToList();
        var mine = preferredIPv4?.Invoke()?.ToString();
        var prefix = mine is null ? null : mine[..(mine.LastIndexOf('.') + 1)];
        return candidates.FirstOrDefault(c => prefix is not null && c.StartsWith(prefix, StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
    }

    public void Dispose() => Unregister();
}
