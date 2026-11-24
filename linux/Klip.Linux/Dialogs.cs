using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Core;

namespace Klip.Linux;

/// <summary>
/// Shows the 6-digit PIN. The buttons set <see cref="Accepted"/> and close explicitly, so it works the same
/// shown modal (outgoing) or modeless (incoming, closable when the other side cancels).
/// </summary>
internal sealed class PinWindow : Window
{
    public bool Accepted { get; private set; }
    public bool ClosedExternally { get; private set; }

    public PinWindow(string title, string pin, string text)
    {
        Title = title;
        Icon = App.IconActive;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var yes = new Button { Content = "Coincide", IsDefault = true, Classes = { "accent" } };
        yes.Click += (_, _) => { Accepted = true; Close(); };
        var no = new Button { Content = "No coincide", IsCancel = true };
        no.Click += (_, _) => { Accepted = false; Close(); };

        Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = $"{pin[..3]} {pin[3..]}",
                    FontSize = 44,
                    FontWeight = FontWeight.Bold,
                    FontFamily = new FontFamily("monospace"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Center },
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

internal sealed class ConfirmWindow : Window
{
    private bool _result;

    private ConfirmWindow(string title, string text)
    {
        Title = "Klip";
        Icon = App.IconActive;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = "Aceptar", IsDefault = true };
        ok.Click += (_, _) => { _result = true; Close(); };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
    }

    public static async Task<bool> AskAsync(Window owner, string title, string text)
    {
        var w = new ConfirmWindow(title, text);
        await w.ShowDialog(owner);
        return w._result;
    }
}

internal sealed class PairByIpWindow : Window
{
    private readonly TextBox _host = new() { Watermark = "192.168.1.20", Width = 200 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = Protocol.HttpPort, Width = 140, FormatString = "0" };
    private (string Host, int Port)? _result;

    private PairByIpWindow()
    {
        Title = "Vincular por IP";
        Icon = App.IconActive;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = "Vincular", IsDefault = true };
        ok.Click += (_, _) =>
        {
            var host = _host.Text?.Trim();
            if (string.IsNullOrEmpty(host)) return;
            _result = (host, (int)(_port.Value ?? Protocol.HttpPort));
            Close();
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "El otro dispositivo debe tener Klip abierto." },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "IP:", Width = 60, VerticalAlignment = VerticalAlignment.Center }, _host } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Puerto:", Width = 60, VerticalAlignment = VerticalAlignment.Center }, _port } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
    }

    public static async Task<(string Host, int Port)?> AskAsync(Window owner)
    {
        var w = new PairByIpWindow();
        await w.ShowDialog(owner);
        return w._result;
    }
}
