using Klip.Core;
using Klip.Node;
using static Klip.Windows.Ui;

namespace Klip.Windows;

/// <summary>
/// Main window (plan §4.1). While visible: pairing window open and UDP radar running.
/// Header with the device, its status and the sync switch; a "Dispositivos" tab with the last received item,
/// paired devices (each with its own actions) and nearby devices; and an "Ajustes" tab.
/// </summary>
internal sealed class ConfigWindow : Form
{
    private readonly TrayApp _app;
    private readonly SyncNode _node;
    private readonly Palette _p = Palette.Current();

    // Header
    private readonly Label _subtitle;
    private readonly Pill _status = new();
    private readonly ToggleSwitch _sync;
    private readonly Card _banner;
    private readonly Label _bannerText;

    // Devices tab
    private readonly Page _devices;
    private readonly Row _lastReceivedHost = new(Row.Fill);
    private readonly Row _peers = new(Row.Fill);
    private readonly Row _discovered = new(Row.Fill);
    private string? _lastReceivedKey;
    private string? _peersKey;
    private string? _discoveredKey;
    private readonly Pill _peerCount = new();
    private byte[]? _shownImage;
    private Image? _shownBitmap;

    // Settings tab
    private readonly Page _settings;
    private readonly TextBox _name;
    private readonly Label _address;
    private readonly ToggleSwitch _autostart;
    private readonly ToggleSwitch _sendSensitive;

