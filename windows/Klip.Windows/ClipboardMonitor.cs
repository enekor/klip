using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Klip.Node;

namespace Klip.Windows;

/// <summary>What was copied: text, an image (PNG bytes), or nothing usable; plus the sensitive flag.</summary>
internal sealed record ClipboardRead(string? Text, byte[]? Png, bool Sensitive);

/// <summary>
/// Listens for WM_CLIPBOARDUPDATE on a message-only window and reports copied text or images.
/// Debounces bursts, ignores our own writes and flags content marked as sensitive (plan §4.1, §4.4).
/// Must be created on the UI (STA) thread.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly uint FmtExcludeFromMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint FmtCanUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    private static readonly uint FmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint FmtPng = RegisterClipboardFormat("PNG");

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private uint _lastSequence;

    /// <summary>Raised on the UI thread with what was copied.</summary>
    public event Action<ClipboardRead>? Copied;

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
        var read = Read();
        if (read.Text is not null || read.Png is not null || read.Sensitive) Copied?.Invoke(read);
    }

    private static bool OwnedByThisProcess()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out var pid);
        return pid == Environment.ProcessId;
    }

    private ClipboardRead Read()
    {
        var convertBitmap = false;
        if (!OpenWithRetries()) return new ClipboardRead(null, null, false);
        try
        {
            var sensitive = IsClipboardFormatAvailable(FmtExcludeFromMonitor)
                            || IsClipboardFormatAvailable(FmtViewerIgnore)
                            || DwordFormatIsZero(FmtCanIncludeInHistory)
                            || DwordFormatIsZero(FmtCanUploadToCloud);
            if (sensitive) return new ClipboardRead(null, null, true);

            // Text wins when both are present (e.g. a cell copied from a spreadsheet).
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadGlobal(CF_UNICODETEXT, ptr => Marshal.PtrToStringUni(ptr));
                return new ClipboardRead(text, null, false);
            }
            // Browsers and Office publish the original PNG: take it as is.
            if (FmtPng != 0 && IsClipboardFormatAvailable(FmtPng))
            {
                var png = ReadGlobalBytes(FmtPng);
                if (png is { Length: > 0 }) return new ClipboardRead(null, png, false);
            }
            convertBitmap = IsClipboardFormatAvailable(CF_DIBV5) || IsClipboardFormatAvailable(CF_DIB);
        }
        finally
        {
            CloseClipboard();
        }

        // Screenshots (Win+Shift+S, Print Screen) are only a bitmap: convert to PNG. Clipboard.GetImage opens
        // the clipboard itself, so this must happen after CloseClipboard.
        if (convertBitmap)
        {
            try
            {
                using var image = Clipboard.GetImage();
                if (image is not null) return new ClipboardRead(null, ToPng(image), false);
            }
            catch (ExternalException e)
            {
                FileLog.Write("No se pudo leer la imagen del portapapeles: " + e.Message);
            }
        }
        return new ClipboardRead(null, null, false);
    }

    internal static byte[] ToPng(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
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

    private static T? ReadGlobal<T>(uint format, Func<IntPtr, T> read)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return default;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return default;
        try { return read(ptr); }
        finally { GlobalUnlock(handle); }
    }

    private static byte[]? ReadGlobalBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (int)GlobalSize(handle).ToUInt64();
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool DwordFormatIsZero(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return false;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero || GlobalSize(handle).ToUInt64() < 4) return false;
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

/// <summary>Writes received content on the UI (STA) thread: text with CRLF, images as PNG + bitmap.</summary>
internal sealed class WindowsClipboardWriter(SynchronizationContext ui) : IClipboardWriter
{
    public void WriteText(string text)
    {
        var windowsText = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        Run(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, windowsText), copy: true, retryTimes: 5, retryDelay: 50));
    }

    public void WriteImage(byte[] data, string mimeType)
    {
        Run(() =>
        {
            using var source = Image.FromStream(new MemoryStream(data));
            using var bitmap = new Bitmap(source);
            var png = mimeType == "image/png" ? data : ClipboardMonitor.ToPng(bitmap);
            var dataObject = new DataObject();
            dataObject.SetImage(bitmap);                       // CF_BITMAP / CF_DIB: every app can paste it
            dataObject.SetData("PNG", false, new MemoryStream(png)); // keeps transparency for apps that read PNG
            Clipboard.SetDataObject(dataObject, copy: true, retryTimes: 5, retryDelay: 50);
        });
    }

    private void Run(Action action)
    {
        Exception? error = null;
        ui.Send(_ =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        }, null);
        if (error is not null) throw error;
    }
}
