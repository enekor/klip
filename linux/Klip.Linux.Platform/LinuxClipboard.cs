using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Klip.Linux.Platform;

/// <summary>What was read from the clipboard: text, or an image (bytes + MIME type).</summary>
public sealed record ClipboardContent(string? Text, bool Sensitive, byte[]? Image = null, string? ImageMimeType = null);

/// <summary>
/// Linux clipboard access. Two backends: X11 (xclip + XFixes notifications) and Wayland (wl-clipboard).
/// Password managers (KeePassXC…) mark secrets with the <c>x-kde-passwordManagerHint = secret</c> target (plan §4.4).
/// </summary>
public interface ILinuxClipboard : IDisposable
{
    string Name { get; }

    /// <summary>False when the compositor forbids background watching (GNOME on Wayland): sending is manual there.</summary>
    bool CanWatch { get; }

    /// <summary>Why <see cref="CanWatch"/> is false, for the UI.</summary>
    string? WatchUnavailableReason { get; }

    /// <summary>Raised (on a background thread) when the clipboard changes, while watching.</summary>
    event Action? Changed;

    /// <summary>Starts watching. Returns false if this session does not allow it.</summary>
    Task<bool> StartWatchingAsync();

    void StopWatching();

    /// <summary>
    /// Reads the clipboard. Content marked as sensitive comes back flagged and without data, unless
    /// <paramref name="includeSensitive"/> (user opt-in): then its text is read too, still flagged.
    /// </summary>
    Task<ClipboardContent> ReadAsync(bool includeSensitive = false);

    Task WriteAsync(string text);

    Task WriteImageAsync(byte[] data, string mimeType);
}

public static class LinuxClipboard
{
    public const string PasswordHintTarget = "x-kde-passwordManagerHint";

    /// <summary>Picks the backend for this session, or null with the reason (missing tools).</summary>
    public static (ILinuxClipboard? Clipboard, string? Problem) Create()
    {
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
        var x11 = Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };
        if (wayland)
        {
            if (Shell.Exists("wl-paste") && Shell.Exists("wl-copy")) return (new WaylandClipboard(), null);
            if (!x11) return (null, "Falta wl-clipboard: instálalo (sudo apt install wl-clipboard)");
        }
        if (x11)
        {
            if (Shell.Exists("xclip")) return (new X11Clipboard(), null);
            return (null, "Falta xclip: instálalo (sudo apt install xclip)");
        }
        return (null, "No hay sesión gráfica (ni WAYLAND_DISPLAY ni DISPLAY)");
    }

    internal static bool IsSecretHint(byte[] value) => Encoding.UTF8.GetString(value).Trim() == "secret";

    /// <summary>Image formats we can send, in order of preference.</summary>
    internal static readonly string[] ImageTypes = ["image/png", "image/jpeg"];

    internal static bool IsText(string type) =>
        type.StartsWith("text/plain", StringComparison.Ordinal) || type is "UTF8_STRING" or "STRING" or "TEXT";
}

/// <summary>Wayland via wl-clipboard. Watching needs the data-control protocol (KDE, wlroots); GNOME lacks it.</summary>
public sealed class WaylandClipboard : ILinuxClipboard
{
    private Process? _watcher;

    public string Name => "Wayland (wl-clipboard)";
    public bool CanWatch { get; private set; } = true;
    public string? WatchUnavailableReason { get; private set; }
    public event Action? Changed;