    // Toast
    private readonly Card _toast;
    private readonly Label _toastText;
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 5000 };

    private bool _busy;

    public ConfigWindow(TrayApp app)
    {
        _app = app;
        _node = app.Node;
        Text = "Klip";
        Icon = AppIcon.Main;
        Font = Body;
        BackColor = _p.Background;
        ForeColor = _p.Text;
        AutoScaleMode = AutoScaleMode.None; // sizes are scaled by hand with S()
        ClientSize = new Size(this.S(800), this.S(720));
        MinimumSize = new Size(this.S(640), this.S(520));
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ApplyTitleBar(this, _p);

        _subtitle = Lbl("", Small, _p.TextSecondary);
        _sync = new ToggleSwitch(_p) { Anchor = AnchorStyles.None };
        _sync.CheckedChanged += async (_, _) => await _app.SetSyncAsync(_sync.Checked);
        _bannerText = Lbl("", Body, _p.Text, wrap: true);
        _banner = new AutoCard(new Row(Row.Fill).Add(_bannerText, 0), _p.SurfaceAlt, Color.Empty, new Padding(this.S(14), this.S(10), this.S(14), this.S(10)))
        {
            Dock = DockStyle.Top,
            Visible = false,
        };

        _name = new TextBox
        {
            Width = this.S(240),
            MaxLength = 64,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _p.Dark ? _p.SurfaceAlt : _p.Surface,
            ForeColor = _p.Text,
            Font = Body,
            Anchor = AnchorStyles.Right,
        };
        _name.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SaveName();
        };
        _name.Leave += (_, _) => SaveName();
        _address = Lbl("", Small, _p.TextSecondary);
        _address.Anchor = AnchorStyles.Right;
        _autostart = new ToggleSwitch(_p);
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_autostart.Checked != _node.StartWithWindows) _app.ToggleAutostart();
            RefreshAll();
        };
        _sendSensitive = new ToggleSwitch(_p);
        _sendSensitive.CheckedChanged += (_, _) =>
        {
            if (_sendSensitive.Checked != _node.SendSensitive) _node.SendSensitive = _sendSensitive.Checked;
        };

        _toastText = Lbl("", Body, _p.Text, wrap: true);
        _toast = new AutoCard(new Row(Row.Fill).Add(_toastText, 0), _p.SurfaceAlt, _p.Stroke, new Padding(this.S(14), this.S(10), this.S(14), this.S(10)))
        {
            Visible = false,
            Anchor = AnchorStyles.Bottom,
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visible = false; };

        _devices = new Page(_p.Background);
        _settings = new Page(_p.Background) { Visible = false };
        BuildDevicesPage();
        BuildSettingsPage();

        var tabs = new TabStrip(_p, "Dispositivos", "Ajustes");
        tabs.SelectedChanged += (_, _) =>
        {
            _devices.Visible = tabs.Selected == 0;
            _settings.Visible = tabs.Selected == 1;
        };

        var content = new Panel { Dock = DockStyle.Fill, BackColor = _p.Background };
        content.Controls.Add(_toast);
        content.Controls.Add(_settings);
        content.Controls.Add(_devices);
        content.Resize += (_, _) => PlaceToast(content);
        _toast.SizeChanged += (_, _) => PlaceToast(content);
        _toast.BringToFront();

        // Docking order: last added docks first.
        Controls.Add(content);
        Controls.Add(tabs);
        Controls.Add(Header());

        _node.PeersChanged += OnNodeChanged;
        _node.Received += OnReceived;
        _node.DiscoveredChanged += OnNodeChanged;
        Shown += (_, _) =>
        {
            // Pairing requests and the radar only while this window is visible (plan §5.1).
            _node.PairingOpen = true;
            _node.StartDiscovery();
        };
        FormClosed += (_, _) =>
        {
            _node.PairingOpen = false;
            _node.StopDiscovery();
            _node.PeersChanged -= OnNodeChanged;
            _node.Received -= OnReceived;
            _node.DiscoveredChanged -= OnNodeChanged;
            _toastTimer.Dispose();
            _shownBitmap?.Dispose();
        };
        RefreshAll();
    }

    // ---------- Layout ----------

    private Control Header()
    {
        var icon = new PictureBox
        {
            Image = new Icon(AppIcon.Main, this.S(48), this.S(48)).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(this.S(44), this.S(44)),
            Margin = new Padding(0, 0, this.S(14), 0),
            Anchor = AnchorStyles.Left,
        };
        var texts = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        texts.Controls.Add(Lbl("Klip", TitleFont, _p.Text));
        texts.Controls.Add(_subtitle);
        _status.Margin = new Padding(0, 0, this.S(12), 0);
        _status.Anchor = AnchorStyles.Right;
        var top = new Row(Row.Auto, Row.Fill, Row.Auto, Row.Auto) { Dock = DockStyle.Top };
        top.Add(icon, 0).Add(texts, 1).Add(_status, 2).Add(_sync, 3);
        new ToolTip().SetToolTip(_sync, "Activar o pausar la sincronización");

        var spacer = new Panel { Dock = DockStyle.Top, Height = this.S(12), BackColor = Color.Transparent };
        var header = new Panel
        {
            Dock = DockStyle.Top,
            BackColor = _p.Background,
            Padding = new Padding(this.S(24), this.S(18), this.S(22), this.S(4)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        header.Controls.Add(_banner);
        header.Controls.Add(spacer);
        header.Controls.Add(top);
        return header;
    }

    /// <summary>
    /// Page-level block: takes the page width and the height of its (auto-sized) content. Auto-sized containers
    /// placed directly on the page would shrink their width to the content instead.
    /// </summary>
    private Control Block(Control content) => new AutoCard(content, _p.Background, Color.Empty, Padding.Empty) { Margin = new Padding(0) };

    /// <summary>Replaces the cards of a vertical stack (one per row, stretched to the stack width).</summary>
    private static void SetChildren(Row stack, IEnumerable<Control> children)
    {
        stack.SuspendLayout();
        stack.Controls.ClearAndDispose();
        stack.RowStyles.Clear();
        stack.RowCount = 0;
        var i = 0;
        foreach (var c in children)
        {
            c.Dock = DockStyle.Fill;
            stack.Add(c, 0, i++);
        }
        stack.ResumeLayout(true);
    }

    private Control Section(string title, Control? trailing = null, Control? badge = null)
    {
        var row = new Row(Row.Auto, Row.Auto, Row.Fill, Row.Auto) { Padding = new Padding(this.S(2), this.S(18), 0, this.S(8)) };
        row.Add(Lbl(title, SectionFont, _p.Text), 0);
        if (badge is not null)
        {
            badge.Margin = new Padding(this.S(8), 0, 0, 0);
            row.Add(badge, 1);
        }
        if (trailing is not null) row.Add(trailing, 3);
        return Block(row);
    }

    private Card NewCard(Control content) =>
        new AutoCard(content, _p.Surface, _p.Stroke, new Padding(this.S(16), this.S(14), this.S(16), this.S(14))) { Margin = new Padding(0, 0, 0, this.S(8)) };

    private Control EmptyState(string title, string text)
    {
        var rows = new Row(Row.Fill);
        rows.Add(Lbl(title, Strong, _p.Text), 0, 0);
        var t = Lbl(text, Small, _p.TextSecondary, wrap: true);
        t.Margin = new Padding(0, this.S(3), 0, 0);
        rows.Add(t, 0, 1);
        var card = new AutoCard(rows, _p.Background, _p.Stroke, new Padding(this.S(18), this.S(16), this.S(18), this.S(16))) { Margin = new Padding(0, 0, 0, this.S(8)) };
        return card;
    }

    private void BuildDevicesPage()
    {
        var testAll = Btn(_p, "Enviar prueba a todos", ButtonKind.Subtle, _p.Background);
        testAll.Click += async (_, _) => await _app.SendTestAsync(null);
        var pairByIp = Btn(_p, "Vincular por IP…", ButtonKind.Subtle, _p.Background);
        pairByIp.Click += async (_, _) => await PairByIp();

        _devices.Controls.Add(Section("Última recepción"));
        _devices.Controls.Add(Block(_lastReceivedHost));
        _devices.Controls.Add(Section("Mis dispositivos", testAll, _peerCount));
        _devices.Controls.Add(Block(_peers));
        _devices.Controls.Add(Section("Cerca de ti", pairByIp));
        _devices.Controls.Add(Block(_discovered));
        _devices.Controls.Add(new Panel { Height = this.S(60), Margin = new Padding(0) }); // room for the toast
    }

    private void BuildSettingsPage()
    {
        var firewall = Btn(_p, "Configurar…", ButtonKind.Standard, _p.Surface);
        firewall.Click += (_, _) => _app.ConfigureFirewall(askFirst: false);
        var logs = Btn(_p, "Abrir carpeta", ButtonKind.Standard, _p.Surface);
        logs.Click += (_, _) => TrayApp.OpenLogs();
        var manual = Btn(_p, "Añadir…", ButtonKind.Standard, _p.Surface);
        manual.Click += (_, _) => AddManual();

        _settings.Controls.Add(Section("Este dispositivo"));
        _settings.Controls.Add(Group(
            SettingRow("Nombre", "Así te verán tus otros dispositivos.", _name),
            SettingRow("Dirección en la red", null, _address)));
        _settings.Controls.Add(Section("Comportamiento"));
        _settings.Controls.Add(Group(
            SettingRow("Iniciar con Windows", "Klip se abre en la bandeja del sistema al iniciar sesión.", _autostart),
            SettingRow("Enviar también contraseñas",
                "Por defecto, lo que KeePass, Bitwarden, 1Password… marcan como sensible no sale de este equipo.", _sendSensitive)));
        _settings.Controls.Add(Section("Sistema"));
        _settings.Controls.Add(Group(
            SettingRow("Firewall de Windows", "Reglas de entrada TCP 8765 y UDP 8766, solo en redes privadas.", firewall),
            SettingRow("Registros", "Para diagnosticar problemas de conexión.", logs)));
        _settings.Controls.Add(Section("Avanzado"));
        _settings.Controls.Add(Group(
            SettingRow("Añadir dispositivo manualmente", "Con un secreto compartido en lugar del PIN. Solo para pruebas.", manual)));
    }

    /// <summary>Several setting rows inside one card, separated by thin lines.</summary>
    private Control Group(params Control[] rows)
    {
        var stack = new Row(Row.Fill);
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                stack.Add(new Panel { Height = 1, Dock = DockStyle.Fill, BackColor = _p.Stroke, Margin = new Padding(0, this.S(12), 0, this.S(12)) }, 0, i * 2 - 1);
            }
            rows[i].Dock = DockStyle.Fill;
            stack.Add(rows[i], 0, i * 2);
        }
        return Block(NewCard(stack));
    }

    private Control SettingRow(string title, string? description, Control control)
    {
        var row = new Row(Row.Fill, Row.Auto);
        row.Add(Lbl(title, Body, _p.Text), 0, 0);
        if (description is not null)
        {
            var d = Lbl(description, Small, _p.TextSecondary, wrap: true);
            d.Margin = new Padding(0, this.S(2), this.S(16), 0);
            row.Add(d, 0, 1);
        }
        control.Anchor = AnchorStyles.Right;
        control.Margin = new Padding(this.S(12), 0, 0, 0);
        row.Add(control, 1, 0);
        if (description is not null) row.SetRowSpan(control, 2);
        return row;
    }

    private void PlaceToast(Control host)
    {
        var width = Math.Min(this.S(620), host.ClientSize.Width - this.S(32));
        _toast.Width = width;
        _toast.Location = new Point((host.ClientSize.Width - width) / 2, host.ClientSize.Height - _toast.Height - this.S(14));
    }

    // ---------- Refresh ----------

    private void OnReceived(ReceivedItem _) => OnNodeChanged();

    private void OnNodeChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshAll);
    }

    private void Toast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _toastText.Text = text;
        _toast.Visible = true;
        _toast.BringToFront();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void RefreshAll()
    {
        if (IsDisposed) return;
        SuspendLayout();
        try
        {
            var ips = NetworkInfo.LocalIPv4();
            var address = (ips.Count == 0 ? "sin red" : string.Join(", ", ips)) + $" · puerto {_node.HttpPort}";
            _subtitle.Text = $"{_node.DeviceName} · {address}";
            _address.Text = address;
            if (!_name.Focused) _name.Text = _node.DeviceName;

            var on = _node.SyncEnabled && _node.IsServerRunning;
            if (on) _status.Set("Sincronizando", _p.Success, _p.SuccessSoft);
            else _status.Set("En pausa", _p.Neutral, _p.SurfaceAlt);
            _sync.SetSilently(_node.SyncEnabled);
            _autostart.SetSilently(_node.StartWithWindows);
            _sendSensitive.SetSilently(_node.SendSensitive);

            _banner.Visible = !_node.SyncEnabled;
            _bannerText.Text = "La sincronización está en pausa: no se envía ni se recibe nada. Actívala con el interruptor.";

            RefreshLastReceived();
            RefreshPeers();
            RefreshDiscovered(on);
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    private void RefreshLastReceived()
    {
        var r = _node.LastReceived;
        var key = r is null ? "" : $"{r.AtMs}|{r.From}|{r.IsTest}|{r.Content.Length}|{r.Image?.Length}";
        if (key == _lastReceivedKey) return;
        _lastReceivedKey = key;
        Control card;
        if (r is null)
        {
            card = EmptyState("Nada todavía", "Copia algo en otro dispositivo vinculado, o pulsa «Enviar prueba» en él.");
        }
        else
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(r.AtMs).LocalDateTime;
            var meta = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, this.S(8)), BackColor = Color.Transparent };
            meta.Controls.Add(Lbl(r.From, Strong, _p.Text));
            var time = Lbl($"{when:HH:mm:ss}", Small, _p.TextSecondary);
            time.Margin = new Padding(this.S(8), this.S(1), 0, 0);
            meta.Controls.Add(time);
            if (r.IsTest)
            {
                var tag = new Pill { Margin = new Padding(this.S(8), 0, 0, 0) };
                tag.Set("prueba", _p.Accent, _p.AccentSoft, dot: false);
                meta.Controls.Add(tag);
            }

            var body = new Row(Row.Fill);
            body.Add(meta, 0, 0);
            if (r.Image is { } bytes)
            {
                if (!ReferenceEquals(bytes, _shownImage))
                {
                    _shownImage = bytes;
                    _shownBitmap?.Dispose();
                    try
                    {
                        using var source = Image.FromStream(new MemoryStream(bytes));
                        _shownBitmap = new Bitmap(source);
                    }
                    catch (ArgumentException)
                    {
                        _shownBitmap = null; // not a decodable image
                    }
                }
                if (_shownBitmap is not null)
                {
                    var scale = Math.Min(1.0, Math.Min(this.S(320) / (double)_shownBitmap.Width, this.S(180) / (double)_shownBitmap.Height));
                    body.Add(new PictureBox
                    {
                        Image = _shownBitmap,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(Math.Max(1, (int)(_shownBitmap.Width * scale)), Math.Max(1, (int)(_shownBitmap.Height * scale))),
                        Margin = new Padding(0, 0, 0, this.S(6)),
                        Anchor = AnchorStyles.Left,
                    }, 0, 1);
                }
                body.Add(Lbl(r.Preview(), Small, _p.TextSecondary), 0, 2);
            }
            else
            {
                var text = new TextBox
                {
                    Text = r.Preview(400),
                    ReadOnly = true,
                    Multiline = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = _p.SurfaceAlt,
                    ForeColor = _p.Text,
                    Font = Body,
                    Dock = DockStyle.Fill,
                    TabStop = false,
                    WordWrap = true,
                };
                var box = new Card(_p.SurfaceAlt, Color.Empty, 6)
                {
                    Padding = new Padding(this.S(12), this.S(9), this.S(12), this.S(9)),
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0),
                };
                box.Controls.Add(text);
                // Height for the wrapped text (at most a few lines; the box scrolls beyond that).
                box.SizeChanged += (_, _) =>
                {
                    var inner = Math.Max(50, box.Width - box.Padding.Horizontal);
                    var measured = TextRenderer.MeasureText(text.Text, Body, new Size(inner, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    var h = Math.Min(measured.Height + this.S(4), this.S(120)) + box.Padding.Vertical;
                    if (box.Height != h) box.Height = h;
                    text.ScrollBars = measured.Height + this.S(4) > this.S(120) ? ScrollBars.Vertical : ScrollBars.None;
                };
                box.Height = this.S(44);
                body.Add(box, 0, 1);
            }
            card = NewCard(body);
        }
        SetChildren(_lastReceivedHost, [card]);
    }

    private void RefreshPeers()
    {
        var peers = _node.Peers;
        _peerCount.Visible = peers.Count > 0;
        _peerCount.Set(peers.Count.ToString(), _p.TextSecondary, _p.SurfaceAlt, dot: false);
        // Rebuild only when something visible changed (also keeps an open "•••" menu alive).
        var key = string.Join("\n", peers.Select(p => $"{p.DeviceId}|{p.Name}|{p.Platform}|{p.Host}:{p.Port}|{p.Enabled}|{(p.LastSeenMs is { } ms ? FormatSeen(ms) : "")}|{p.LastStatus}"));
        if (key == _peersKey) return;
        _peersKey = key;
        SetChildren(_peers, peers.Count == 0
            ? [EmptyState("Aún no hay dispositivos vinculados",
                "Abre Klip en otro dispositivo conectado a la misma red: aparecerá en «Cerca de ti». Pulsa Vincular y comprueba que ambos muestran el mismo PIN.")]
            : peers.Select(PeerCard));
    }

    private void RefreshDiscovered(bool on)
    {
        var found = _node.Discovered;
        var key = $"{on}\n" + string.Join("\n", found.Select(d => $"{d.DeviceId}|{d.DeviceName}|{d.Platform}|{d.Host}:{d.HttpPort}|{d.PairingOpen}"));
        if (key == _discoveredKey) return;
        _discoveredKey = key;
        SetChildren(_discovered, found.Count == 0
            ? [on
                ? EmptyState("Buscando dispositivos…", "Abre Klip en el otro dispositivo (con su ventana visible). ¿No aparece? Prueba «Vincular por IP».")
                : EmptyState("El radar está parado", "Activa la sincronización para buscar dispositivos cercanos.")]
            : found.Select(DiscoveredCard));
    }

    private Control PeerCard(PeerView p)
    {
        var seen = p.LastSeenMs is { } ms ? FormatSeen(ms) : "nunca conectado";
        var (statusColor, statusText) = p.LastStatus switch
        {
            null => (_p.Neutral, "Sin comprobar"),
            var s when s.StartsWith("Enviado", StringComparison.Ordinal) || s.StartsWith("Conectado", StringComparison.Ordinal) => (_p.Success, s),
            var s => (_p.Danger, s),
        };

        var info = new Row(Row.Fill) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, this.S(12), 0) };
        var name = Lbl(p.Name, Strong, _p.Text);
        name.AutoEllipsis = true;
        info.Add(name, 0, 0);
        info.Add(Lbl($"{PlatformLabel(p.Platform)} · {p.Host}:{p.Port} · {seen}", Small, _p.TextSecondary), 0, 1);
        var status = new Label
        {
            Text = "●  " + statusText,
            AutoSize = true,
            Font = Small,
            ForeColor = statusColor,
            Margin = new Padding(0, this.S(2), 0, 0),
            UseMnemonic = false,
            BackColor = Color.Transparent,
        };
        info.Add(status, 0, 2);

        var receivesLabel = Lbl("Recibe copias", Small, _p.TextSecondary);
        receivesLabel.Anchor = AnchorStyles.Right;
        receivesLabel.Margin = new Padding(0, 0, this.S(8), 0);
        var enabled = new ToggleSwitch(_p) { Anchor = AnchorStyles.None };
        enabled.SetSilently(p.Enabled);
        enabled.CheckedChanged += (_, _) => _node.SetPeerEnabled(p.DeviceId, enabled.Checked);
        var tips = new ToolTip();
        tips.SetToolTip(enabled, "Recibe lo que copias en este equipo");

        var test = Btn(_p, "Enviar prueba", ButtonKind.Standard, _p.Surface);
        test.Margin = new Padding(this.S(14), 0, this.S(4), 0);
        test.Click += async (_, _) => await _app.SendTestAsync(p.DeviceId);

        var more = Btn(_p, "•••", ButtonKind.Subtle, _p.Surface);
        more.Padding = new Padding(this.S(6), this.S(2), this.S(6), this.S(2));
        tips.SetToolTip(more, "Más opciones");
        var menu = MenuFor(_p);
        menu.Items.Add("Comprobar conexión", null, async (_, _) =>
        {
            Toast($"Comprobando {p.Name}…");
            var r = await _node.PingAsync(p.DeviceId);
            Toast(r.Ok ? $"{p.Name}: conectado ({r.ElapsedMs} ms)" : $"{p.Name}: sin conexión ({r.Error})");
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Desvincular…", null, async (_, _) =>
        {
            if (Confirm($"¿Desvincular «{p.Name}»?", $"Este equipo y {p.Name} dejarán de sincronizarse entre sí.", "Desvincular"))
                await _node.UnpairAsync(p.DeviceId);
        });
        menu.Items.Add("Expulsar del grupo…", null, async (_, _) =>
        {
            if (Confirm($"¿Expulsar «{p.Name}» del grupo?", $"{p.Name} se eliminará de todos los dispositivos vinculados.", "Expulsar"))
                await _node.ExpelAsync(p.DeviceId);
        });
        foreach (ToolStripItem item in menu.Items) item.ForeColor = _p.Text;
        more.Click += (_, _) => menu.Show(more, new Point(more.Width - menu.PreferredSize.Width, more.Height));

        var row = new Row(Row.Auto, Row.Fill, Row.Auto, Row.Auto, Row.Auto, Row.Auto);
        row.Add(new PlatformIcon(_p, p.Platform, dimmed: !p.Enabled), 0)
           .Add(info, 1)
           .Add(receivesLabel, 2)
           .Add(enabled, 3)
           .Add(test, 4)
           .Add(more, 5);
        var card = NewCard(row);
        card.Disposed += (_, _) => { menu.Dispose(); tips.Dispose(); };
        return card;
    }

    private Control DiscoveredCard(DiscoveredDevice d)
    {
        var pair = Btn(_p, "Vincular", d.PairingOpen ? ButtonKind.Accent : ButtonKind.Standard, _p.Surface);
        pair.Enabled = d.PairingOpen;
        pair.Click += async (_, _) => await PairWith(d.DeviceName, d.Host, d.HttpPort);
        var info = new Row(Row.Fill) { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        info.Add(Lbl(d.DeviceName, Strong, _p.Text), 0, 0);
        info.Add(Lbl($"{PlatformLabel(d.Platform)} · {d.Host}:{d.HttpPort}" + (d.PairingOpen ? "" : " · abre su ventana de Klip para vincular"), Small, _p.TextSecondary), 0, 1);
        var row = new Row(Row.Auto, Row.Fill, Row.Auto);
        row.Add(new PlatformIcon(_p, d.Platform), 0).Add(info, 1).Add(pair, 2);
        return NewCard(row);
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

    private static string PlatformLabel(string p) => p switch
    {
        "android" => "Android",
        "windows" => "Windows",
        "linux" => "Linux",
        "devpeer" => "DevPeer",
        _ => p,
    };

    private bool Confirm(string title, string text, string confirm)
    {
        using var dialog = new MessageDialog(_p, title, text, confirm, "Cancelar");
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private void Inform(string title, string text)
    {
        using var dialog = new MessageDialog(_p, title, text, "Aceptar", null);
        dialog.ShowDialog(this);
    }

    private void SaveName()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || name == _node.DeviceName) return;
        _node.DeviceName = name;
        Toast("Nombre guardado");
        RefreshAll();
    }

    // ---------- Pairing (initiator) ----------

    private async Task PairByIp()
    {
        using var dialog = new PairByIpDialog(_p);
        if (dialog.ShowDialog(this) == DialogResult.OK) await PairWith(dialog.Host, dialog.Host, dialog.Port);
    }

    private async Task PairWith(string name, string host, int port)
    {
        if (_busy) return;
        if (!_node.IsServerRunning)
        {
            Inform("La sincronización está en pausa", "Actívala con el interruptor de arriba antes de vincular.");
            return;
        }
        _busy = true;
        try
        {
            Toast($"Contactando con {name}…");
            var (pairing, error) = await _node.StartPairingAsync(host, port);
            if (pairing is null)
            {
                _toast.Visible = false;
                Inform("No se pudo vincular", error ?? "No se pudo contactar");
                return;
            }

            using var pin = new PinDialog($"Vincular con {pairing.DeviceName}", pairing.Pin, $"Comprueba que {pairing.DeviceName} muestra el mismo código.");
            pin.ShowDialog(this);
            if (!pin.Accepted)
            {
                _toast.Visible = false;
                await _node.CancelPairingAsync(pairing);
                return;
            }

            Toast($"Esperando la confirmación en {pairing.DeviceName}…");
            var outcome = await _node.ConfirmPairingAsync(pairing);
            if (outcome.Ok) Toast($"Vinculado con {pairing.DeviceName}");
            else
            {
                _toast.Visible = false;
                Inform("No se pudo vincular", outcome.Message ?? "No se pudo vincular");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void AddManual()
    {
        using var dialog = new AddPeerDialog(_p, _node.DeviceId);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _node.AddOrUpdatePeer(dialog.PeerId, dialog.PeerName, dialog.Host, dialog.Port, dialog.Secret, dialog.Platform);
            Toast($"{dialog.PeerName} añadido");
        }
        catch (ArgumentException e)
        {
            Inform("No se pudo añadir", e.Message);
        }
    }
}

/// <summary>Common look of Klip's small windows: palette, fixed border, auto size, dark title bar.</summary>
internal abstract class KlipDialog : Form
{
    protected readonly Palette P;

    protected KlipDialog(Palette p, string title)
    {
        P = p;
        Text = title;
        Icon = AppIcon.Main;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = p.Background;
        ForeColor = p.Text;
        Font = Body;
        ShowInTaskbar = false;
        ApplyTitleBar(this, p);
    }

    protected Row NewLayout() => new(Row.Fill) { Padding = new Padding(this.S(24), this.S(20), this.S(24), this.S(20)), Dock = DockStyle.Fill };

    protected FlowLayoutPanel Actions(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, this.S(16), 0, 0), BackColor = Color.Transparent };
        foreach (var b in buttons.Reverse())
        {
            b.MinimumSize = new Size(this.S(100), 0);
            b.Margin = new Padding(this.S(8), 0, 0, 0);
            panel.Controls.Add(b);
        }
        return panel;
    }

    protected Label Heading(string text) => Ui.Lbl(text, SectionFont, P.Text);

    protected Label Description(string text, int width)
    {
        var l = Ui.Lbl(text, Body, P.TextSecondary);
        l.MaximumSize = new Size(this.S(width), 0);
        l.Margin = new Padding(0, this.S(6), 0, 0);
        return l;
    }

    protected TextBox Input(int width) => new()
    {
        Width = this.S(width),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = P.Dark ? P.SurfaceAlt : P.Surface,
        ForeColor = P.Text,
        Font = Body,
    };

    protected NumericUpDown PortInput() => new()
    {
        Minimum = 1,
        Maximum = 65535,
        Value = Protocol.HttpPort,
        Width = this.S(100),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = P.Dark ? P.SurfaceAlt : P.Surface,
        ForeColor = P.Text,
        Font = Body,
    };
}

