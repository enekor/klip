using Klip.Core;
using Klip.Node;

namespace Klip.Windows;

/// <summary>
/// Configuration window (plan §4.1). While visible: pairing window open and UDP radar running.
/// </summary>
internal sealed class ConfigWindow : Form
{
    private readonly TrayApp _app;
    private readonly SyncNode _node;
    private readonly TextBox _name = new() { Width = 260 };
    private readonly Label _address = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _lastReceived = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly Label _activity = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 4, 8, 4) };
    private readonly ListView _discovered = NewList(checkBoxes: false);
    private readonly ListView _peers = NewList(checkBoxes: true);
    private bool _refreshing;
    private bool _busy;

    public ConfigWindow(TrayApp app)
    {
        _app = app;
        _node = app.Node;
        Text = "Klip — Configuración";
        Width = 900;
        Height = 640;
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        // ---- This device ----
        var identity = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 0) };
        identity.Controls.Add(new Label { Text = "Nombre:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        identity.Controls.Add(_name, 1, 0);
        var saveName = new Button { Text = "Guardar", AutoSize = true };
        saveName.Click += (_, _) => SaveName();
        identity.Controls.Add(saveName, 2, 0);
        identity.Controls.Add(new Label { Text = "Dirección:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        identity.Controls.Add(_address, 1, 1);
        identity.Controls.Add(new Label { Text = "Estado:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        identity.Controls.Add(_status, 1, 2);
        identity.Controls.Add(new Label { Text = "Última recepción:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 3);
        identity.Controls.Add(_lastReceived, 1, 3);
        identity.SetColumnSpan(_lastReceived, 2);

        // ---- Radar ----
        _discovered.Columns.Add("Nombre", 200);
        _discovered.Columns.Add("Plataforma", 90);
        _discovered.Columns.Add("Dirección", 160);
        _discovered.Columns.Add("Estado", 300);
        _discovered.DoubleClick += async (_, _) => await PairSelected();
        var radarButtons = Buttons(
            ("Vincular", async () => await PairSelected()),
            ("Vincular por IP…", async () => await PairByIp()));
        var radar = Group("Dispositivos detectados en la red (abre Klip en el otro dispositivo)", _discovered, radarButtons);

        // ---- Paired ----
        _peers.Columns.Add("Activo / Nombre", 170);
        _peers.Columns.Add("Plataforma", 85);
        _peers.Columns.Add("Dirección", 150);
        _peers.Columns.Add("Última conexión", 120);
        _peers.Columns.Add("Estado", 200);
        _peers.ItemChecked += (_, e) =>
        {
            if (!_refreshing && e.Item.Tag is string id) _node.SetPeerEnabled(id, e.Item.Checked);
        };
        var peerButtons = Buttons(
            ("Comprobar conexión", async () => await PingSelected()),
            ("Enviar prueba", async () => await TestSelected()),
            ("Enviar prueba a todos", async () => await _app.SendTestAsync(null)),
            ("Desvincular", async () => await UnpairSelected()),
            ("Expulsar del grupo", async () => await ExpelSelected()),
            ("Añadir manualmente (avanzado)…", AddManual));
        var paired = Group("Dispositivos vinculados (marca los que reciben tus copias)", _peers, peerButtons);

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(6) };
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        lists.Controls.Add(radar, 0, 0);
        lists.Controls.Add(paired, 0, 1);

        Controls.Add(lists);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 26, Controls = { _activity } });
        Controls.Add(identity);

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
        };
        RefreshAll();
    }

    private static ListView NewList(bool checkBoxes) => new()
    {
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = checkBoxes,
        MultiSelect = false,
        Dock = DockStyle.Fill,
        HideSelection = false,
    };

    private static FlowLayoutPanel Buttons(params (string Text, Action OnClick)[] buttons)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        foreach (var (text, onClick) in buttons)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            b.Click += (_, _) => onClick();
            panel.Controls.Add(b);
        }
        return panel;
    }

    private static GroupBox Group(string title, Control list, Control buttons)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        g.Controls.Add(list);
        g.Controls.Add(buttons);
        return g;
    }

    private void OnReceived(ReceivedItem _) => OnNodeChanged();

    private void OnNodeChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshAll);
    }

    public void RefreshAll()
    {
        if (IsDisposed) return;
        if (!_name.Focused) _name.Text = _node.DeviceName;
        var ips = NetworkInfo.LocalIPv4();
        _address.Text = (ips.Count == 0 ? "sin red" : string.Join(", ", ips)) + $"  ·  puerto {_node.HttpPort}";
        var on = _node.SyncEnabled && _node.IsServerRunning;
        _status.Text = on ? "Sincronización activada" : "Sincronización pausada (actívala en el icono de la bandeja para vincular)";
        _status.ForeColor = on ? Color.DarkGreen : Color.DimGray;
        _lastReceived.Text = _node.LastReceived is { } r
            ? $"{DateTimeOffset.FromUnixTimeMilliseconds(r.AtMs).LocalDateTime:HH:mm:ss} · de {r.From}{(r.IsTest ? " · prueba" : "")}" +
              $"{Environment.NewLine}«{r.Preview(200)}»"
            : "nada todavía";

        var selectedDiscovered = SelectedTag(_discovered);
        _discovered.BeginUpdate();
        _discovered.Items.Clear();
        foreach (var d in _node.Discovered)
        {
            var item = new ListViewItem(d.DeviceName) { Tag = d.DeviceId, Selected = d.DeviceId == selectedDiscovered };
            item.SubItems.Add(PlatformLabel(d.Platform));
            item.SubItems.Add($"{d.Host}:{d.HttpPort}");
            item.SubItems.Add(d.PairingOpen ? "Listo para vincular" : "Abre su ventana de Klip para vincular");
            _discovered.Items.Add(item);
        }
        _discovered.EndUpdate();

        var selected = SelectedTag(_peers);
        _refreshing = true;
        _peers.BeginUpdate();
        _peers.Items.Clear();
        foreach (var p in _node.Peers)
        {
            var item = new ListViewItem(p.Name) { Tag = p.DeviceId, Checked = p.Enabled, Selected = p.DeviceId == selected };
            item.SubItems.Add(PlatformLabel(p.Platform));
            item.SubItems.Add($"{p.Host}:{p.Port}");
            item.SubItems.Add(p.LastSeenMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("dd/MM HH:mm:ss") : "nunca");
            item.SubItems.Add(p.LastStatus ?? "");
            _peers.Items.Add(item);
        }
        _peers.EndUpdate();
        _refreshing = false;
    }

    private static string? SelectedTag(ListView list) => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null;

    private static string PlatformLabel(string p) => p switch
    {
        "android" => "Android",
        "windows" => "Windows",
        "devpeer" => "DevPeer",
        _ => p,
    };

    private void SetActivity(string text) => _activity.Text = text;

    private void SaveName()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) return;
        _node.DeviceName = name;
        RefreshAll();
    }

    // ---------- Pairing (initiator) ----------

    private async Task PairSelected()
    {
        if (SelectedTag(_discovered) is not { } id) return;
        var d = _node.Discovered.FirstOrDefault(x => x.DeviceId == id);
        if (d is null) return;
        await PairWith(d.DeviceName, d.Host, d.HttpPort);
    }

    private async Task PairByIp()
    {
        using var dialog = new PairByIpDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK) await PairWith(dialog.Host, dialog.Host, dialog.Port);
    }

    private async Task PairWith(string name, string host, int port)
    {
        if (_busy) return;
        if (!_node.IsServerRunning)
        {
            MessageBox.Show(this, "Activa la sincronización (icono de la bandeja) antes de vincular.", "Klip", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _busy = true;
        try
        {
            SetActivity($"Contactando con {name}…");
            var (pairing, error) = await _node.StartPairingAsync(host, port);
            if (pairing is null)
            {
                SetActivity("");
                MessageBox.Show(this, error, "Klip — Vincular", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var pin = new PinDialog($"Vincular con {pairing.DeviceName}", pairing.Pin, $"Comprueba que {pairing.DeviceName} muestra el mismo código.");
            pin.ShowDialog(this);
            if (!pin.Accepted)
            {
                SetActivity("");
                await _node.CancelPairingAsync(pairing);
                return;
            }

            SetActivity($"Esperando la confirmación en {pairing.DeviceName}…");
            var outcome = await _node.ConfirmPairingAsync(pairing);
            SetActivity(outcome.Ok ? $"Vinculado con {pairing.DeviceName}" : "");
            if (!outcome.Ok) MessageBox.Show(this, outcome.Message ?? "No se pudo vincular", "Klip — Vincular", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------- Paired devices ----------

    private async Task PingSelected()
    {
        if (SelectedTag(_peers) is not { } id) return;
        SetActivity("Comprobando…");
        var r = await _node.PingAsync(id);
        SetActivity(r.Ok ? $"Conectado ({r.ElapsedMs} ms)" : $"Sin conexión: {r.Error}");
    }

    private async Task TestSelected()
    {
        if (SelectedTag(_peers) is not { } id) return;
        await _app.SendTestAsync(id);
    }

    private async Task UnpairSelected()
    {
        if (SelectedTag(_peers) is not { } id) return;
        var name = _peers.SelectedItems[0].Text;
        if (MessageBox.Show(this, $"¿Desvincular «{name}»? Este equipo y {name} dejarán de sincronizarse entre sí.", "Klip",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            await _node.UnpairAsync(id);
        }
    }

    private async Task ExpelSelected()
    {
        if (SelectedTag(_peers) is not { } id) return;
        var name = _peers.SelectedItems[0].Text;
        if (MessageBox.Show(this, $"¿Expulsar «{name}» del grupo? Se eliminará de todos los dispositivos vinculados.", "Klip",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            await _node.ExpelAsync(id);
        }
    }

    private void AddManual()
    {
        using var dialog = new AddPeerDialog(_node.DeviceId);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _node.AddOrUpdatePeer(dialog.PeerId, dialog.PeerName, dialog.Host, dialog.Port, dialog.Secret, dialog.Platform);
        }
        catch (ArgumentException e)
        {
            MessageBox.Show(this, e.Message, "Klip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>Shows the 6-digit PIN. OK = "Coincide". Can be closed by code when the session ends.</summary>
internal sealed class PinDialog : Form
{
    public PinDialog(string title, string pin, string text)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(20), Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Anchor = AnchorStyles.None });
        layout.Controls.Add(new Label
        {
            Text = $"{pin[..3]} {pin[3..]}",
            AutoSize = true,
            Font = new Font("Consolas", 32f, FontStyle.Bold),
            Anchor = AnchorStyles.None,
            Padding = new Padding(0, 12, 0, 12),
        });
        layout.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(380, 0), Anchor = AnchorStyles.None });

        // Buttons close the form explicitly: a button's DialogResult only closes MODAL forms, and the incoming
        // pairing dialog is shown modeless (so it can be closed when the other side cancels).
        var yes = new Button { Text = "Coincide", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        var no = new Button { Text = "No coincide", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([yes, no]);
        layout.Controls.Add(buttons);
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

internal sealed class PairByIpDialog : Form
{
    private readonly TextBox _host = new() { Width = 200 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = Protocol.HttpPort, Width = 100 };

    public string Host => _host.Text.Trim();
    public int Port => (int)_port.Value;

    public PairByIpDialog()
    {
        Text = "Vincular por IP";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(10), Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = "IP:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_host, 1, 0);
        t.Controls.Add(new Label { Text = "Puerto:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        t.Controls.Add(_port, 1, 1);
        t.Controls.Add(new Label { Text = "El otro dispositivo debe tener Klip abierto.", AutoSize = true, ForeColor = Color.DimGray }, 1, 2);
        var ok = new Button { Text = "Vincular", AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (Host.Length == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        actions.Controls.AddRange([cancel, ok]);
        t.Controls.Add(actions, 1, 3);
        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Advanced: manual pairing with a shared 64-hex-char secret (no PIN). Both sides must enter the same secret.</summary>
internal sealed class AddPeerDialog : Form
{
    private readonly TextBox _name = new() { Width = 320 };
    private readonly TextBox _id = new() { Width = 320 };
    private readonly TextBox _host = new() { Width = 320 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = Protocol.HttpPort, Width = 100 };
    private readonly ComboBox _platform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _secret = new() { Width = 320, Font = new Font(FontFamily.GenericMonospace, 9) };

    public string PeerName => _name.Text.Trim();
    public string PeerId => _id.Text.Trim();
    public string Host => _host.Text.Trim();
    public int Port => (int)_port.Value;
    public string Platform => (string)_platform.SelectedItem!;
    public byte[] Secret => Convert.FromHexString(_secret.Text.Trim());

    public AddPeerDialog(string localDeviceId)
    {
        Text = "Añadir dispositivo manualmente (avanzado)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        _platform.Items.AddRange(["android", "windows", "devpeer", "unknown"]);
        _platform.SelectedIndex = 0;

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(10), Dock = DockStyle.Fill };
        void Row(int r, string label, Control c, Control? extra = null)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            t.Controls.Add(c, 1, r);
            if (extra is not null) t.Controls.Add(extra, 2, r);
        }

        var ownId = new TextBox { Width = 320, ReadOnly = true, Text = localDeviceId };
        var copyId = new Button { Text = "Copiar", AutoSize = true };
        copyId.Click += (_, _) => Clipboard.SetText(localDeviceId);
        var generate = new Button { Text = "Generar", AutoSize = true };
        generate.Click += (_, _) => _secret.Text = Convert.ToHexStringLower(PairKeys.NewRandomSecret());
        var copy = new Button { Text = "Copiar", AutoSize = true };
        copy.Click += (_, _) => { if (_secret.Text.Length > 0) Clipboard.SetText(_secret.Text); };
        var secretButtons = new FlowLayoutPanel { AutoSize = true };
        secretButtons.Controls.AddRange([generate, copy]);

        Row(0, "ID de este equipo:", ownId, copyId);
        Row(1, "Nombre:", _name);
        Row(2, "ID de dispositivo:", _id);
        Row(3, "IP / host:", _host);
        Row(4, "Puerto:", _port);
        Row(5, "Plataforma:", _platform);
        Row(6, "Secreto (64 hex):", _secret, secretButtons);
        t.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            ForeColor = Color.DimGray,
            Text = "Usa mejor «Vincular» con el PIN. Esto solo es para pruebas: introduce el mismo secreto en ambos dispositivos.",
        }, 1, 7);

        var ok = new Button { Text = "Añadir", AutoSize = true };
        ok.Click += (_, _) => ValidateAndClose();
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        actions.Controls.AddRange([cancel, ok]);
        t.Controls.Add(actions, 1, 8);

        Controls.Add(t);
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
            MessageBox.Show(this, error, "Klip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
