using System.Net.Sockets;
using System.Text;
using Klip.Core;
using Klip.Node;

namespace Klip.Linux.Platform;

public static class LinuxPaths
{
    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : Path.Combine(Home, fallback);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigDir => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "klip");
    public static string Config => Path.Combine(ConfigDir, "config.json");
    public static string StateDir => Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "klip");
    public static string LogFile => Path.Combine(StateDir, "klip.log");
    public static string AutostartFile => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "autostart", "klip.desktop");

    public static string Socket =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run
            ? Path.Combine(run, "klip.sock")
            : Path.Combine(Path.GetTempPath(), $"klip-{Environment.UserName}.sock");
}

/// <summary>Tiny rotating file log (1 MB, one backup).</summary>
public static class FileLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LinuxPaths.StateDir);
                var info = new FileInfo(LinuxPaths.LogFile);
                if (info.Exists && info.Length > 1024 * 1024) File.Move(LinuxPaths.LogFile, LinuxPaths.LogFile + ".1", overwrite: true);
                File.AppendAllText(LinuxPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }
}

/// <summary>
/// Pair secrets at rest: AES-GCM with a master key kept in the desktop keyring (Secret Service via secret-tool:
/// GNOME Keyring / KWallet). Without a keyring, the key lives in a 0600 file next to the config.
/// Output = iv(12) || ct.
/// </summary>
public sealed class LinuxSecretProtector : ISecretProtector
{
    private static readonly string[] Attributes = ["service", "klip", "account", "pair-secrets"];
    private readonly Lazy<byte[]> _key;

    private string _storage = "?";

    /// <summary>Where the master key lives (loads it on first access).</summary>
    public string Storage
    {
        get { _ = _key.Value; return _storage; }
        private set => _storage = value;
    }

    public LinuxSecretProtector() => _key = new Lazy<byte[]>(LoadOrCreateKey);

    public byte[] Protect(byte[] data)
    {
        var iv = KlipCrypto.RandomBytes(Protocol.AesGcmIvBytes);
        return [.. iv, .. KlipCrypto.AesGcmEncrypt(_key.Value, iv, [], data)];
    }

    public byte[] Unprotect(byte[] data) =>
        KlipCrypto.AesGcmDecrypt(_key.Value, data[..Protocol.AesGcmIvBytes], [], data[Protocol.AesGcmIvBytes..]);

    private byte[] LoadOrCreateKey()
    {
        if (Shell.Exists("secret-tool"))
        {
            var found = Shell.RunAsync("secret-tool", ["lookup", .. Attributes], timeoutMs: 30_000).GetAwaiter().GetResult();
            if (found.Ok && TryDecode(found.Text.Trim(), out var existing))
            {
                Storage = "llavero del escritorio";
                return existing;
            }
            var key = KlipCrypto.RandomBytes(32);
            var stored = Shell.RunAsync("secret-tool", ["store", "--label=Klip (claves de vinculación)", .. Attributes],
                Encoding.UTF8.GetBytes(Convert.ToBase64String(key)), timeoutMs: 30_000).GetAwaiter().GetResult();
            if (stored.Ok)
            {
                Storage = "llavero del escritorio";
                return key;
            }
            FileLog.Write($"secret-tool no disponible ({stored.Stderr.Trim()}): se usa un archivo de clave");
        }
        return FileKey();
    }

    private byte[] FileKey()
    {
        Storage = "archivo protegido (0600)";
        var path = Path.Combine(LinuxPaths.ConfigDir, "master.key");
        if (File.Exists(path) && TryDecode(File.ReadAllText(path).Trim(), out var existing)) return existing;
        Directory.CreateDirectory(LinuxPaths.ConfigDir);
        var key = KlipCrypto.RandomBytes(32);
        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(path, Convert.ToBase64String(key));
        return key;
    }

    private static bool TryDecode(string base64, out byte[] key)
    {
        key = [];
        try { key = Convert.FromBase64String(base64); }
        catch (FormatException) { return false; }
        return key.Length == 32;
    }
}

/// <summary>"Start with the session" through the XDG autostart directory.</summary>
public static class Autostart
{
    /// <summary>The command that starts this build (AppImage path, or the installed binary).</summary>
    public static string LaunchCommand =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath ?? "klip";

    public static bool IsEnabled => File.Exists(LinuxPaths.AutostartFile);

    public static void Apply(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(LinuxPaths.AutostartFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(LinuxPaths.AutostartFile)!);
        File.WriteAllText(LinuxPaths.AutostartFile, $"""
            [Desktop Entry]
            Type=Application
            Name=Klip
            Comment=Sincronizador local de portapapeles
            Exec="{LaunchCommand}" --minimized
            Icon=klip
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """);
    }
}

/// <summary>Desktop notifications through notify-send (libnotify). Silently does nothing without it.</summary>
public static class Notifier
{
    public static void Show(string title, string body)
    {
        if (!Shell.Exists("notify-send")) return;
        _ = Shell.RunAsync("notify-send", ["--app-name=Klip", "--icon=klip", title, body]);
    }
}

/// <summary>Desktop environment facts that change behaviour.</summary>
public static class DesktopSession
{
    public static string Kind =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } ? "Wayland" :
        Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? "X11" : "sin sesión gráfica";

    public static string Desktop => Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "desconocido";

    /// <summary>Whether a StatusNotifierItem host exists (KDE, or GNOME with the AppIndicator extension).</summary>
    public static async Task<bool> HasTrayAsync()
    {
        if (!Shell.Exists("gdbus")) return true; // cannot tell: assume yes
        var r = await Shell.RunAsync("gdbus", ["call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
            "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher"]);
        return !r.Ok || r.Text.Contains("true");
    }
}

/// <summary>
/// Single instance + commands from the CLI (`klip --send-clipboard` bound to a keyboard shortcut) over a Unix socket.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket _server;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Socket server, Action<string> onCommand)
    {
        _server = server;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var client = await _server.AcceptAsync(_cts.Token);
                    var buffer = new byte[256];
                    var n = await client.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                    onCommand(Encoding.UTF8.GetString(buffer, 0, n).Trim());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // a client went away: keep serving
                }
            }
        });
    }

    /// <summary>
    /// Becomes the primary instance, or forwards <paramref name="command"/> to the running one and returns null.
    /// </summary>
    public static SingleInstance? Acquire(string command, Action<string> onCommand)
    {
        var path = LinuxPaths.Socket;
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send(Encoding.UTF8.GetBytes(command));
            return null;
        }
        catch (SocketException)
        {
            // Nobody listening: stale socket file or first run.
        }
        File.Delete(path);
        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        server.Listen(4);
        return new SingleInstance(server, onCommand);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Dispose();
        try { File.Delete(LinuxPaths.Socket); } catch (IOException) { }
    }
}