/// <summary>Themed replacement for MessageBox: title, text and one or two buttons.</summary>
internal sealed class MessageDialog : KlipDialog
{
    public MessageDialog(Palette p, string title, string text, string ok, string? cancel) : base(p, "Klip")
    {
        var layout = NewLayout();
        layout.Add(Heading(title), 0, 0);
        layout.Add(Description(text, 380), 0, 1);
        var okButton = Ui.Btn(p, ok, ButtonKind.Accent, p.Background);
        okButton.DialogResult = DialogResult.OK;
        var buttons = new List<Button> { okButton };
        if (cancel is not null)
        {
            var cancelButton = Ui.Btn(p, cancel, ButtonKind.Standard, p.Background);
            cancelButton.DialogResult = DialogResult.Cancel;
            buttons.Add(cancelButton);
            CancelButton = cancelButton;
        }
        else
        {
            CancelButton = okButton;
        }
        layout.Add(Actions([.. buttons]), 0, 2);
        Controls.Add(layout);
        AcceptButton = okButton;
    }
}

/// <summary>Shows the 6-digit PIN. OK = "Coincide". Can be closed by code when the session ends.</summary>
internal sealed class PinDialog : KlipDialog
{
    public PinDialog(string title, string pin, string text) : base(Palette.Current(), title)
    {
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;

        var layout = NewLayout();
        layout.Padding = new Padding(this.S(32), this.S(26), this.S(32), this.S(24));
        var heading = Heading(title);
        heading.Anchor = AnchorStyles.None;
        layout.Add(heading, 0, 0);

        var digits = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None, Margin = new Padding(0, this.S(18), 0, this.S(14)), BackColor = Color.Transparent };
        for (var i = 0; i < pin.Length; i++)
        {
            var cell = new Card(P.Surface, P.Stroke, 8)
            {
                Size = new Size(this.S(44), this.S(56)),
                Margin = new Padding(i == 3 ? this.S(16) : this.S(3), 0, this.S(3), 0),
            };
            cell.Controls.Add(new Label
            {
                Text = pin[i].ToString(),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 24f, FontStyle.Bold),
                ForeColor = P.Text,
                BackColor = Color.Transparent,
            });
            digits.Controls.Add(cell);
        }
        layout.Add(digits, 0, 1);
        var description = Description(text, 360);
        description.Anchor = AnchorStyles.None;
        description.TextAlign = ContentAlignment.MiddleCenter;
        layout.Add(description, 0, 2);

        // Buttons close the form explicitly: a button's DialogResult only closes MODAL forms, and the incoming
        // pairing dialog is shown modeless (so it can be closed when the other side cancels).
        var yes = Ui.Btn(P, "Coincide", ButtonKind.Accent, P.Background);
        var no = Ui.Btn(P, "No coincide", ButtonKind.Standard, P.Background);
        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);
        var actions = Actions(yes, no);
        actions.Anchor = AnchorStyles.None;
        layout.Add(actions, 0, 3);
        Controls.Add(layout);
        AcceptButton = yes;
        CancelButton = no;
    }

    /// <summary>True only if the user pressed "Coincide". Closing the window any other way means "no".</summary>
    public bool Accepted { get; private set; }

    private void Answer(bool accepted)
    {
        Accepted = accepted;
        DialogResult = accepted ? DialogResult.OK : DialogResult.Cancel; // ends ShowDialog when modal
        Close(); // needed when modeless
    }

    /// <summary>True when closed because the session ended elsewhere (not a user answer).</summary>
    public bool ClosedExternally { get; private set; }

    public void CloseExternally()
    {
        ClosedExternally = true;
        Close();
    }
}

