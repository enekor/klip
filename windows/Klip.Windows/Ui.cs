using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Klip.Windows;

/// <summary>Light/dark palette, read from the Windows "app mode" setting when a window opens.</summary>
internal sealed record Palette(
    bool Dark,
    Color Background, Color Surface, Color SurfaceAlt, Color Stroke,
    Color Text, Color TextSecondary,
    Color Accent, Color AccentHover, Color AccentText, Color AccentSoft,
    Color Success, Color SuccessSoft, Color Danger, Color DangerSoft, Color Neutral)
{
    private static Color C(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));

    public static readonly Palette Light = new(false,
        C(0xF4F6FA), C(0xFFFFFF), C(0xEEF1F6), C(0xE2E6EE),
        C(0x1B1F27), C(0x5F6673),
        C(0x2563EB), C(0x1D4ED8), C(0xFFFFFF), C(0xE6EEFE),
        C(0x15803D), C(0xDCFCE7), C(0xB91C1C), C(0xFEE2E2), C(0x8A8F98));

    public static readonly Palette DarkMode = new(true,
        C(0x15171C), C(0x1F2229), C(0x2A2E37), C(0x30343D),
        C(0xF1F3F7), C(0xA3A9B5),
        C(0x3B82F6), C(0x2563EB), C(0xFFFFFF), C(0x1E3A5F),
        C(0x4ADE80), C(0x14361F), C(0xF87171), C(0x3F1D1D), C(0x7C828D));

    public static Palette Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? DarkMode : Light;
        }
        catch (Exception)
        {
            return Light;
        }
    }
}

internal static class Ui
{
    private static readonly string Family = PickFamily("Segoe UI Variable Text", "Segoe UI");
    private static readonly string DisplayFamily = PickFamily("Segoe UI Variable Display", "Segoe UI");

    public static Font Body { get; } = new(Family, 9.75f);
    public static Font Small { get; } = new(Family, 9f);
    public static Font Strong { get; } = new(Family, 9.75f, FontStyle.Bold);
    public static Font SectionFont { get; } = new(DisplayFamily, 11.5f, FontStyle.Bold);
    public static Font TitleFont { get; } = new(DisplayFamily, 17f, FontStyle.Bold);
    public static Font TabFont { get; } = new(Family, 10.5f);

    private static string PickFamily(params string[] names)
    {
        using var installed = new InstalledFontCollection();
        foreach (var name in names)
            if (installed.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        return "Segoe UI";
    }

    /// <summary>Scales a 96-DPI length to the window's DPI.</summary>
    public static int S(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96f);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Label Lbl(string text, Font? font = null, Color? color = null, bool wrap = false) => new()
    {
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Color.Empty,
        AutoSize = true,
        Dock = wrap ? DockStyle.Fill : DockStyle.None,
        Anchor = wrap ? AnchorStyles.None : AnchorStyles.Left,
        Margin = new Padding(0),
        UseMnemonic = false,
        BackColor = Color.Transparent,
    };

    public enum ButtonKind { Standard, Accent, Subtle }

    public static Button Btn(Palette p, string text, ButtonKind kind, Color surface)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Font = kind == ButtonKind.Subtle ? Strong : Body,
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(4, 0, 0, 0),
            Anchor = AnchorStyles.Right,
            UseMnemonic = false,
        };
        switch (kind)
        {
            case ButtonKind.Accent:
                b.BackColor = p.Accent;
                b.ForeColor = p.AccentText;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = p.AccentHover;
                b.FlatAppearance.MouseDownBackColor = p.AccentHover;
                break;
            case ButtonKind.Subtle:
                b.BackColor = surface;
                b.ForeColor = p.Accent;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = p.SurfaceAlt;
                b.FlatAppearance.MouseDownBackColor = p.Stroke;
                break;
            default:
                b.BackColor = p.SurfaceAlt;
                b.ForeColor = p.Text;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = p.Stroke;
                b.FlatAppearance.MouseOverBackColor = p.Stroke;
                b.FlatAppearance.MouseDownBackColor = p.Stroke;
                break;
        }
        b.EnabledChanged += (_, _) => b.ForeColor = b.Enabled ? (kind == ButtonKind.Accent ? p.AccentText : kind == ButtonKind.Subtle ? p.Accent : p.Text) : p.TextSecondary;
        return b;
    }

    /// <summary>Dark title bar on Windows 10 20H1+ / 11, to match the dark palette.</summary>
    public static void ApplyTitleBar(Form form, Palette p)
    {
        if (!p.Dark) return;
        form.HandleCreated += (_, _) =>
        {
            var on = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int)); // older Windows 10 builds
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ClearAndDispose(this Control.ControlCollection controls)
    {
        var old = controls.Cast<Control>().ToList();
        controls.Clear();
        foreach (var c in old) c.Dispose();
    }

    public static ContextMenuStrip MenuFor(Palette p) => new()
    {
        Renderer = new ToolStripProfessionalRenderer(new MenuColors(p)) { RoundedEdges = false },
        BackColor = p.Surface,
        ForeColor = p.Text,
        Font = Body,
        ShowImageMargin = false,
    };

    private sealed class MenuColors(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Surface;
        public override Color MenuBorder => p.Stroke;
        public override Color MenuItemBorder => p.SurfaceAlt;
        public override Color MenuItemSelected => p.SurfaceAlt;
        public override Color MenuItemSelectedGradientBegin => p.SurfaceAlt;
        public override Color MenuItemSelectedGradientEnd => p.SurfaceAlt;
        public override Color SeparatorDark => p.Stroke;
        public override Color SeparatorLight => p.Stroke;
        public override Color ImageMarginGradientBegin => p.Surface;
        public override Color ImageMarginGradientMiddle => p.Surface;
        public override Color ImageMarginGradientEnd => p.Surface;
    }
}

