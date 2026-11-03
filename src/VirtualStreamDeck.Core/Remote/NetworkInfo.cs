using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VirtualStreamDeck.Core.Remote;

public sealed record LanAddress(string Address, string InterfaceName)
{
    public override string ToString() => $"{Address}  ({InterfaceName})";
}

public static class NetworkInfo
{
    private static readonly string[] VirtualHints =
        { "vEthernet", "VirtualBox", "VMware", "Hyper-V", "WSL", "Docker", "Loopback", "Bluetooth", "TAP-", "Npcap" };

    /// <summary>IPv4 addresses a phone on the same network can reach, best guess first.</summary>
    public static IReadOnlyList<LanAddress> GetLanAddresses()
    {
        var found = new List<(LanAddress Address, int Score)>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return Array.Empty<LanAddress>(); }

        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            bool looksVirtual = VirtualHints.Any(h =>
                ni.Name.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains(h, StringComparison.OrdinalIgnoreCase));

            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local, no DHCP

                int score = 0;
                if (hasGateway) score += 10;
                if (looksVirtual) score -= 8;
                if (IsPrivate(b)) score += 3;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet) score += 1;
                found.Add((new LanAddress(ip.ToString(), ni.Name), score));
            }
        }

        return found.OrderByDescending(f => f.Score).Select(f => f.Address).ToList();
    }

    private static bool IsPrivate(byte[] b) =>
        b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
}
