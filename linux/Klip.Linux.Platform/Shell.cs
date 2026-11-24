using System.Diagnostics;
using System.Text;

namespace Klip.Linux.Platform;

public sealed record ShellResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Stdout);
}

/// <summary>Runs external tools (xclip, wl-clipboard, secret-tool, avahi…) without a shell.</summary>
public static class Shell
{
    private static readonly string[] SearchPath =
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries);

    public static bool Exists(string tool) => SearchPath.Any(dir => File.Exists(Path.Combine(dir, tool)));

    /// <summary>Runs a command capturing stdout/stderr. Returns exit code 127 if it cannot be started, 124 on timeout.</summary>
    public static async Task<ShellResult> RunAsync(string file, IEnumerable<string> args, byte[]? stdin = null, int timeoutMs = 5000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ShellResult(127, [], e.Message);
        }
        using (p)
        {
            if (stdin is not null)
            {
                await p.StandardInput.BaseStream.WriteAsync(stdin);
                p.StandardInput.Close();
            }
            using var ms = new MemoryStream();
            var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
                await outTask;
                return new ShellResult(p.ExitCode, ms.ToArray(), await errTask);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ShellResult(124, ms.ToArray(), "timeout");
            }
        }
    }

    /// <summary>
    /// Starts a tool that forks itself into the background to keep serving data (xclip -i, wl-copy).
    /// Only stdin is redirected: a forked child holding our stdout pipe would otherwise block us forever.
    /// </summary>
    public static async Task<int> RunDetachedWithInputAsync(string file, IEnumerable<string> args, byte[] stdin, int timeoutMs = 5000)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardInput.BaseStream.WriteAsync(stdin);
        p.StandardInput.Close();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0; // still running in the foreground = still serving the clipboard
        }
    }
}
