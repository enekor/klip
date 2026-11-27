using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Klip.Core;
using Klip.Node;

namespace Klip.Windows;

/// <summary>
/// System tray host. A single "Sincronización activada" switch starts/stops both the clipboard
/// monitor and the HTTP server; its state persists across restarts (plan §4.1).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly SyncNode _node;
    private readonly ClipboardMonitor _monitor;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _syncItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly Icon _iconOn = new(AppIcon.Main, SystemInformation.SmallIconSize);
    private readonly Icon _iconOff = TrayIcons.Create(active: false);
    private readonly RegisteredWaitHandle _showWait;
    private ConfigWindow? _configWindow;
    private bool _exiting;

    public TrayApp(EventWaitHandle showEvent)
    {
        // Ensure a WinForms synchronization context exists before capturing it.
        using (new Control()) { }
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _node = new SyncNode(new ConfigStore(AppPaths.Config), new DpapiProtector(), new WindowsClipboardWriter(_ui))
        {
            Resolver = new WindowsDnsSd(FileLog.Write, LanAddress.PreferredIPv4),
        };
        _node.LogMessage += FileLog.Write;
        // Show the received test text itself, so the user sees exactly what arrived.
        _node.Received += item =>
        {
            if (item.IsTest) _ui.Post(_ => Balloon($"Prueba recibida de {item.From}", item.Preview(200)), null);
        };
        _node.PairingRequested += p => _ui.Post(_ => ShowIncomingPairing(p), null);
        _node.IncomingPairingFinished += (sessionId, status, name) => _ui.Post(_ => OnIncomingPairingFinished(sessionId, status, name), null);

        _monitor = new ClipboardMonitor(() => _node.SendSensitive);
        _monitor.Copied += OnLocalCopy;

        _syncItem = new ToolStripMenuItem("Sincronización activada") { CheckOnClick = false };
        _syncItem.Click += async (_, _) => await SetSyncAsync(!_node.SyncEnabled);
        _autostartItem = new ToolStripMenuItem("Iniciar con Windows") { Checked = _node.StartWithWindows };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_syncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir configuración…", null, (_, _) => OpenConfig());
        menu.Items.Add("Enviar prueba a todos", null, async (_, _) => await SendTestAsync(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Configurar firewall…", null, (_, _) => ConfigureFirewall(askFirst: false));
        menu.Items.Add("Abrir carpeta de registros", null, (_, _) => OpenLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, async (_, _) => await ExitAsync());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenConfig();

        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => _ui.Post(_ => OpenConfig(), null), null, -1, executeOnlyOnce: false);

        _ = InitializeAsync();
    }

    public SyncNode Node => _node;

    private async Task InitializeAsync()
    {
        FileLog.Write($"Klip iniciado. deviceId={_node.DeviceId}");
        try { Autostart.Apply(_node.StartWithWindows); }
        catch (Exception e) { FileLog.Write($"Autostart: {e.Message}"); }

        await SetSyncAsync(_node.SyncEnabled, persist: false);

        if (!_node.FirewallPromptShown)
        {
            _node.FirewallPromptShown = true;
            ConfigureFirewall(askFirst: true);
        }
    }

    internal async Task SetSyncAsync(bool enabled, bool persist = true)
    {
        try
        {
            if (persist) await _node.SetSyncEnabledAsync(enabled);
            else if (enabled) await _node.StartAsync();
            if (enabled) _monitor.Start(); else _monitor.Stop();
        }
        catch (Exception e)
        {
            // Typically: port already in use.
            FileLog.Write($"Error al cambiar la sincronización: {e}");
            _monitor.Stop();
            if (enabled) await _node.SetSyncEnabledAsync(false);
            Balloon("Klip", $"No se pudo activar la sincronización: {e.Message}", ToolTipIcon.Error);
        }
        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        var on = _node.SyncEnabled && _node.IsServerRunning;
        _syncItem.Checked = on;
        _tray.Icon = on ? _iconOn : _iconOff;
        _tray.Text = on ? "Klip — Sincronización activada" : "Klip — Sincronización pausada";
        _configWindow?.RefreshAll();
    }

    private async void OnLocalCopy(ClipboardRead read)
    {
        SendOutcome outcome;
        // The node only lets sensitive text through when the user enabled it in the configuration window.
        if (read.Text is not null) outcome = await _node.SendClipboardAsync(read.Text, isSensitive: read.Sensitive);
        else if (read.Sensitive) outcome = new SendOutcome(OutgoingDecision.SkipSensitive, []);
        else if (read.Png is not null) outcome = await _node.SendImageAsync(read.Png, "image/png");
        else return;
        if (outcome.Decision == OutgoingDecision.SkipSensitive)
        {
            FileLog.Write("Contenido sensible detectado en el portapapeles: no se envía");
            return;
        }
        if (outcome.Decision == OutgoingDecision.Send && outcome.Results.Count > 0 && !outcome.AnyOk)
        {
            FileLog.Write("Ningún dispositivo recibió la copia: " + string.Join(", ", outcome.Results.Select(r => $"{r.Name}: {r.Error}")));
        }
    }

    internal async Task SendTestAsync(string? deviceId)
    {
        var outcome = await _node.SendTestAsync(deviceId);
        if (outcome.Results.Count == 0)
        {
            Balloon("Enviar prueba", outcome.Decision == OutgoingDecision.SkipDisabled
                ? "La sincronización está desactivada."
                : "No hay dispositivos activos.", ToolTipIcon.Warning);
            return;
        }
        var lines = outcome.Results.Select(r => r.Ok ? $"✔ {r.Name}: Enviado ({r.HttpStatus} OK)" : $"✖ {r.Name}: {r.Error}");
        Balloon("Enviar prueba", string.Join("\n", lines), outcome.Results.All(r => r.Ok) ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    // ---------- Incoming pairing (only possible while the configuration window is open) ----------

    private PinDialog? _incomingDialog;
    private string? _incomingSession;

    private void ShowIncomingPairing(IncomingPairing p)
    {
        _incomingDialog?.CloseExternally();
        var dialog = new PinDialog($"{p.DeviceName} quiere vincularse", p.Pin, $"Acepta solo si {p.DeviceName} muestra el mismo código.");
        _incomingDialog = dialog;
        _incomingSession = p.SessionId;
        dialog.FormClosed += (_, _) =>
        {
            if (!dialog.ClosedExternally) _node.RespondToPairing(p.SessionId, dialog.Accepted);
            if (dialog.Accepted && !dialog.ClosedExternally)
                Balloon("Klip", $"Esperando la confirmación en {p.DeviceName}…");
            if (_incomingDialog == dialog) _incomingDialog = null;
            dialog.Dispose();
        };
        dialog.Show(_configWindow is { IsDisposed: false } owner ? owner : null);
        dialog.Activate();
    }

    private void OnIncomingPairingFinished(string sessionId, PairingStatus status, string name)
    {
        if (_incomingSession == sessionId) _incomingDialog?.CloseExternally();
        switch (status)
        {
            case PairingStatus.Accepted: Balloon("Klip", $"Vinculado con {name}"); break;
            case PairingStatus.Cancelled: Balloon("Klip", $"{name} canceló la vinculación", ToolTipIcon.Warning); break;
            case PairingStatus.TimedOut: Balloon("Klip", $"La vinculación con {name} caducó", ToolTipIcon.Warning); break;
        }
    }

    private void OpenConfig()
    {
        if (_configWindow is { IsDisposed: false })
        {
            if (_configWindow.WindowState == FormWindowState.Minimized) _configWindow.WindowState = FormWindowState.Normal;
            _configWindow.Activate();
            return;
        }
        _configWindow = new ConfigWindow(this);
        _configWindow.FormClosed += (_, _) => _configWindow = null;
        _configWindow.Show();
    }

    internal void ToggleAutostart()
    {
        var enabled = !_node.StartWithWindows;
        try
        {
            Autostart.Apply(enabled);
            _node.StartWithWindows = enabled;
            _autostartItem.Checked = enabled;
        }
        catch (Exception e)
        {
            Balloon("Klip", $"No se pudo cambiar el inicio automático: {e.Message}", ToolTipIcon.Error);
        }
        _configWindow?.RefreshAll();
    }

    internal void ConfigureFirewall(bool askFirst)
    {
        if (Firewall.RulesExist())
        {
            if (!askFirst) Balloon("Firewall", "Las reglas de Klip ya existen (TCP 8765 y UDP 8766, red privada).");
            return;
        }
        if (askFirst && MessageBox.Show(
                "Klip necesita reglas de entrada en el Firewall de Windows (TCP 8765 y UDP 8766, solo redes privadas) " +
                "para recibir el portapapeles de tus otros dispositivos.\n\n¿Crearlas ahora? Windows pedirá permisos de administrador.",
                "Klip — Firewall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        var ok = Firewall.CreateRules();
        Balloon("Firewall", ok ? "Reglas creadas correctamente." : "No se crearon las reglas (cancelado o bloqueado por políticas).",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    internal static void OpenLogs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
        System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(AppPaths.LogFile)!);
    }

    internal void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        _tray.ShowBalloonTip(4000, title, text, icon);

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _monitor.Stop();
        _configWindow?.Close();
        await _node.DisposeAsync(); // async: the UI thread stays free for in-flight clipboard writes
        _showWait.Unregister(null);
        _tray.Visible = false;
        _tray.Dispose();
        _monitor.Dispose();
        ExitThread();
    }
}

/// <summary>The "K" icon embedded in the executable (same artwork as Android and Linux).</summary>
internal static class AppIcon
{
    public static readonly Icon Main = new(typeof(AppIcon).Assembly.GetManifestResourceStream("klip.ico")!);
}

internal static class TrayIcons
{
    public static Icon Create(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(active ? Color.FromArgb(0x25, 0x63, 0xEB) : Color.FromArgb(0x8A, 0x8F, 0x98));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(bg, path);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("K", font, fg, new RectangleF(0, 0, 32, 33), sf);
        }
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
