using Avalonia;
using Klip.Linux.Platform;

namespace Klip.Linux;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Klip — sincronizador local de portapapeles

                  klip                    Abre Klip (o muestra la ventana si ya está abierto)
                  klip --minimized        Arranca en segundo plano (inicio de sesión)
                  klip --send-clipboard   Envía el portapapeles actual a tus dispositivos.
                                          Asígnalo a un atajo de teclado en GNOME (Wayland),
                                          donde Klip no puede vigilar el portapapeles solo.
                """);
            return 0;
        }

        var command = args.Contains("--send-clipboard") ? "send-clipboard" : "show";
        var instance = SingleInstance.Acquire(command, App.EnqueueCommand);
        if (instance is null) return 0; // forwarded to the running instance

        App.StartHidden = args.Contains("--minimized") || command == "send-clipboard";
        if (command == "send-clipboard") App.EnqueueCommand(command);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            instance.Dispose();
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "klip" })
            .LogToTrace();
}
