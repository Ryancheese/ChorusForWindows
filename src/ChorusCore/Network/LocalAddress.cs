using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ChorusCore.Network;

/// <summary>
/// Resolves the host machine's primary LAN IPv4 address so it can be displayed for
/// manual Speaker connection when Bonjour/mDNS auto-discovery is unavailable.
/// </summary>
public static class LocalAddress
{
    public static string? PrimaryIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            // Skip virtual/docker adapters that tend to grab the first hit.
            if (ni.Name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ||
                ni.Name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ||
                ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !addr.Address.Equals(IPAddress.Loopback))
                    return addr.Address.ToString();
            }
        }
        return null;
    }
}
