namespace Klip.Windows;

internal static class Program
{
    private const string MutexName = @"Local\Klip.Windows.SingleInstance";
    private const string ShowEventName = @"Local\Klip.Windows.Show";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirstInstance)
        {
            // Ask the running instance to open its configuration window.
            showEvent.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new TrayApp(showEvent));
        GC.KeepAlive(mutex);
    }
}
