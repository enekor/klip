using System.Text;
using Klip.Linux.Platform;

namespace Klip.Linux.Platform.Tests;

/// <summary>
/// Real clipboard round-trips. Run through linux/test-clipboard.sh, which starts Xvfb (X11) and a headless sway
/// (Wayland). Each backend's tests are skipped (pass trivially) when its display is not available.
/// </summary>
public abstract class ClipboardContract
{
    protected abstract ILinuxClipboard? Create();

    /// <summary>Puts <paramref name="value"/> on the clipboard with only the given MIME type / X target.</summary>
    protected abstract Task SetExternallyAsync(string type, string value);

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out: " + what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task WriteThenReadRoundTripsUnicodeAndNewlines()
    {
        using var cb = Create();
        if (cb is null) return;
        const string text = "hola ñ € 😀\nsegunda línea";
        await cb.WriteAsync(text);
        await Task.Delay(200);
        var read = await cb.ReadAsync();
        Assert.False(read.Sensitive);
        Assert.Equal(text, read.Text);
    }

    [Fact]
    public async Task ImagesRoundTripExactBytes()
    {
        using var cb = Create();
        if (cb is null) return;
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        await cb.WriteImageAsync(png, "image/png");
        await Task.Delay(200);
        var read = await cb.ReadAsync();
        Assert.Null(read.Text);
        Assert.Equal("image/png", read.ImageMimeType);
        Assert.Equal(png, read.Image);
    }

    [Fact]
    public async Task WatchReportsExternalChanges()
    {
        using var cb = Create();
        if (cb is null) return;
        var changes = 0;
        cb.Changed += () => Interlocked.Increment(ref changes);
        Assert.True(await cb.StartWatchingAsync(), cb.WatchUnavailableReason);
        await Task.Delay(300);
        var before = changes;
        await SetExternallyAsync("text/plain;charset=utf-8", "copiado por otra app");
        await WaitUntil(() => changes > before, "change notification");
        Assert.Equal("copiado por otra app", (await cb.ReadAsync()).Text);
        cb.StopWatching();
    }

    [Fact]
    public async Task PasswordManagerHintIsSensitive()
    {
        using var cb = Create();
        if (cb is null) return;
        await SetExternallyAsync(LinuxClipboard.PasswordHintTarget, "secret");
        await Task.Delay(200);
        var read = await cb.ReadAsync();
        Assert.True(read.Sensitive);
        Assert.Null(read.Text);
    }
}

public sealed class X11ClipboardTests : ClipboardContract
{
    protected override ILinuxClipboard? Create() =>
        Environment.GetEnvironmentVariable("KLIP_TEST_X11") == "1" ? new X11Clipboard() : null;

    protected override async Task SetExternallyAsync(string type, string value)
    {
        var target = type == "text/plain;charset=utf-8" ? "UTF8_STRING" : type;
        await Shell.RunDetachedWithInputAsync("xclip", ["-selection", "clipboard", "-i", "-t", target], Encoding.UTF8.GetBytes(value));
    }
}

public sealed class WaylandClipboardTests : ClipboardContract
{
    protected override ILinuxClipboard? Create() =>
        Environment.GetEnvironmentVariable("KLIP_TEST_WAYLAND") == "1" ? new WaylandClipboard() : null;

    protected override async Task SetExternallyAsync(string type, string value) =>
        await Shell.RunDetachedWithInputAsync("wl-copy", ["--type", type], Encoding.UTF8.GetBytes(value));
}

public sealed class SystemTests
{
    [Fact]
    public void SecretProtectorRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("klip-linux").FullName;
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            var p = new LinuxSecretProtector();
            var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var sealedBytes = p.Protect(secret);
            Assert.NotEqual(secret, sealedBytes[12..]);
            Assert.Equal(secret, p.Unprotect(sealedBytes));
            // A new instance (next app start) reads the same key.
            Assert.Equal(secret, new LinuxSecretProtector().Unprotect(sealedBytes));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SingleInstanceForwardsCommands()
    {
        var dir = Directory.CreateTempSubdirectory("klip-run").FullName;
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", dir);
        try
        {
            var received = new List<string>();
            var gotOne = new ManualResetEventSlim();
            using var primary = SingleInstance.Acquire("show", c => { lock (received) received.Add(c); gotOne.Set(); });
            Assert.NotNull(primary);
            Assert.Null(SingleInstance.Acquire("send-clipboard", _ => { }));
            Assert.True(gotOne.Wait(3000));
            Assert.Equal(["send-clipboard"], received);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Directory.Delete(dir, true);
        }
    }
}
