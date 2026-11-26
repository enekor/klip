using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Linux.Platform;
using Klip.Node;

namespace Klip.Linux;

/// <summary>Configuration window: identity, last received, radar, paired devices and settings.</summary>
internal sealed class MainWindow : Window
{
    private readonly App _app;
    private readonly SyncNode _node;
    private readonly TextBox _name = new() { Width = 280 };
    private readonly TextBlock _address = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
    private readonly TextBlock _sendMode = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
    private readonly TextBlock _lastReceived = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Avalonia.Controls.Image _lastImage = new() { MaxWidth = 240, MaxHeight = 160, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private byte[]? _shownImage;
    private readonly TextBlock _activity = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ToggleSwitch _sync = new() { OnContent = "Sincronización activada", OffContent = "Sincronización pausada" };
    private readonly CheckBox _autostart = new() { Content = "Iniciar con la sesión" };
    private readonly CheckBox _sendSensitive = new() { Content = "Enviar también contraseñas (texto que los gestores de contraseñas marcan como sensible)" };
    private readonly StackPanel _discovered = new() { Spacing = 6 };
    private readonly StackPanel _peers = new() { Spacing = 6 };
    private bool _refreshing;
    private bool _busy;

    public MainWindow(App app)
    {
        _app = app;
        _node = app.Controller.Node;
        Title = "Klip — Configuración";
        Icon = App.IconActive;
        Width = 820;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var save = new Button { Content = "Guardar" };
        save.Click += (_, _) => SaveName();
        _sync.IsCheckedChanged += async (_, _) =>
        {
            if (_refreshing) return;
            await _app.Controller.SetSyncAsync(_sync.IsChecked == true);
        };
        _autostart.IsCheckedChanged += (_, _) =>
        {
            if (!_refreshing && (_autostart.IsChecked == true) != _node.StartWithWindows) _app.ToggleAutostart();
        };
        _sendSensitive.IsCheckedChanged += (_, _) =>
        {
            var enabled = _sendSensitive.IsChecked == true;
            if (!_refreshing && enabled != _node.SendSensitive) _node.SendSensitive = enabled;
        };
        var sendNow = new Button { Content = "Enviar portapapeles ahora" };
        sendNow.Click += async (_, _) => SetActivity(await _app.Controller.SendCurrentAsync(manual: false));
        var testAll = new Button { Content = "Enviar prueba a todos" };
        testAll.Click += async (_, _) => await _app.SendTestAsync(null);
        var pairByIp = new Button { Content = "Vincular por IP…" };
        pairByIp.Click += async (_, _) => await PairByIp();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    Card(new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            Row(new TextBlock { Text = "Nombre:", VerticalAlignment = VerticalAlignment.Center, Width = 80 }, _name, save),
                            Row(Label("Dirección:"), _address),
                            Row(Label("Estado:"), _status),
                            Row(Label("Envío:"), _sendMode),
                            Row(_sync, _autostart),
                            _sendSensitive,
                            Row(sendNow, testAll),
                        },
                    }),
                    Card(new StackPanel { Spacing = 4, Children = { Heading("Última recepción"), _lastReceived, _lastImage } }),
                    Heading("Dispositivos detectados en la red (abre Klip en el otro dispositivo)"),
                    _discovered,
                    pairByIp,
                    Heading("Dispositivos vinculados (marca los que reciben tus copias)"),
                    _peers,
                    _activity,
                },
            },
        };

        _node.PeersChanged += OnNodeChanged;
        _node.DiscoveredChanged += OnNodeChanged;
        _node.Received += _ => OnNodeChanged();
        RefreshAll();
    }

    private static TextBlock Label(string text) => new() { Text = text, Width = 80, VerticalAlignment = VerticalAlignment.Top };
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 6, 0, 0) };

    private static StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private static Border Card(Control content) => new()
    {
        Child = content,
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
    };

    private void OnNodeChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAll);

    public void SetActivity(string text) => _activity.Text = text;

    public void RefreshAll()
    {
        _refreshing = true;
        try
        {
            if (!_name.IsFocused) _name.Text = _node.DeviceName;
            var ips = LanAddress.LocalIPv4();
            _address.Text = (ips.Count == 0 ? "sin red" : string.Join(", ", ips)) + $"  ·  puerto {_node.HttpPort}";
            var on = _node.SyncEnabled && _node.IsServerRunning;
            var error = _node.SyncEnabled ? _app.Controller.ServerError : null;
            _status.Text = on ? "Sincronización activada" : error is not null ? "No se puede recibir: " + error : "Sincronización pausada";
            _status.Foreground = on ? Brushes.SeaGreen : error is not null ? Brushes.IndianRed : Brushes.Gray;
            _sync.IsChecked = _node.SyncEnabled;
            _autostart.IsChecked = _node.StartWithWindows;
            _sendSensitive.IsChecked = _node.SendSensitive;
            _sendMode.Text = SendModeText();
            _lastReceived.Text = _node.LastReceived is { } r
                ? $"{DateTimeOffset.FromUnixTimeMilliseconds(r.AtMs).LocalDateTime:HH:mm:ss} · de {r.From}{(r.IsTest ? " · prueba" : "")}\n{(r.IsImage ? r.Preview() : $"«{r.Preview(300)}»")}"
                : "Nada todavía. Pulsa «Enviar prueba» en otro dispositivo.";
            ShowLastImage(_node.LastReceived);

            _discovered.Children.Clear();
            var found = _node.Discovered;
            if (found.Count == 0) _discovered.Children.Add(new TextBlock { Text = "Ninguno por ahora.", Foreground = Brushes.Gray });
            foreach (var d in found) _discovered.Children.Add(DiscoveredRow(d));

            _peers.Children.Clear();
            var peers = _node.Peers;
            if (peers.Count == 0) _peers.Children.Add(new TextBlock { Text = "Aún no hay dispositivos vinculados.", Foreground = Brushes.Gray });
            foreach (var p in peers) _peers.Children.Add(PeerRow(p));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowLastImage(ReceivedItem? item)
    {
        if (item?.Image is not { } bytes)
        {
            _lastImage.IsVisible = false;
            return;
        }
        if (ReferenceEquals(bytes, _shownImage)) return;
        try
        {
            _lastImage.Source = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes));
            _lastImage.IsVisible = true;
            _shownImage = bytes;
        }
        catch (Exception)
        {
            _lastImage.IsVisible = false; // not a decodable image
        }
    }

    private string SendModeText()
    {
        var c = _app.Controller;
        if (c.Clipboard is null) return "Sin acceso al portapapeles: " + c.ClipboardProblem;
        if (c.AutoSend) return $"Automático al copiar ({c.Clipboard.Name})";
        if (!_node.SyncEnabled) return "En pausa";
        return $"Manual: esta sesión ({DesktopSession.Desktop}, {DesktopSession.Kind}) no permite vigilar el portapapeles. " +
               "Usa «Enviar portapapeles ahora» o asigna un atajo de teclado al comando:  klip --send-clipboard";
    }

    private Control DiscoveredRow(DiscoveredDevice d)
    {
        var pair = new Button { Content = "Vincular", IsEnabled = d.PairingOpen };
        pair.Click += async (_, _) => await PairWith(d.DeviceName, d.Host, d.HttpPort);
        return Card(Row(
            new StackPanel
            {
                Width = 560,
                Children =
                {
                    new TextBlock { Text = d.DeviceName, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{PlatformLabel(d.Platform)} · {d.Host}:{d.HttpPort}" + (d.PairingOpen ? "" : " · abre su ventana de Klip para vincular"), Foreground = Brushes.Gray },
                },
            },
            pair));
    }

    private Control PeerRow(PeerView p)
    {
        var enabled = new CheckBox { IsChecked = p.Enabled, VerticalAlignment = VerticalAlignment.Center };
        enabled.IsCheckedChanged += (_, _) => { if (!_refreshing) _node.SetPeerEnabled(p.DeviceId, enabled.IsChecked == true); };
        var test = new Button { Content = "Enviar prueba" };
        test.Click += async (_, _) => await _app.SendTestAsync(p.DeviceId);
        var ping = new Button { Content = "Comprobar" };
        ping.Click += async (_, _) =>
        {
            SetActivity("Comprobando…");
            var r = await _node.PingAsync(p.DeviceId);
            SetActivity(r.Ok ? $"{p.Name}: conectado ({r.ElapsedMs} ms)" : $"{p.Name}: sin conexión ({r.Error})");
        };
        var unpair = new Button { Content = "Desvincular" };
        unpair.Click += async (_, _) =>
        {
            if (await ConfirmWindow.AskAsync(this, $"¿Desvincular «{p.Name}»?", $"Este equipo y {p.Name} dejarán de sincronizarse entre sí."))
                await _node.UnpairAsync(p.DeviceId);
        };
        var expel = new Button { Content = "Expulsar" };
        expel.Click += async (_, _) =>
        {
            if (await ConfirmWindow.AskAsync(this, $"¿Expulsar «{p.Name}» del grupo?", $"{p.Name} se eliminará de todos los dispositivos vinculados."))
                await _node.ExpelAsync(p.DeviceId);
        };
        var seen = p.LastSeenMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("dd/MM HH:mm:ss") : "nunca";
        return Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row(enabled, new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"{PlatformLabel(p.Platform)} · {p.Host}:{p.Port} · última conexión: {seen}", Foreground = Brushes.Gray },
                        new TextBlock { Text = p.LastStatus ?? "", Foreground = Brushes.SteelBlue },
                    },
                }),
                Row(test, ping, unpair, expel),
            },
        });
    }

    private static string PlatformLabel(string p) => p switch
    {
        "android" => "Android",
        "windows" => "Windows",
        "linux" => "Linux",
        "devpeer" => "DevPeer",
        _ => p,
    };

    private void SaveName()
    {
        var name = _name.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _node.DeviceName = name;
        SetActivity("Nombre guardado");
    }

    private async Task PairByIp()
    {
        var target = await PairByIpWindow.AskAsync(this);
        if (target is { } t) await PairWith(t.Host, t.Host, t.Port);
    }

    private async Task PairWith(string name, string host, int port)
    {
        if (_busy) return;
        if (!_node.IsServerRunning)
        {
            SetActivity("Activa la sincronización antes de vincular.");
            return;
        }
        _busy = true;
        try
        {
            SetActivity($"Contactando con {name}…");
            var (pairing, error) = await _node.StartPairingAsync(host, port);
            if (pairing is null)
            {
                SetActivity(error ?? "No se pudo contactar");
                return;
            }
            var pin = new PinWindow($"Vincular con {pairing.DeviceName}", pairing.Pin, $"Comprueba que {pairing.DeviceName} muestra el mismo código.");
            await pin.ShowDialog(this);
            if (!pin.Accepted)
            {
                SetActivity("");
                await _node.CancelPairingAsync(pairing);
                return;
            }
            SetActivity($"Esperando la confirmación en {pairing.DeviceName}…");
            var outcome = await _node.ConfirmPairingAsync(pairing);
            SetActivity(outcome.Ok ? $"Vinculado con {pairing.DeviceName}" : outcome.Message ?? "No se pudo vincular");
        }
        finally
        {
            _busy = false;
        }
    }
}
