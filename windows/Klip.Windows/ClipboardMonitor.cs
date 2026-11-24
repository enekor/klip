using System.Runtime.InteropServices;
using Klip.Node;

namespace Klip.Windows;

/// <summary>
/// Listens for WM_CLIPBOARDUPDATE on a message-only window and reports copied text.
/// Debounces bursts, ignores our own writes and flags content marked as sensitive (plan §4.1, §4.4).
/// Must be created on the UI (STA) thread.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly uint FmtExcludeFromMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint FmtCanUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    private static readonly uint FmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private uint _lastSequence;

    /// <summary>Raised on the UI thread with the copied text and whether it is sensitive.</summary>
    public event Action<string?, bool>? Copied;

    public bool IsListening { get; private set; }

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        _debounce.Tick += (_, _) => { _debounce.Stop(); Process(); };
    }

    public void Start()
    {
        if (IsListening) return;
        _lastSequence = GetClipboardSequenceNumber(); // don't send whatever was already on the clipboard
        IsListening = AddClipboardFormatListener(Handle);
    }

    public void Stop()
    {
        if (!IsListening) return;
        RemoveClipboardFormatListener(Handle);
        _debounce.Stop();
        IsListening = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        base.WndProc(ref m);
    }

    private void Process()
    {
        var seq = GetClipboardSequenceNumber();
        if (seq == _lastSequence) return;
        _lastSequence = seq;
        if (OwnedByThisProcess()) return; // content we just wrote ourselves
        var (text, sensitive) = Read();
        if (text is not null || sensitive) Copied?.Invoke(text, sensitive);
    }

    private static bool OwnedByThisProcess()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out var pid);
        return pid == Environment.ProcessId;
    }

    private (string? Text, bool Sensitive) Read()
    {
        if (!OpenWithRetries()) return (null, false);
        try
        {
            var sensitive = IsClipboardFormatAvailable(FmtExcludeFromMonitor)
                            || IsClipboardFormatAvailable(FmtViewerIgnore)
                            || DwordFormatIsZero(FmtCanIncludeInHistory)
                            || DwordFormatIsZero(FmtCanUploadToCloud);
            if (sensitive) return (null, true);
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return (null, false);
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return (null, false);
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return (null, false);
            try { return (Marshal.PtrToStringUni(ptr), false); }
            finally { GlobalUnlock(handle); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Another app may hold the clipboard briefly (CLIPBRD_E_CANT_OPEN): retry 5 times every 50 ms.</summary>
    private bool OpenWithRetries()
    {
        for (var i = 0; i < 5; i++)
        {
            if (OpenClipboard(Handle)) return true;
            Thread.Sleep(50);
        }
        FileLog.Write("No se pudo abrir el portapapeles tras 5 intentos");
        return false;
    }

    private static bool DwordFormatIsZero(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return false;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero || GlobalSize(handle) < 4) return false;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return false;
        try { return Marshal.ReadInt32(ptr) == 0; }
        finally { GlobalUnlock(handle); }
    }

    public void Dispose()
    {
        Stop();
        _debounce.Dispose();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
}

/// <summary>Writes received text on the UI (STA) thread, converting LF to CRLF.</summary>
internal sealed class WindowsClipboardWriter(SynchronizationContext ui) : IClipboardWriter
{
    public void WriteText(string text)
    {
        var windowsText = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        Exception? error = null;
        ui.Send(_ =>
        {
            try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, windowsText), copy: true, retryTimes: 5, retryDelay: 50); }
            catch (Exception e) { error = e; }
        }, null);
        if (error is not null) throw error;
    }
}
