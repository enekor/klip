using System.Diagnostics;
using System.Security.Cryptography;
using Klip.Core;
using Klip.Node;
using Microsoft.Win32;

namespace Klip.Windows;

/// <summary>Secrets at rest protected with DPAPI for the current Windows user.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "klip-pair-secret-v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip");
    public static string Config => Path.Combine(Root, "config.json");
    public static string LogFile => Path.Combine(Root, "logs", "klip.log");
}

/// <summary>Tiny rotating file log (1 MB, one backup).</summary>
internal static class FileLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
                var info = new FileInfo(AppPaths.LogFile);
                if (info.Exists && info.Length > MaxBytes) File.Move(AppPaths.LogFile, AppPaths.LogFile + ".1", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Klip";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Inbound firewall rules for the Private profile (plan §4.2).</summary>
internal static class Firewall
{
    private static readonly string HttpRule = $"Klip HTTP (TCP {Protocol.HttpPort})";
    private static readonly string DiscoveryRule = $"Klip descubrimiento (UDP {Protocol.DiscoveryPort})";

    public static bool RulesExist() => RuleExists(HttpRule) && RuleExists(DiscoveryRule);

    /// <summary>Creates both rules in an elevated process (UAC prompt). Returns false if the user cancelled.</summary>
    public static bool CreateRules()
    {
        var cmd =
            $"netsh advfirewall firewall delete rule name=\"{HttpRule}\" >nul & " +
            $"netsh advfirewall firewall delete rule name=\"{DiscoveryRule}\" >nul & " +
            $"netsh advfirewall firewall add rule name=\"{HttpRule}\" dir=in action=allow protocol=TCP localport={Protocol.HttpPort} profile=private & " +
            $"netsh advfirewall firewall add rule name=\"{DiscoveryRule}\" dir=in action=allow protocol=UDP localport={Protocol.DiscoveryPort} profile=private";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(30_000);
            return RulesExist();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC cancelled
        }
    }

    private static bool RuleExists(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal static class NetworkInfo
{
    /// <summary>IPv4 addresses of physical, up interfaces with a default gateway.</summary>
    public static IReadOnlyList<string> LocalIPv4() => LanAddress.LocalIPv4().Select(a => a.ToString()).ToList();
}