    public async Task<bool> StartWatchingAsync()
    {
        if (_watcher is not null) return true;
        var psi = new ProcessStartInfo("wl-paste") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // wl-paste runs `echo x` on every change; we only need the trigger.
        foreach (var a in new[] { "--watch", "echo", "x" }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.BeginErrorReadLine();

        // Compositors without data-control make wl-paste --watch exit immediately with an error.
        if (await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(1500)) is var t && p.HasExited)
        {
            CanWatch = false;
            lock (stderr) WatchUnavailableReason = stderr.ToString().Trim() is { Length: > 0 } msg ? msg : "wl-paste --watch no está soportado";
            p.Dispose();
            return false;
        }
        _watcher = p;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Changed?.Invoke(); };
        p.BeginOutputReadLine();
        return true;
    }

    public void StopWatching()
    {
        if (_watcher is null) return;
        try { _watcher.Kill(); } catch { /* gone */ }
        _watcher.Dispose();
        _watcher = null;
    }

    public async Task<ClipboardContent> ReadAsync(bool includeSensitive = false)
    {
        var types = await Shell.RunAsync("wl-paste", ["--list-types"]);
        if (!types.Ok) return new ClipboardContent(null, false); // empty clipboard
        var list = types.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sensitive = false;
        if (list.Contains(LinuxClipboard.PasswordHintTarget))
        {
            var hint = await Shell.RunAsync("wl-paste", ["--no-newline", "--type", LinuxClipboard.PasswordHintTarget]);
            sensitive = hint.Ok && LinuxClipboard.IsSecretHint(hint.Stdout);
        }
        if (sensitive && !includeSensitive) return new ClipboardContent(null, true);
        if (list.Any(LinuxClipboard.IsText))
        {
            var text = await Shell.RunAsync("wl-paste", ["--no-newline", "--type", "text"]);
            return new ClipboardContent(text.Ok ? text.Text : null, sensitive);
        }
        if (sensitive) return new ClipboardContent(null, true); // sensitive content is only ever sent as text
        if (LinuxClipboard.ImageTypes.FirstOrDefault(list.Contains) is { } imageType)
        {
            var image = await Shell.RunAsync("wl-paste", ["--type", imageType], timeoutMs: 15_000);
            if (image.Ok && image.Stdout.Length > 0) return new ClipboardContent(null, false, image.Stdout, imageType);
        }
        return new ClipboardContent(null, false);
    }

    public async Task WriteAsync(string text)
    {
        var rc = await Shell.RunDetachedWithInputAsync("wl-copy", ["--type", "text/plain;charset=utf-8"], Encoding.UTF8.GetBytes(text));
        if (rc != 0) throw new IOException($"wl-copy terminó con código {rc}");
    }

    public async Task WriteImageAsync(byte[] data, string mimeType)
    {
        var rc = await Shell.RunDetachedWithInputAsync("wl-copy", ["--type", mimeType], data);
        if (rc != 0) throw new IOException($"wl-copy terminó con código {rc}");
    }

    public void Dispose() => StopWatching();
}

/// <summary>X11 via xclip, with change notifications from the XFixes extension (no polling of processes).</summary>
public sealed class X11Clipboard : ILinuxClipboard
{
    private Thread? _thread;
    private volatile bool _stop;

    public string Name => "X11 (xclip)";
    public bool CanWatch { get; private set; } = true;
    public string? WatchUnavailableReason { get; private set; }
    public event Action? Changed;

    public Task<bool> StartWatchingAsync()
    {
        if (_thread is not null) return Task.FromResult(true);
        var ready = new TaskCompletionSource<bool>();
        _stop = false;
        _thread = new Thread(() => WatchLoop(ready)) { IsBackground = true, Name = "klip-xfixes" };
        _thread.Start();
        return ready.Task;
    }

    public void StopWatching()
    {
        _stop = true;
        _thread?.Join(1000);
        _thread = null;
    }