/// <summary>Rounded panel. Its BackColor is the fill (children inherit it); the corners show the parent's color.</summary>
internal class Card : Panel
{
    private Color _border;

    public Card(Color fill, Color border, int radius = 10)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = fill;
        _border = border;
        Radius = radius;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor
    {
        get => _border;
        set { _border = value; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = this.S(Radius);
        using var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), r);
        using var fill = new SolidBrush(BackColor);
        g.FillPath(fill, path);
        if (_border != Color.Empty && _border != BackColor)
        {
            using var pen = new Pen(_border);
            g.DrawPath(pen, path);
        }
    }
}

/// <summary>
/// A card whose height follows its content: the content is a top-docked, auto-sized control, and the card grows
/// or shrinks with it while its width is set by the page.
/// </summary>
internal sealed class AutoCard : Card
{
    public AutoCard(Control content, Color fill, Color border, Padding padding) : base(fill, border)
    {
        Padding = padding;
        content.Dock = DockStyle.Top;
        Controls.Add(content);
        content.SizeChanged += (_, _) => Height = content.Height + Padding.Vertical;
        Height = content.Height + Padding.Vertical;
    }
}

/// <summary>Auto-sized grid used as card content; rows grow with their content.</summary>
internal sealed class Row : TableLayoutPanel
{
    public Row(params ColumnStyle[] columns)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0);
        Padding = new Padding(0);
        ColumnCount = columns.Length;
        RowCount = 1;
        foreach (var c in columns) ColumnStyles.Add(c);
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        BackColor = Color.Transparent;
    }

    public static ColumnStyle Auto => new(SizeType.AutoSize);
    public static ColumnStyle Fill => new(SizeType.Percent, 100);

    public Row Add(Control c, int column, int row = 0)
    {
        if (row >= RowCount)
        {
            RowCount = row + 1;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        Controls.Add(c, column, row);
        return this;
    }
}

/// <summary>Vertical scrolling page: children stack top-down and stretch to the page width.</summary>
internal sealed class Page : FlowLayoutPanel
{
    public Page(Color background)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        Dock = DockStyle.Fill;
        BackColor = background;
        DoubleBuffered = true;
        Padding = new Padding(this.S(20), this.S(4), this.S(20), this.S(24));
        // Only vertical scrolling.
        HorizontalScroll.Maximum = 0;
        HorizontalScroll.Visible = false;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var width = ClientSize.Width - Padding.Horizontal;
        foreach (Control c in Controls)
        {
            var w = Math.Max(100, width - c.Margin.Horizontal);
            if (c.Width != w) c.Width = w;
        }
        base.OnLayout(levent);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        PerformLayout();
    }
}

/// <summary>On/off switch in the style of Windows 11.</summary>
internal sealed class ToggleSwitch : Control
{
    private readonly Palette _p;
    private bool _checked;
    private bool _hover;

