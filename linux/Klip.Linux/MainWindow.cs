using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Klip.Linux.Platform;
using Klip.Node;
using static Klip.Linux.KlipTheme;

namespace Klip.Linux;

/// <summary>
/// Main window. Header with the device, its status and the sync switch; a "Dispositivos" tab with the last received
/// item, paired devices (each with its own actions) and nearby devices; and an "Ajustes" tab.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly App _app;
    private readonly SyncNode _node;

    // Header
    private readonly TextBlock _subtitle = Secondary();
    private readonly ContentControl _statusPill = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _sync = new() { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
    private readonly Button _sendNowHeader = Accented("Enviar portapapeles");
    private readonly Border _banner = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 4), IsVisible = false };
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap };

    // Devices tab
    private readonly ContentControl _lastReceived = new();
    private readonly StackPanel _peers = new() { Spacing = 8 };
    private readonly StackPanel _discovered = new() { Spacing = 8 };
    private readonly ContentControl _peerCount = new();
    private byte[]? _shownImage;
    private Avalonia.Media.Imaging.Bitmap? _shownBitmap;

    // Settings tab
    private readonly TextBox _name = new() { Width = 240, MaxLength = 64 };
    private readonly TextBlock _address = Secondary();
    private readonly TextBlock _sendMode = Secondary();
    private readonly ToggleSwitch _autostart = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly ToggleSwitch _sendSensitive = new() { OnContent = null, OffContent = null, MinWidth = 0 };

    // Toast
    private readonly Border _toast = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10), Margin = new Thickness(16, 0, 16, 14), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 640 };
    private readonly TextBlock _toastText = new() { TextWrapping = TextWrapping.Wrap };
    private DispatcherTimer? _toastTimer;

    private string? _bannerKey;
    private string? _lastReceivedKey;
    private string? _peersKey;
    private string? _discoveredKey;
    private bool _refreshing;
    private bool _busy;

    public MainWindow(App app)
    {
        _app = app;
        _node = app.Controller.Node;
        Title = "Klip";
        Icon = App.IconActive;
        Width = 780;
        Height = 720;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Res(BackgroundProperty, KlipTheme.Background);

        _sync.IsCheckedChanged += async (_, _) =>
        {
            if (_refreshing) return;
            await _app.Controller.SetSyncAsync(_sync.IsChecked == true);
        };
        _sendNowHeader.Click += async (_, _) => await SendNowAsync();
        _autostart.IsCheckedChanged += (_, _) =>
        {
            if (!_refreshing && (_autostart.IsChecked == true) != _node.StartWithWindows) _app.ToggleAutostart();
        };
        _sendSensitive.IsCheckedChanged += (_, _) =>
        {
            var enabled = _sendSensitive.IsChecked == true;
            if (!_refreshing && enabled != _node.SendSensitive) _node.SendSensitive = enabled;
        };
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveName(); };
        _name.LostFocus += (_, _) => SaveName();
        _banner.Child = _bannerText;
        _toast.Child = _toastText;
        _toast.Res(Border.BackgroundProperty, SurfaceAlt).Res(Border.BorderBrushProperty, Stroke);
        _toast.BorderThickness = new Thickness(1);

        var tabs = new TabControl
        {
            Padding = new Thickness(0),
            Margin = new Thickness(12, 0, 12, 0),
            Items =
            {
                new TabItem { Header = "Dispositivos", FontSize = 15, Content = Page(DevicesPage()) },
                new TabItem { Header = "Ajustes", FontSize = 15, Content = Page(SettingsPage()) },
            },
        };

        var root = new DockPanel();
        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var toastHost = new Panel { Children = { tabs, new Border { Child = _toast, VerticalAlignment = VerticalAlignment.Bottom } } };
        root.Children.Add(toastHost);
        Content = root;

        _node.PeersChanged += OnNodeChanged;
        _node.DiscoveredChanged += OnNodeChanged;
        _node.Received += _ => OnNodeChanged();
        RefreshAll();
    }

    // ---------- Layout ----------

    private Control Header()
    {
        var icon = new Image { Source = Icons.Render(active: true, size: 96), Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        var texts = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { new TextBlock { Text = "Klip", FontSize = 22, FontWeight = FontWeight.SemiBold }, _subtitle },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        _sendNowHeader.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(_sendNowHeader, 2);
        grid.Children.Add(_sendNowHeader);
        _statusPill.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_statusPill, 3);
        grid.Children.Add(_statusPill);
        Grid.SetColumn(_sync, 4);
        grid.Children.Add(_sync);
        ToolTip.SetTip(_sync, "Activar o pausar la sincronización");

        return new Border
        {
            Padding = new Thickness(24, 18, 20, 10),
            Child = new StackPanel { Spacing = 12, Children = { grid, _banner } },
        };
    }

    private static Control Page(Control content) => new ScrollViewer
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = new Border
        {
            Padding = new Thickness(12, 4, 12, 72), // room for the toast
            MaxWidth = 820,
            Child = content,
        },
    };

    private Control DevicesPage()
    {
        var testAll = Subtle("Enviar prueba a todos");
        testAll.Click += async (_, _) => await SendTestAsync(null);
        var pairByIp = Subtle("Vincular por IP…");
        pairByIp.Click += async (_, _) => await PairByIp();
        return new StackPanel
        {
            Children =
            {
                Section("Última recepción"),
                _lastReceived,
                Section("Mis dispositivos", testAll, _peerCount),
                _peers,
                Section("Cerca de ti", pairByIp),
                _discovered,
            },
        };
    }

    private Control SettingsPage()
    {
        var sendNow = new Button { Content = "Enviar portapapeles ahora", VerticalAlignment = VerticalAlignment.Center };
        sendNow.Click += async (_, _) => await SendNowAsync();
        var copyCommand = Subtle("Copiar comando");
        copyCommand.Click += async (_, _) =>
        {
            if (Clipboard is { } c)
            {
                await c.SetTextAsync("klip --send-clipboard");
                Toast("Comando copiado: klip --send-clipboard");
            }
        };
        return new StackPanel
        {
            Children =
            {
                Section("Este dispositivo"),
                Group(
                    SettingRow("Nombre", "Así te verán tus otros dispositivos.", _name),
                    SettingRow("Dirección en la red", null, _address)),
                Section("Comportamiento"),
                Group(
                    SettingRow("Iniciar con la sesión", "Klip se abre en segundo plano al entrar en el escritorio.", _autostart),
                    SettingRow("Enviar también contraseñas",
                        "Por defecto, lo que KeePassXC, Bitwarden, 1Password… marcan como sensible no sale de este equipo.", _sendSensitive)),
                Section("Envío del portapapeles"),
                Group(
                    SettingRow("Modo de envío", null, sendNow, _sendMode),
                    SettingRow("Atajo de teclado",
                        "Asigna este comando a un atajo en los ajustes de tu escritorio para enviar sin abrir la ventana:  klip --send-clipboard",
                        copyCommand)),
            },
        };
    }

    /// <summary>Several setting rows inside one card, separated by thin lines.</summary>
    private static Control Group(params Control[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 12) }.Res(Border.BackgroundProperty, Stroke));
            stack.Children.Add(rows[i]);
        }
        return Card(stack);
    }

    private static Control SettingRow(string title, string? description, Control control, TextBlock? descriptionBlock = null)
    {
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.Medium });
        if (descriptionBlock is not null) texts.Children.Add(descriptionBlock);
        else if (description is not null) texts.Children.Add(Secondary(description));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(texts);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // ---------- Refresh ----------

    private void OnNodeChanged() => Dispatcher.UIThread.Post(RefreshAll);

    public void SetActivity(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Toast(text);
    }

    private void Toast(string text)
    {
        _toastText.Text = text;
        _toast.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => { _toast.IsVisible = false; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    public void RefreshAll()
    {
        _refreshing = true;
        try
        {
            var c = _app.Controller;
            var ips = LanAddress.LocalIPv4();
            var address = (ips.Count == 0 ? "sin red" : string.Join(", ", ips)) + $" · puerto {_node.HttpPort}";
            _subtitle.Text = $"{_node.DeviceName} · {address}";
            _address.Text = address;
            if (!_name.IsFocused) _name.Text = _node.DeviceName;

            var on = _node.SyncEnabled && _node.IsServerRunning;
            var error = _node.SyncEnabled ? c.ServerError : null;
            _statusPill.Content = on ? Pill("Sincronizando", Success, SuccessSoft)
                : error is not null ? Pill("Error", Danger, DangerSoft)
                : Pill("En pausa", KlipTheme.Neutral, SurfaceAlt);
            _sync.IsChecked = _node.SyncEnabled;
            Icon = on ? App.IconActive : App.IconPaused;

            // Banner: only when something needs the user's attention.
            string? banner = null;
            if (error is not null) banner = "No se puede recibir: " + error;
            else if (!_node.SyncEnabled) banner = "La sincronización está en pausa: no se envía ni se recibe nada. Actívala con el interruptor.";
            else if (c.Clipboard is null) banner = "Sin acceso al portapapeles: " + c.ClipboardProblem;
            _banner.IsVisible = banner is not null;
            _bannerText.Text = banner;
            var bannerKey = error is not null || c.Clipboard is null ? DangerSoft : SurfaceAlt;
            if (bannerKey != _bannerKey) _banner.Res(Border.BackgroundProperty, _bannerKey = bannerKey);

            // Manual mode (GNOME on Wayland): the send button stays one click away in the header.
            _sendNowHeader.IsVisible = _node.SyncEnabled && c.Clipboard is not null && !c.AutoSend;

            _autostart.IsChecked = _node.StartWithWindows;
            _sendSensitive.IsChecked = _node.SendSensitive;
            _sendMode.Text = SendModeText();

            var r = _node.LastReceived;
            var receivedKey = r is null ? "" : $"{r.AtMs}|{r.From}|{r.IsTest}|{r.Content.Length}|{r.Image?.Length}";
            if (receivedKey != _lastReceivedKey)
            {
                _lastReceivedKey = receivedKey;
                _lastReceived.Content = LastReceivedCard(r);
            }

            // Cards are rebuilt only when something visible changed (also keeps an open "⋯" menu alive).
            var peers = _node.Peers;
            var peersKey = string.Join("\n", peers.Select(p => $"{p.DeviceId}|{p.Name}|{p.Platform}|{p.Host}:{p.Port}|{p.Enabled}|{(p.LastSeenMs is { } ms ? FormatSeen(ms) : "")}|{p.LastStatus}"));
            if (peersKey != _peersKey)
            {
                _peersKey = peersKey;
                RebuildPeers(peers);
            }

            var found = _node.Discovered;
            var discoveredKey = $"{on}\n" + string.Join("\n", found.Select(d => $"{d.DeviceId}|{d.DeviceName}|{d.Platform}|{d.Host}:{d.HttpPort}|{d.PairingOpen}"));
            if (discoveredKey != _discoveredKey)
            {
                _discoveredKey = discoveredKey;
                RebuildDiscovered(found, on);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RebuildPeers(IReadOnlyList<PeerView> peers)
    {
        _peerCount.Content = peers.Count > 0 ? Pill(peers.Count.ToString(), TextSecondary, SurfaceAlt, dot: false) : null;
        _peers.Children.Clear();
        if (peers.Count == 0)
        {
            _peers.Children.Add(EmptyState("Aún no hay dispositivos vinculados",
                "Abre Klip en otro dispositivo conectado a la misma red: aparecerá en «Cerca de ti». Pulsa Vincular y comprueba que ambos muestran el mismo PIN."));
        }
        foreach (var p in peers) _peers.Children.Add(PeerCard(p));
    }

    private void RebuildDiscovered(IReadOnlyList<DiscoveredDevice> found, bool on)
    {
        _discovered.Children.Clear();
        if (found.Count == 0)
        {
            _discovered.Children.Add(EmptyState(on ? "Buscando dispositivos…" : "El radar está parado",
                on ? "Abre Klip en el otro dispositivo (con su ventana visible). ¿No aparece? Prueba «Vincular por IP»."
                   : "Activa la sincronización para buscar dispositivos cercanos."));
        }
        foreach (var d in found) _discovered.Children.Add(DiscoveredCard(d));
    }

    private string SendModeText()
    {
        var c = _app.Controller;
        if (c.Clipboard is null) return "Sin acceso al portapapeles: " + c.ClipboardProblem;
        if (!_node.SyncEnabled) return "En pausa.";
        if (c.AutoSend) return $"Automático: lo que copies se envía solo ({c.Clipboard.Name}).";
        return $"Manual: esta sesión ({DesktopSession.Desktop}, {DesktopSession.Kind}) no deja vigilar el portapapeles. " +
               "Usa este botón, el del menú de la bandeja o un atajo de teclado.";
    }

    private static Control EmptyState(string title, string text) =>
        new Border
        {
            Padding = new Thickness(18, 16),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 4, Children = { Title(title, 13.5), Secondary(text) } },
        }.Res(Border.BorderBrushProperty, Stroke);

    private Control LastReceivedCard(ReceivedItem? r)
    {
        if (r is null) return EmptyState("Nada todavía", "Copia algo en otro dispositivo vinculado, o pulsa «Enviar prueba» en él.");

        var when = DateTimeOffset.FromUnixTimeMilliseconds(r.AtMs).LocalDateTime;
        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Title(r.From, 13.5), Secondary($"{when:HH:mm:ss}") },
        };
        if (r.IsTest) meta.Children.Add(Pill("prueba", Accent, AccentSoft, dot: false));

        var body = new StackPanel { Spacing = 8, Children = { meta } };
        if (r.Image is { } bytes)
        {
            if (!ReferenceEquals(bytes, _shownImage))
            {
                _shownImage = bytes;
                try { _shownBitmap = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes)); }
                catch (Exception) { _shownBitmap = null; } // not a decodable image
            }
            if (_shownBitmap is not null)
            {
                body.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image { Source = _shownBitmap, MaxWidth = 320, MaxHeight = 180, Stretch = Stretch.Uniform },
                });
            }
            body.Children.Add(Secondary(r.Preview()));
        }
        else
        {
            body.Children.Add(new Border
            {
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(6),
                Child = new SelectableTextBlock { Text = r.Preview(400), TextWrapping = TextWrapping.Wrap, FontSize = 13.5 },
            }.Res(Border.BackgroundProperty, SurfaceAlt));
        }
        return Card(body);
    }

    private Control PeerCard(PeerView p)
    {
        var seen = p.LastSeenMs is { } ms ? FormatSeen(ms) : "nunca conectado";
        var (statusBrush, statusText) = p.LastStatus switch
        {
            null => (KlipTheme.Neutral, "Sin comprobar"),
            var s when s.StartsWith("Enviado", StringComparison.Ordinal) || s.StartsWith("Conectado", StringComparison.Ordinal) => (Success, s),
            var s => (Danger, s),
        };

        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 12, 0),
            Children =
            {
                Title(p.Name),
                Secondary($"{PlatformLabel(p.Platform)} · {p.Host}:{p.Port} · {seen}"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(0, 2, 0, 0),
                    Children = { Dot(statusBrush), Secondary(statusText) },
                },
            },
        };

        var enabled = new ToggleSwitch { IsChecked = p.Enabled, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        enabled.IsCheckedChanged += (_, _) => { if (!_refreshing) _node.SetPeerEnabled(p.DeviceId, enabled.IsChecked == true); };
        ToolTip.SetTip(enabled, "Recibe lo que copias en este equipo");
        var receives = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Secondary("Recibe copias"), enabled },
        };
        ((TextBlock)receives.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        ((TextBlock)receives.Children[0]).Margin = new Thickness(0, 0, 8, 0);

        var test = new Button { Content = "Enviar prueba", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        test.Click += async (_, _) => await SendTestAsync(p.DeviceId);

        var ping = new MenuItem { Header = "Comprobar conexión" };
        ping.Click += async (_, _) =>
        {
            Toast($"Comprobando {p.Name}…");
            var r = await _node.PingAsync(p.DeviceId);
            Toast(r.Ok ? $"{p.Name}: conectado ({r.ElapsedMs} ms)" : $"{p.Name}: sin conexión ({r.Error})");
        };
        var unpair = new MenuItem { Header = "Desvincular…" };
        unpair.Click += async (_, _) =>
        {
            if (await ConfirmWindow.AskAsync(this, $"¿Desvincular «{p.Name}»?", $"Este equipo y {p.Name} dejarán de sincronizarse entre sí.", "Desvincular"))
                await _node.UnpairAsync(p.DeviceId);
        };
        var expel = new MenuItem { Header = "Expulsar del grupo…" };
        expel.Click += async (_, _) =>
        {
            if (await ConfirmWindow.AskAsync(this, $"¿Expulsar «{p.Name}» del grupo?", $"{p.Name} se eliminará de todos los dispositivos vinculados.", "Expulsar"))
                await _node.ExpelAsync(p.DeviceId);
        };
        var more = Subtle("⋯");
        more.FontSize = 18;
        more.Padding = new Thickness(10, 0, 10, 4);
        ToolTip.SetTip(more, "Más opciones");
        more.Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, Items = { ping, new Separator(), unpair, expel } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        var icon = PlatformIcon(p.Platform);
        if (!p.Enabled) icon.Opacity = 0.5;
        grid.Children.Add(icon);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        Grid.SetColumn(receives, 2);
        grid.Children.Add(receives);
        Grid.SetColumn(test, 3);
        grid.Children.Add(test);
        Grid.SetColumn(more, 4);
        grid.Children.Add(more);
        return Card(grid);
    }

    private Control DiscoveredCard(DiscoveredDevice d)
    {
        var pair = d.PairingOpen ? Accented("Vincular") : new Button { Content = "Vincular", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        pair.Click += async (_, _) => await PairWith(d.DeviceName, d.Host, d.HttpPort);
        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 12, 0),
            Children =
            {
                Title(d.DeviceName),
                Secondary($"{PlatformLabel(d.Platform)} · {d.Host}:{d.HttpPort}" + (d.PairingOpen ? "" : " · abre su ventana de Klip para vincular")),
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(PlatformIcon(d.Platform));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        Grid.SetColumn(pair, 2);
        grid.Children.Add(pair);
        return Card(grid);
    }

    private static string FormatSeen(long ms)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var ago = DateTime.Now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "visto ahora";
        if (ago < TimeSpan.FromHours(1)) return $"visto hace {(int)ago.TotalMinutes} min";
        if (at.Date == DateTime.Today) return $"visto a las {at:HH:mm}";
        return $"visto el {at:dd/MM HH:mm}";
    }

    // ---------- Actions ----------

    private async Task SendNowAsync() => Toast(await _app.Controller.SendCurrentAsync(manual: false));

    private async Task SendTestAsync(string? deviceId) => await _app.SendTestAsync(deviceId);

    private void SaveName()
    {
        var name = _name.Text?.Trim();
        if (string.IsNullOrEmpty(name) || name == _node.DeviceName) return;
        _node.DeviceName = name;
        Toast("Nombre guardado");
        RefreshAll();
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
            Toast("Activa la sincronización antes de vincular.");
            return;
        }
        _busy = true;
        try
        {
            Toast($"Contactando con {name}…");
            var (pairing, error) = await _node.StartPairingAsync(host, port);
            if (pairing is null)
            {
                Toast(error ?? "No se pudo contactar");
                return;
            }
            var pin = new PinWindow($"Vincular con {pairing.DeviceName}", pairing.Pin, $"Comprueba que {pairing.DeviceName} muestra el mismo código.");
            await pin.ShowDialog(this);
            if (!pin.Accepted)
            {
                _toast.IsVisible = false;
                await _node.CancelPairingAsync(pairing);
                return;
            }
            Toast($"Esperando la confirmación en {pairing.DeviceName}…");
            var outcome = await _node.ConfirmPairingAsync(pairing);
            Toast(outcome.Ok ? $"Vinculado con {pairing.DeviceName}" : outcome.Message ?? "No se pudo vincular");
        }
        finally
        {
            _busy = false;
        }
    }
}