internal sealed class PairByIpDialog : KlipDialog
{
    private readonly TextBox _host;
    private readonly NumericUpDown _port;

    public string Host => _host.Text.Trim();
    public int Port => (int)_port.Value;

    public PairByIpDialog(Palette p) : base(p, "Vincular por IP")
    {
        _host = Input(220);
        _port = PortInput();
        var layout = NewLayout();
        layout.Add(Heading("Vincular por IP"), 0, 0);
        layout.Add(Description("Útil si el otro dispositivo no aparece en «Cerca de ti» (redes de invitados, aislamiento de clientes…). Debe tener la ventana de Klip abierta.", 380), 0, 1);

        var fields = new Row(Row.Auto, Row.Auto) { Margin = new Padding(0, this.S(14), 0, 0) };
        fields.Add(Ui.Lbl("Dirección IP", Small, p.Text), 0, 0).Add(Ui.Lbl("Puerto", Small, p.Text), 1, 0);
        _host.Margin = new Padding(0, this.S(4), this.S(12), 0);
        _port.Margin = new Padding(0, this.S(4), 0, 0);
        fields.Add(_host, 0, 1).Add(_port, 1, 1);
        layout.Add(fields, 0, 2);

        var ok = Ui.Btn(p, "Vincular", ButtonKind.Accent, p.Background);
        ok.Click += (_, _) =>
        {
            if (Host.Length == 0) { _host.Focus(); return; }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = Ui.Btn(p, "Cancelar", ButtonKind.Standard, p.Background);
        cancel.DialogResult = DialogResult.Cancel;
        layout.Add(Actions(ok, cancel), 0, 3);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Advanced: manual pairing with a shared 64-hex-char secret (no PIN). Both sides must enter the same secret.</summary>
internal sealed class AddPeerDialog : KlipDialog
{
    private readonly TextBox _name;
    private readonly TextBox _id;
    private readonly TextBox _host;
    private readonly NumericUpDown _port;
    private readonly ComboBox _platform;
    private readonly TextBox _secret;

    public string PeerName => _name.Text.Trim();
    public string PeerId => _id.Text.Trim();
    public string Host => _host.Text.Trim();
    public int Port => (int)_port.Value;
    public string Platform => (string)_platform.SelectedItem!;
    public byte[] Secret => Convert.FromHexString(_secret.Text.Trim());

    public AddPeerDialog(Palette p, string localDeviceId) : base(p, "Añadir dispositivo manualmente")
    {
        _name = Input(320);
        _id = Input(320);
        _host = Input(320);
        _port = PortInput();
        _secret = Input(320);
        _secret.Font = new Font("Consolas", 9.5f);
        _platform = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = this.S(140),
            FlatStyle = FlatStyle.Flat,
            BackColor = p.Dark ? p.SurfaceAlt : p.Surface,
            ForeColor = p.Text,
            Font = Body,
        };
        _platform.Items.AddRange(["android", "windows", "linux", "devpeer", "unknown"]);
        _platform.SelectedIndex = 0;

        var layout = NewLayout();
        layout.Add(Heading("Añadir dispositivo manualmente"), 0, 0);
        layout.Add(Description("Usa mejor «Vincular» con el PIN. Esto solo es para pruebas: introduce el mismo secreto en ambos dispositivos.", 520), 0, 1);

        var grid = new Row(Row.Auto, Row.Auto, Row.Auto) { Margin = new Padding(0, this.S(12), 0, 0) };
        var r = 0;
        void Field(string label, Control c, Control? extra = null)
        {
            var l = Ui.Lbl(label, Body, p.TextSecondary);
            l.Margin = new Padding(0, this.S(4), this.S(12), this.S(4));
            grid.Add(l, 0, r);
            c.Margin = new Padding(0, this.S(4), 0, this.S(4));
            c.Anchor = AnchorStyles.Left;
            grid.Add(c, 1, r);
            if (extra is not null) grid.Add(extra, 2, r);
            r++;
        }

        var ownId = Input(320);
        ownId.ReadOnly = true;
        ownId.Text = localDeviceId;
        var copyId = Ui.Btn(p, "Copiar", ButtonKind.Subtle, p.Background);
        copyId.Click += (_, _) => Clipboard.SetText(localDeviceId);
        var generate = Ui.Btn(p, "Generar", ButtonKind.Subtle, p.Background);
        generate.Click += (_, _) => _secret.Text = Convert.ToHexStringLower(PairKeys.NewRandomSecret());
        var copy = Ui.Btn(p, "Copiar", ButtonKind.Subtle, p.Background);
        copy.Click += (_, _) => { if (_secret.Text.Length > 0) Clipboard.SetText(_secret.Text); };
        var secretButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
        secretButtons.Controls.AddRange([generate, copy]);

        Field("ID de este equipo", ownId, copyId);
        Field("Nombre", _name);
        Field("ID de dispositivo", _id);
        Field("IP / host", _host);
        Field("Puerto", _port);
        Field("Plataforma", _platform);
        Field("Secreto (64 hex)", _secret, secretButtons);
        layout.Add(grid, 0, 2);

        var ok = Ui.Btn(p, "Añadir", ButtonKind.Accent, p.Background);
        ok.Click += (_, _) => ValidateAndClose();
        var cancel = Ui.Btn(p, "Cancelar", ButtonKind.Standard, p.Background);
        cancel.DialogResult = DialogResult.Cancel;
        layout.Add(Actions(ok, cancel), 0, 3);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void ValidateAndClose()
    {
        string? error = null;
        if (PeerName.Length == 0) error = "Falta el nombre.";
        else if (PeerId.Length == 0) error = "Falta el ID de dispositivo.";
        else if (Host.Length == 0) error = "Falta la IP o host.";
        else if (!System.Text.RegularExpressions.Regex.IsMatch(_secret.Text.Trim(), "^[0-9a-fA-F]{64}$")) error = "El secreto debe tener 64 caracteres hexadecimales.";
        if (error is not null)
        {
            using var dialog = new MessageDialog(P, "Revisa los datos", error, "Aceptar", null);
            dialog.ShowDialog(this);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
