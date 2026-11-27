using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Klip.Linux.Platform;
using Klip.Node;

namespace Klip.Linux;

/// <summary>
/// Tray + window host (plan §4.1, Linux edition). Without a tray host (plain GNOME) the window is the UI:
/// closing it keeps Klip running and launching Klip again shows it.
/// </summary>
internal sealed class App : Application
{
    private static readonly ConcurrentQueue<string> PendingCommands = new();
    private static App? _current;

    public static bool StartHidden { get; set; }

    private KlipController _controller = null!;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _syncItem;
    private NativeMenuItem? _autostartItem;
    private bool _hasTray;
    private bool _exiting;

    public static WindowIcon IconActive { get; private set; } = null!;
    public static WindowIcon IconPaused { get; private set; } = null!;

    /// <summary>Commands from `klip --send-clipboard` / a second launch (any thread, even before startup).</summary>
    public static void EnqueueCommand(string command)
    {
        PendingCommands.Enqueue(command);
        if (_current is { } app) Dispatcher.UIThread.Post(app.DrainCommands);
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(KlipTheme.Resources());
        RequestedThemeVariant = ThemeVariant.Default; // follow the desktop's light/dark setting
    }

    public override void OnFrameworkInitializationCompleted()
    {
        IconActive = new WindowIcon(Icons.Render(active: true));
        IconPaused = new WindowIcon(Icons.Render(active: false));
        FileLog.Write($"Klip {typeof(App).Assembly.GetName().Version} arrancando…");
        _ = InitializeAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync()
    {
        // Off the UI thread: reading the keyring or probing D-Bus must never freeze the window.
        _controller = await Task.Run(() => new KlipController());
        _controller.StateChanged += () => Dispatcher.UIThread.Post(UpdateState);
        _controller.Node.Received += item =>
        {
            if (item.IsTest) Notifier.Show($"Prueba recibida de {item.From}", item.Preview(200));
        };
        _controller.Node.PairingRequested += p => Dispatcher.UIThread.Post(() => ShowIncomingPairing(p));
        _controller.Node.IncomingPairingFinished += (id, status, name) => Dispatcher.UIThread.Post(() => OnIncomingFinished(id, status, name));

        _hasTray = await DesktopSession.HasTrayAsync();
        if (_hasTray) CreateTray();
        try { if (_controller.Node.StartWithWindows) Autostart.Apply(true); }
        catch (Exception e) { FileLog.Write("Autostart: " + e.Message); }

        await Task.Run(_controller.StartAsync);
        if (_controller.ServerError is { } error) Notifier.Show("Klip", "No se puede recibir: " + error);

        _current = this;
        if (!StartHidden || !_hasTray) ShowWindow();
        DrainCommands();
        UpdateState();
    }

    private void DrainCommands()
    {
        while (PendingCommands.TryDequeue(out var command))
        {
            if (command == "send-clipboard") _ = _controller.SendCurrentAsync(manual: true);
            else ShowWindow();
        }
    }

    private void CreateTray()
    {
        _syncItem = new NativeMenuItem("Sincronización activada") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _syncItem.Click += async (_, _) => await _controller.SetSyncAsync(!_controller.Node.SyncEnabled);
        _autostartItem = new NativeMenuItem("Iniciar con la sesión") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _autostartItem.Click += (_, _) => ToggleAutostart();
        var send = new NativeMenuItem("Enviar portapapeles ahora");
        send.Click += async (_, _) => await _controller.SendCurrentAsync(manual: true);
        var open = new NativeMenuItem("Abrir configuración…");
        open.Click += (_, _) => ShowWindow();
        var test = new NativeMenuItem("Enviar prueba a todos");
        test.Click += async (_, _) => await SendTestAsync(null);
        var quit = new NativeMenuItem("Salir");
        quit.Click += async (_, _) => await ExitAsync();

        _tray = new TrayIcon
        {
            Icon = IconActive,
            ToolTipText = "Klip",
            Menu = new NativeMenu { Items = { _syncItem, new NativeMenuItemSeparator(), open, send, test, new NativeMenuItemSeparator(), _autostartItem, new NativeMenuItemSeparator(), quit } },
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    public KlipController Controller => _controller;
    public bool HasTray => _hasTray;

    private void UpdateState()
    {
        var on = _controller.Node.SyncEnabled && _controller.Node.IsServerRunning;
        if (_tray is not null)
        {
            _tray.Icon = on ? IconActive : IconPaused;
            _tray.ToolTipText = on ? "Klip — Sincronización activada" : "Klip — Sincronización pausada";
        }
        if (_syncItem is not null) _syncItem.IsChecked = on;
        if (_autostartItem is not null) _autostartItem.IsChecked = _controller.Node.StartWithWindows;
        _window?.RefreshAll();
    }

    public void ToggleAutostart()
    {
        var enabled = !_controller.Node.StartWithWindows;
        try
        {
            Autostart.Apply(enabled);
            _controller.Node.StartWithWindows = enabled;
        }
        catch (Exception e)
        {
            Notifier.Show("Klip", "No se pudo cambiar el inicio automático: " + e.Message);
        }
        UpdateState();
    }

    public async Task SendTestAsync(string? deviceId)
    {
        var outcome = await _controller.Node.SendTestAsync(deviceId);
        var text = outcome.Results.Count == 0
            ? (outcome.Decision == Core.OutgoingDecision.SkipDisabled ? "La sincronización está desactivada." : "No hay dispositivos activos.")
            : string.Join("\n", outcome.Results.Select(r => r.Ok ? $"✔ {r.Name}: enviado" : $"✖ {r.Name}: {r.Error}"));
        Notifier.Show("Enviar prueba", text);
        _window?.SetActivity(text.Replace('\n', ' '));
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(this);
            _window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true; // keep running in the background
                HideWindow();
            };
        }
        _controller.Node.PairingOpen = true; // pairing and radar only while the window is visible (plan §5.1)
        _controller.Node.StartDiscovery();
        _window.Show();
        _window.Activate();
        _window.RefreshAll();
    }

    private void HideWindow()
    {
        _controller.Node.PairingOpen = false;
        _controller.Node.StopDiscovery();
        _window?.Hide();
        if (!_hasTray) Notifier.Show("Klip sigue activo", "Vuelve a abrir Klip desde el menú de aplicaciones para ver la ventana.");
    }

    // ---------- Incoming pairing ----------

    private PinWindow? _incoming;
    private string? _incomingSession;

    private void ShowIncomingPairing(IncomingPairing p)
    {
        _incoming?.CloseExternally();
        var dialog = new PinWindow($"{p.DeviceName} quiere vincularse", p.Pin, $"Acepta solo si {p.DeviceName} muestra el mismo código.");
        _incoming = dialog;
        _incomingSession = p.SessionId;
        dialog.Closed += (_, _) =>
        {
            if (!dialog.ClosedExternally)
            {
                _controller.Node.RespondToPairing(p.SessionId, dialog.Accepted);
                if (dialog.Accepted) _window?.SetActivity($"Esperando la confirmación en {p.DeviceName}…");
            }
            if (_incoming == dialog) _incoming = null;
        };
        if (_window is { IsVisible: true }) dialog.Show(_window);
        else dialog.Show();
    }

    private void OnIncomingFinished(string sessionId, PairingStatus status, string name)
    {
        if (_incomingSession == sessionId) _incoming?.CloseExternally();
        var text = status switch
        {
            PairingStatus.Accepted => $"Vinculado con {name}",
            PairingStatus.Cancelled => $"{name} canceló la vinculación",
            PairingStatus.TimedOut => $"La vinculación con {name} caducó",
            PairingStatus.Rejected => $"Vinculación con {name} rechazada",
            _ => null,
        };
        if (text is null) return;
        _window?.SetActivity(text);
        Notifier.Show("Klip", text);
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _incoming?.CloseExternally();
        _window?.Close();
        await _controller.DisposeAsync();
        _tray?.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}

internal static class Icons
{
    // Same "K" as the Android launcher icon (108×108 viewport).
    private static readonly Geometry K = Geometry.Parse("M38,32h9v17l15,-17h11l-16,18l17,22h-11l-12,-16l-4,4.5v11.5h-9z");

    public static Bitmap Render(bool active, int size = 64)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var ctx = bmp.CreateDrawingContext();
        var bg = new SolidColorBrush(active ? Color.Parse("#2563EB") : Color.Parse("#8A8F98"));
        ctx.DrawRectangle(bg, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
        var scale = size / 108.0 * 1.25;
        var offset = (size - 108 * scale) / 2;
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset)))
        {
            ctx.DrawGeometry(Brushes.White, null, K);
        }
        return bmp;
    }
}
