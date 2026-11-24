using Klip.Core;
using Klip.Node;

namespace Klip.Windows;

/// <summary>Configuration window: local identity and the list of paired devices (plan §4.1).</summary>
internal sealed class ConfigWindow : Form
{
    private readonly TrayApp _app;
    private readonly SyncNode _node;
    private readonly TextBox _name = new() { Width = 260 };
    private readonly TextBox _deviceId = new() { Width = 260, ReadOnly = true };
    private readonly Label _address = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly ListView _peers = new()
    {
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        MultiSelect = false,
        Dock = DockStyle.Fill,
    };
    private bool _refreshing;

    public ConfigWindow(TrayApp app)
    {
        _app = app;
        _node = app.Node;
        Text = "Klip — Configuración";
        Width = 860;
        Height = 520;
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var identity = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10) };
        identity.Controls.Add(new Label { Text = "Nombre:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        identity.Controls.Add(_name, 1, 0);
        var saveName = new Button { Text = "Guardar", AutoSize = true };
        saveName.Click += (_, _) => SaveName();
        identity.Controls.Add(saveName, 2, 0);
        identity.Controls.Add(new Label { Text = "ID de dispositivo:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        identity.Controls.Add(_deviceId, 1, 1);
        var copyId = new Button { Text = "Copiar", AutoSize = true };
        copyId.Click += (_, _) => Clipboard.SetText(_node.DeviceId);
        identity.Controls.Add(copyId, 2, 1);
        identity.Controls.Add(new Label { Text = "Dirección:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        identity.Controls.Add(_address, 1, 2);
        identity.Controls.Add(new Label { Text = "Estado:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        identity.Controls.Add(_status, 1, 3);

        _peers.Columns.Add("Activo / Nombre", 170);
        _peers.Columns.Add("Plataforma", 85);
        _peers.Columns.Add("Dirección", 150);
        _peers.Columns.Add("Última conexión", 130);
        _peers.Columns.Add("Estado", 200);
        _peers.Columns.Add("ID", 260);
        _peers.ItemChecked += (_, e) =>
        {
            if (!_refreshing && e.Item.Tag is string id) _node.SetPeerEnabled(id, e.Item.Checked);
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Añadir manualmente…", AddPeer));
        buttons.Controls.Add(MakeButton("Eliminar", RemovePeer));
        buttons.Controls.Add(MakeButton("Comprobar conexión", async () => await PingSelected()));
        buttons.Controls.Add(MakeButton("Enviar prueba", async () => await TestSelected()));
        buttons.Controls.Add(MakeButton("Enviar prueba a todos", async () => await _app.SendTestAsync(null)));

        var group = new GroupBox { Text = "Dispositivos vinculados (marca los que reciben tus copias)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(_peers);

        Controls.Add(group);
        Controls.Add(buttons);
        Controls.Add(identity);

        _node.PeersChanged += OnPeersChanged;
        FormClosed += (_, _) => _node.PeersChanged -= OnPeersChanged;
        RefreshAll();
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OnPeersChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshAll);
    }

    public void RefreshAll()
    {
        if (IsDisposed) return;
        if (!_name.Focused) _name.Text = _node.DeviceName;
        _deviceId.Text = _node.DeviceId;
        var ips = NetworkInfo.LocalIPv4();
        _address.Text = (ips.Count == 0 ? "sin red" : string.Join(", ", ips)) + $"  ·  puerto {_node.HttpPort}";
        _status.Text = _node.SyncEnabled && _node.IsServerRunning ? "Sincronización activada" : "Sincronización pausada";
        _status.ForeColor = _node.SyncEnabled && _node.IsServerRunning ? Color.DarkGreen : Color.DimGray;

        var selected = SelectedId();
        _refreshing = true;
        _peers.BeginUpdate();
        _peers.Items.Clear();
        foreach (var p in _node.Peers)
        {
            var item = new ListViewItem(p.Name) { Tag = p.DeviceId, Checked = p.Enabled };
            item.SubItems.Add(p.Platform);
            item.SubItems.Add($"{p.Host}:{p.Port}");
            item.SubItems.Add(p.LastSeenMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("dd/MM HH:mm:ss") : "nunca");
            item.SubItems.Add(p.LastStatus ?? "");
            item.SubItems.Add(p.DeviceId);
            item.Selected = p.DeviceId == selected;
            _peers.Items.Add(item);
        }
        _peers.EndUpdate();
        _refreshing = false;
    }

    private string? SelectedId() => _peers.SelectedItems.Count > 0 ? _peers.SelectedItems[0].Tag as string : null;

    private void SaveName()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) return;
        _node.DeviceName = name;
        RefreshAll();
    }

    private void AddPeer()
    {
        using var dialog = new AddPeerDialog();
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

    private void RemovePeer()
    {
        if (SelectedId() is not { } id) return;
        var name = _peers.SelectedItems[0].Text;
        if (MessageBox.Show(this, $"¿Eliminar «{name}»? Dejará de recibir y enviar copias con este equipo.", "Klip",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _node.RemovePeer(id);
        }
    }

    private async Task PingSelected()
    {
        if (SelectedId() is not { } id) return;
        await _node.PingAsync(id);
    }

    private async Task TestSelected()
    {
        if (SelectedId() is not { } id) return;
        await _app.SendTestAsync(id);
    }
}

/// <summary>Manual pairing for development (Phase 2): both sides enter the same 64-hex-char secret.</summary>
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

    public AddPeerDialog()
    {
        Text = "Añadir dispositivo manualmente";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        _platform.Items.AddRange(["android", "windows", "unknown"]);
        _platform.SelectedIndex = 0;

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(10), Dock = DockStyle.Fill };
        void Row(int r, string label, Control c, Control? extra = null)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            t.Controls.Add(c, 1, r);
            if (extra is not null) t.Controls.Add(extra, 2, r);
        }

        var generate = new Button { Text = "Generar", AutoSize = true };
        generate.Click += (_, _) => _secret.Text = Convert.ToHexStringLower(PairKeys.NewRandomSecret());
        var copy = new Button { Text = "Copiar", AutoSize = true };
        copy.Click += (_, _) => { if (_secret.Text.Length > 0) Clipboard.SetText(_secret.Text); };
        var secretButtons = new FlowLayoutPanel { AutoSize = true };
        secretButtons.Controls.AddRange([generate, copy]);

        Row(0, "Nombre:", _name);
        Row(1, "ID de dispositivo:", _id);
        Row(2, "IP / host:", _host);
        Row(3, "Puerto:", _port);
        Row(4, "Plataforma:", _platform);
        Row(5, "Secreto (64 hex):", _secret, secretButtons);
        t.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            ForeColor = Color.DimGray,
            Text = "Modo desarrollo: introduce el mismo secreto en ambos dispositivos. " +
                   "En la Fase 4 esto se sustituye por el emparejamiento automático con PIN.",
        }, 1, 6);

        var ok = new Button { Text = "Añadir", DialogResult = DialogResult.None, AutoSize = true };
        ok.Click += (_, _) => Validate();
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        actions.Controls.AddRange([cancel, ok]);
        t.Controls.Add(actions, 1, 7);

        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private new void Validate()
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
