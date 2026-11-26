using System.Text.Json;
using System.Text.Json.Serialization;
using Klip.Core;

namespace Klip.Node;

/// <summary>A paired device as stored on disk. The pair secret is kept protected.</summary>
public sealed class PeerConfig
{
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public string Platform { get; set; } = "unknown";
    public required string Host { get; set; }
    public int Port { get; set; } = Protocol.HttpPort;
    public bool Enabled { get; set; } = true;

    /// <summary>Base64 of the pair secret after <see cref="ISecretProtector.Protect"/>.</summary>
    public string? ProtectedSecret { get; set; }

    public long? LastSeenMs { get; set; }

    /// <summary>Version of the pair secret (docs/protocol.md §8.3).</summary>
    public long SecretIssuedAt { get; set; }

    public string SecretIssuer { get; set; } = "manual";

    [JsonIgnore]
    public SecretVersion SecretVersion => new(SecretIssuedAt, SecretIssuer);

    public MemberInfo ToMemberInfo() => new() { DeviceId = DeviceId, DeviceName = Name, Platform = Platform, HttpPort = Port, Host = Host };
}

/// <summary>A group introduction that could not be delivered yet (docs/protocol.md §8.3).</summary>
public sealed class PendingIntroduction
{
    public required string ReceiverId { get; set; }
    public required MemberInfo Member { get; set; }
    public required long IssuedAt { get; set; }
    public required string ProtectedSecret { get; set; }
    public required long CreatedAt { get; set; }
}

public sealed class NodeConfig
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceName { get; set; } = Environment.MachineName;
    public string Platform { get; set; } = "windows";
    public int HttpPort { get; set; } = Protocol.HttpPort;
    public bool SyncEnabled { get; set; } = true;

    /// <summary>Opt-in: also send text that password managers mark as sensitive. Off by default.</summary>
    public bool SendSensitive { get; set; }

    public bool StartWithWindows { get; set; } = true;
    public bool FirewallPromptShown { get; set; }
    public List<PeerConfig> Peers { get; set; } = [];
    public List<PendingIntroduction> PendingIntroductions { get; set; } = [];
    public SyncSnapshot? Sync { get; set; }
}

/// <summary>Protects secrets at rest (DPAPI on Windows, Keystore on Android).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>No protection. Only for development tools and tests.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => (byte[])data.Clone();
    public byte[] Unprotect(byte[] data) => (byte[])data.Clone();
}

/// <summary>Loads and atomically saves <see cref="NodeConfig"/> as JSON.</summary>
/// <param name="path">Config file.</param>
/// <param name="defaults">Creates the config on first run (e.g. platform = "linux").</param>
public sealed class ConfigStore(string path, Func<NodeConfig>? defaults = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();

    public string Path { get; } = path;

    public NodeConfig LoadOrCreate()
    {
        lock (_lock)
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(Path), Options) ?? CreateDefault();
            }
            var config = CreateDefault();
            SaveLocked(config);
            return config;
        }
    }

    private NodeConfig CreateDefault() => defaults?.Invoke() ?? new NodeConfig();

    public void Save(NodeConfig config)
    {
        lock (_lock) SaveLocked(config);
    }

    private void SaveLocked(NodeConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, Path, overwrite: true);
    }
}
