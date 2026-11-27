using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Core;
using static Klip.Linux.KlipTheme;

namespace Klip.Linux;

/// <summary>Common look of Klip's small windows: themed background, fixed size, buttons aligned to the right.</summary>
internal abstract class KlipDialog : Window
{
    protected KlipDialog(string title)
    {
        Title = title;
        Icon = App.IconActive;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        this.Res(BackgroundProperty, KlipTheme.Background);
    }

    protected static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons)
        {
            b.MinWidth = 110;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            panel.Children.Add(b);
        }
        return panel;
    }
}

/// <summary>
/// Shows the 6-digit PIN. The buttons set <see cref="Accepted"/> and close explicitly, so it works the same
/// shown modal (outgoing) or modeless (incoming, closable when the other side cancels).
/// </summary>
internal sealed class PinWindow : KlipDialog
{
    public bool Accepted { get; private set; }
    public bool ClosedExternally { get; private set; }

    public PinWindow(string title, string pin, string text) : base(title)
    {
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var yes = Accented("Coincide");
        yes.IsDefault = true;
        yes.Click += (_, _) => { Accepted = true; Close(); };
        var no = new Button { Content = "No coincide", IsCancel = true };
        no.Click += (_, _) => { Accepted = false; Close(); };

        var digits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < pin.Length; i++)
        {
            if (i == 3) digits.Children.Add(new Border { Width = 10 });
            digits.Children.Add(new Border
            {
                Width = 44,
                Height = 56,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = pin[i].ToString(),
                    FontSize = 30,
                    FontWeight = FontWeight.Bold,
                    FontFamily = new FontFamily("monospace"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            }.Res(Border.BackgroundProperty, Surface).Res(Border.BorderBrushProperty, Stroke));
        }

        Content = new StackPanel
        {
            Margin = new Thickness(32, 28),
            Spacing = 16,
            Width = 400,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                digits,
                Secondary(text, 13).Also(t => { t.TextAlignment = TextAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Center; }),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Children = { yes, no } },
            },
        };
    }

    public void CloseExternally()
    {
        ClosedExternally = true;
        Close();
    }
}

internal sealed class ConfirmWindow : KlipDialog
{
    private bool _result;

    private ConfirmWindow(string title, string text, string confirm) : base("Klip")
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = Accented(confirm);
        ok.IsDefault = true;
        ok.Click += (_, _) => { _result = true; Close(); };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Spacing = 10,
            Width = 400,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap },
                Secondary(text, 13),
                Buttons(ok, cancel),
            },
        };
    }

    public static async Task<bool> AskAsync(Window owner, string title, string text, string confirm = "Aceptar")
    {
        var w = new ConfirmWindow(title, text, confirm);
        await w.ShowDialog(owner);
        return w._result;
    }
}

internal sealed class PairByIpWindow : KlipDialog
{
    private readonly TextBox _host = new() { Watermark = "192.168.1.20", Width = 220 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = Protocol.HttpPort, Width = 130, FormatString = "0" };
    private (string Host, int Port)? _result;

    private PairByIpWindow() : base("Vincular por IP")
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = Accented("Vincular");
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            var host = _host.Text?.Trim();
            if (string.IsNullOrEmpty(host)) return;
            _result = (host, (int)(_port.Value ?? Protocol.HttpPort));
            Close();
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => Close();

        static Control Field(string label, Control input) => new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeight.Medium }, input },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Spacing = 12,
            Width = 400,
            Children =
            {
                new TextBlock { Text = "Vincular por IP", FontWeight = FontWeight.SemiBold, FontSize = 16 },
                Secondary("Útil si el otro dispositivo no aparece en «Cerca de ti» (redes de invitados, aislamiento de clientes…). Debe tener la ventana de Klip abierta.", 13),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Dirección IP", _host), Field("Puerto", _port) } },
                Buttons(ok, cancel),
            },
        };
        Opened += (_, _) => _host.Focus();
    }

    public static async Task<(string Host, int Port)?> AskAsync(Window owner)
    {
        var w = new PairByIpWindow();
        await w.ShowDialog(owner);
        return w._result;
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
