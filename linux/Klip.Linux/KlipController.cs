using Klip.Core;
using Klip.Linux.Platform;
using Klip.Node;

namespace Klip.Linux;

/// <summary>Glue between the node and the Linux clipboard: automatic sending when possible, manual otherwise.</summary>
internal sealed class KlipController : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _debounce;

    public SyncNode Node { get; }
    public ILinuxClipboard? Clipboard { get; }
    public string? ClipboardProblem { get; }
    public LinuxSecretProtector Secrets { get; } = new();

    /// <summary>True while copies are sent automatically (watching the clipboard).</summary>
    public bool AutoSend { get; private set; }

    /// <summary>Why the receiver could not start (e.g. port in use), or null.</summary>
    public string? ServerError { get; private set; }

    public event Action? StateChanged;

    public KlipController()
    {
        (Clipboard, ClipboardProblem) = LinuxClipboard.Create();
        var writer = new ClipboardWriter(this);
        Node = new SyncNode(
            new ConfigStore(LinuxPaths.Config, () => new NodeConfig { Platform = "linux", DeviceName = Environment.MachineName }),
            Secrets, writer)
        {
            Resolver = AvahiResolver.Available ? new AvahiResolver(FileLog.Write, LanAddress.PreferredIPv4) : null,
        };
        Node.LogMessage += FileLog.Write;
        if (Clipboard is not null) Clipboard.Changed += OnClipboardChanged;
        if (ClipboardProblem is not null) FileLog.Write("Portapapeles: " + ClipboardProblem);
    }

    public async Task StartAsync()
    {
        FileLog.Write($"Klip iniciado. deviceId={Node.DeviceId} sesión={DesktopSession.Kind} escritorio={DesktopSession.Desktop} " +
                      $"portapapeles={Clipboard?.Name ?? "ninguno"} secretos={Secrets.Storage}");
        try
        {
            await Node.StartAsync();
            ServerError = null;
        }
        catch (Exception e)
        {
            // Sending still works without the receiver: keep going.
            ServerError = e is IOException && e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                ? $"El puerto {Node.HttpPort} está ocupado (¿hay otra copia de Klip abierta?)"
                : e.Message;
            FileLog.Write("No se pudo arrancar el receptor: " + e);
        }
        if (Node.SyncEnabled) await StartWatchingAsync();
        StateChanged?.Invoke();
    }

    /// <summary>The single switch (tray and window): server + clipboard watching.</summary>
    public async Task SetSyncAsync(bool enabled)
    {
        try
        {
            await Node.SetSyncEnabledAsync(enabled);
            ServerError = null;
        }
        catch (Exception e)
        {
            ServerError = e.Message;
            FileLog.Write("No se pudo cambiar la sincronización: " + e);
        }
        if (enabled) await StartWatchingAsync();
        else
        {
            Clipboard?.StopWatching();
            AutoSend = false;
        }
        StateChanged?.Invoke();
    }

    private async Task StartWatchingAsync()
    {
        if (Clipboard is null) return;
        AutoSend = await Clipboard.StartWatchingAsync();
        if (!AutoSend) FileLog.Write("Envío automático no disponible: " + Clipboard.WatchUnavailableReason);
    }

    private void OnClipboardChanged()
    {
        // X11 may notify several times per copy: wait 150 ms of quiet before reading.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(150, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            await SendCurrentAsync(manual: false);
        }, TaskScheduler.Default);
    }

    /// <summary>Reads the clipboard and sends it. Manual sends notify the result; automatic ones stay quiet.</summary>
    public async Task<string> SendCurrentAsync(bool manual)
    {
        if (Clipboard is null) return ClipboardProblem ?? "Sin acceso al portapapeles";
        await _sendGate.WaitAsync();
        try
        {
            var content = await Clipboard.ReadAsync();
            if (content.Sensitive)
            {
                FileLog.Write("Contenido sensible en el portapapeles: no se envía");
                return Report(manual, "Contenido sensible: no se envía");
            }
            if (content.Text is null) return Report(manual, "El portapapeles no contiene texto");
            var outcome = await Node.SendClipboardAsync(content.Text);
            var message = outcome.Decision switch
            {
                OutgoingDecision.Send when outcome.Results.Count == 0 => "No hay dispositivos vinculados activos",
                OutgoingDecision.Send when outcome.Results.All(r => r.Ok) => "Enviado a " + string.Join(", ", outcome.Results.Select(r => r.Name)),
                OutgoingDecision.Send => string.Join(", ", outcome.Results.Select(r => r.Ok ? $"✔ {r.Name}" : $"✖ {r.Name}: {r.Error}")),
                OutgoingDecision.SkipAlreadySent or OutgoingDecision.SkipJustReceived => "Sin cambios nuevos",
                OutgoingDecision.SkipBlank => "El portapapeles está vacío",
                OutgoingDecision.SkipDisabled => "La sincronización está desactivada",
                _ => outcome.Decision.ToString(),
            };
            if (!manual && outcome.Decision == OutgoingDecision.Send && outcome.Results.Count > 0 && !outcome.AnyOk)
                FileLog.Write("Ningún dispositivo recibió la copia: " + message);
            return Report(manual, message);
        }
        catch (Exception e)
        {
            FileLog.Write("Error al enviar el portapapeles: " + e);
            return Report(manual, "Error: " + e.Message);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static string Report(bool manual, string message)
    {
        if (manual) Notifier.Show("Klip", message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        Clipboard?.Dispose();
        await Node.DisposeAsync();
        (Node.Resolver as IDisposable)?.Dispose();
    }

    private sealed class ClipboardWriter(KlipController owner) : IClipboardWriter
    {
        public void WriteText(string text)
        {
            if (owner.Clipboard is null) throw new InvalidOperationException(owner.ClipboardProblem);
            owner.Clipboard.WriteAsync(text).GetAwaiter().GetResult();
        }
    }
}