    public ToggleSwitch(Palette p)
    {
        _p = p;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Size = new Size(this.S(42), this.S(22));
        Cursor = Cursors.Hand;
        Margin = new Padding(0);
        Anchor = AnchorStyles.Right;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the state without raising <see cref="CheckedChanged"/> (refreshes from the model).</summary>
    public void SetSilently(bool value)
    {
        _checked = value;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Ui.Rounded(track, track.Height / 2);
        if (_checked)
        {
            using var b = new SolidBrush(Enabled ? (_hover ? _p.AccentHover : _p.Accent) : _p.Neutral);
            g.FillPath(b, path);
        }
        else
        {
            using var b = new SolidBrush(_hover ? _p.SurfaceAlt : Parent?.BackColor ?? _p.Surface);
            g.FillPath(b, path);
            using var pen = new Pen(_p.TextSecondary, 1.2f);
            g.DrawPath(pen, path);
        }
        var d = track.Height - this.S(_checked ? 8 : 10);
        var x = _checked ? track.Right - d - this.S(4) : track.X + this.S(5);
        var y = track.Y + (track.Height - d) / 2;
        using (var thumb = new SolidBrush(_checked ? Color.White : _p.TextSecondary)) g.FillEllipse(thumb, x, y, d, d);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(_p.Text) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }
}

/// <summary>Small rounded label: status ("● Sincronizando"), counters, tags.</summary>
internal sealed class Pill : Control
{
    private Color _fg;
    private Color _bg;
    private bool _dot;

    public Pill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font(Ui.Small, FontStyle.Bold);
        Margin = new Padding(0);
        Anchor = AnchorStyles.Left;
    }

    public void Set(string text, Color foreground, Color background, bool dot = true)
    {
        Text = text;
        _fg = foreground;
        _bg = background;
        _dot = dot;
        var size = TextRenderer.MeasureText(text, Font);
        Size = new Size(size.Width + this.S(dot ? 30 : 16), size.Height + this.S(8));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
        using (var b = new SolidBrush(_bg))
            g.FillPath(b, path);
        var x = this.S(8);
        if (_dot)
        {
            var d = this.S(8);
            using var dot = new SolidBrush(_fg);
            g.FillEllipse(dot, x, (Height - d) / 2f, d, d);
            x += d + this.S(6);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), _fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

/// <summary>Round tile with a phone / monitor glyph for the platform.</summary>
internal sealed class PlatformIcon : Control
{
    private readonly Palette _p;
    private readonly string _platform;

    public PlatformIcon(Palette p, string platform, bool dimmed = false)
    {
        _p = p;
        _platform = platform;
        Dimmed = dimmed;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(this.S(40), this.S(40));
        Margin = new Padding(0, 0, this.S(14), 0);
        Anchor = AnchorStyles.Left;
    }

    public bool Dimmed { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Dimmed ? _p.SurfaceAlt : _p.AccentSoft)) g.FillEllipse(bg, 0, 0, Width - 1, Height - 1);
        var color = Dimmed ? _p.Neutral : _p.Accent;
        var u = Width / 40f; // drawing grid: 40×40
        using var pen = new Pen(color, 1.8f * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);
        if (_platform == "android")
        {
            using var body = Ui.Rounded(new RectangleF(14 * u, 9 * u, 12 * u, 22 * u), 2.5f * u);
            g.DrawPath(pen, body);
            g.FillEllipse(brush, 18.8f * u, 26.3f * u, 2.4f * u, 2.4f * u);
        }
        else
        {
            using var screen = Ui.Rounded(new RectangleF(9 * u, 11 * u, 22 * u, 15 * u), 1.5f * u);
            g.DrawPath(pen, screen);
            g.DrawLine(pen, 20 * u, 26 * u, 20 * u, 30 * u);
            g.DrawLine(pen, 15 * u, 30 * u, 25 * u, 30 * u);
        }
    }
}

/// <summary>Underlined tab headers ("Dispositivos", "Ajustes").</summary>
internal sealed class TabStrip : Control
{
    private readonly Palette _p;
    private readonly string[] _items;
    private readonly List<Rectangle> _bounds = [];
    private int _hover = -1;
    private int _selected;

    public TabStrip(Palette p, params string[] items)
    {
        _p = p;
        _items = items;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Ui.TabFont;
        Height = this.S(42);
        Dock = DockStyle.Top;
        BackColor = p.Background;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? SelectedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Measure()
    {
        _bounds.Clear();
        var x = this.S(24);
        foreach (var item in _items)
        {
            var w = TextRenderer.MeasureText(item, Font).Width;
            _bounds.Add(new Rectangle(x, 0, w, Height));
            x += w + this.S(28);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Measure();
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < _items.Length; i++)
        {
            var r = _bounds[i];
            var color = i == _selected || i == _hover ? _p.Text : _p.TextSecondary;
            TextRenderer.DrawText(g, _items[i], Font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (i == _selected)
            {
                using var b = new SolidBrush(_p.Accent);
                using var bar = Ui.Rounded(new RectangleF(r.X, Height - this.S(4), r.Width, this.S(3)), this.S(1));
                g.FillPath(b, bar);
            }
        }
    }

    private int HitTest(Point pt)
    {
        Measure();
        return _bounds.FindIndex(r => r.Contains(pt));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h >= 0) Selected = h;
        base.OnMouseClick(e);
    }
}
