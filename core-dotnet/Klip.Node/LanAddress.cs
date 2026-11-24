using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Klip.Node;

/// <summary>Picks the LAN addresses other devices can reach, skipping virtual adapters (plan §4.2).</summary>
public static class LanAddress
{
    private static readonly string[] VirtualHints =
        ["virtual", "hyper-v", "vethernet", "wsl", "vmware", "virtualbox", "vpn", "tap", "tun", "loopback", "docker", "vbox"];

    /// <summary>IPv4 addresses of physical, up interfaces with an IPv4 default gateway (Wi-Fi first).</summary>
    public static IReadOnlyList<IPAddress> LocalIPv4()
    {
        var result = new List<(IPAddress Ip, bool Wifi)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var desc = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (VirtualHints.Any(desc.Contains)) continue;
            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.All(g => g.Address.AddressFamily != AddressFamily.InterNetwork || g.Address.Equals(IPAddress.Any))) continue;
            var wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            result.AddRange(props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (a.Address, wifi)));
        }
        return result.OrderByDescending(r => r.Wifi).Select(r => r.Ip).ToList();
    }

    public static IPAddress? PreferredIPv4() => LocalIPv4().FirstOrDefault();
}