    private void WatchLoop(TaskCompletionSource<bool> ready)
    {
        IntPtr display;
        try
        {
            display = X.XOpenDisplay(null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Fail(ready, "No se encontró libX11/libXfixes: " + e.Message);
            return;
        }
        if (display == IntPtr.Zero)
        {
            Fail(ready, "No se pudo abrir el display X11");
            return;
        }
        try
        {
            if (!X.XFixesQueryExtension(display, out var eventBase, out _))
            {
                Fail(ready, "El servidor X no tiene la extensión XFixes");
                return;
            }
            var clipboard = X.XInternAtom(display, "CLIPBOARD", false);
            X.XFixesSelectSelectionInput(display, X.XDefaultRootWindow(display), clipboard, X.XFixesSetSelectionOwnerNotifyMask);
            X.XFlush(display);
            ready.TrySetResult(true);

            var ev = new byte[192]; // XEvent is a union of 24 longs
            while (!_stop)
            {
                // Poll the connection every 100 ms: closing a Display from another thread is unsafe.
                while (!_stop && X.XPending(display) > 0)
                {
                    X.XNextEvent(display, ev);
                    if (BitConverter.ToInt32(ev, 0) == eventBase + X.XFixesSelectionNotify)
                    {
                        try { Changed?.Invoke(); } catch { /* subscriber errors must not kill the loop */ }
                    }
                }
                Thread.Sleep(100);
            }
        }
        finally
        {
            X.XCloseDisplay(display);
        }
    }

    private void Fail(TaskCompletionSource<bool> ready, string reason)
    {
        CanWatch = false;
        WatchUnavailableReason = reason;
        _thread = null;
        ready.TrySetResult(false);
    }

    public async Task<ClipboardContent> ReadAsync(bool includeSensitive = false)
    {
        var targets = await Shell.RunAsync("xclip", ["-selection", "clipboard", "-o", "-t", "TARGETS"]);
        if (!targets.Ok) return new ClipboardContent(null, false); // empty clipboard
        var list = targets.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sensitive = false;
        if (list.Contains(LinuxClipboard.PasswordHintTarget))
        {
            var hint = await Shell.RunAsync("xclip", ["-selection", "clipboard", "-o", "-t", LinuxClipboard.PasswordHintTarget]);
            sensitive = hint.Ok && LinuxClipboard.IsSecretHint(hint.Stdout);
        }
        if (sensitive && !includeSensitive) return new ClipboardContent(null, true);
        var target = list.Contains("UTF8_STRING") ? "UTF8_STRING" : list.Contains("text/plain;charset=utf-8") ? "text/plain;charset=utf-8" : list.Contains("STRING") ? "STRING" : null;
        if (target is not null)
        {
            var text = await Shell.RunAsync("xclip", ["-selection", "clipboard", "-o", "-t", target]);
            return new ClipboardContent(text.Ok ? text.Text : null, sensitive);
        }
        if (sensitive) return new ClipboardContent(null, true); // sensitive content is only ever sent as text
        if (LinuxClipboard.ImageTypes.FirstOrDefault(list.Contains) is { } imageType)
        {
            var image = await Shell.RunAsync("xclip", ["-selection", "clipboard", "-o", "-t", imageType], timeoutMs: 15_000);
            if (image.Ok && image.Stdout.Length > 0) return new ClipboardContent(null, false, image.Stdout, imageType);
        }
        return new ClipboardContent(null, false);
    }

    public async Task WriteAsync(string text)
    {
        // xclip forks into the background and serves the selection until someone else copies.
        var rc = await Shell.RunDetachedWithInputAsync("xclip", ["-selection", "clipboard", "-i", "-t", "UTF8_STRING"], Encoding.UTF8.GetBytes(text));
        if (rc != 0) throw new IOException($"xclip terminó con código {rc}");
    }

    public async Task WriteImageAsync(byte[] data, string mimeType)
    {
        var rc = await Shell.RunDetachedWithInputAsync("xclip", ["-selection", "clipboard", "-i", "-t", mimeType], data);
        if (rc != 0) throw new IOException($"xclip terminó con código {rc}");
    }

    public void Dispose() => StopWatching();

    private static class X
    {
        public const int XFixesSelectionNotify = 0;
        public const ulong XFixesSetSelectionOwnerNotifyMask = 1;

        [DllImport("libX11.so.6")] public static extern IntPtr XOpenDisplay(string? name);
        [DllImport("libX11.so.6")] public static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] public static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport("libX11.so.6")] public static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
        [DllImport("libX11.so.6")] public static extern int XPending(IntPtr display);
        [DllImport("libX11.so.6")] public static extern int XNextEvent(IntPtr display, byte[] ev);
        [DllImport("libX11.so.6")] public static extern int XFlush(IntPtr display);
        [DllImport("libXfixes.so.3")] public static extern bool XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
        [DllImport("libXfixes.so.3")] public static extern void XFixesSelectSelectionInput(IntPtr display, IntPtr window, IntPtr selection, ulong eventMask);
    }
}
