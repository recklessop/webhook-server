using System.Net;
using System.Net.Sockets;

namespace WebhookServer.Core.Auth;

/// <summary>
/// Compiled allow-list of IPs and CIDR ranges. Empty list = allow all.
/// </summary>
public sealed class IpAllowList
{
    private readonly List<IPNetwork> _networks;

    public bool IsEmpty => _networks.Count == 0;

    private IpAllowList(List<IPNetwork> networks) => _networks = networks;

    public bool Contains(IPAddress address)
    {
        if (IsEmpty) return true;

        var normalized = Normalize(address);
        foreach (var net in _networks)
        {
            if (net.BaseAddress.AddressFamily != normalized.AddressFamily) continue;
            if (net.Contains(normalized)) return true;
        }
        return false;
    }

    /// <summary>
    /// Parse a list of allowlist entries. Each entry may be a single IP or a CIDR.
    /// Throws <see cref="FormatException"/> on the first invalid entry.
    /// </summary>
    public static IpAllowList Parse(IEnumerable<string> entries)
    {
        var nets = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            nets.Add(ParseEntry(entry));
        }
        return new IpAllowList(nets);
    }

    public static bool TryParse(IEnumerable<string> entries, out IpAllowList list, out string? error)
    {
        var nets = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            try
            {
                nets.Add(ParseEntry(entry));
            }
            catch (FormatException ex)
            {
                list = new IpAllowList(new List<IPNetwork>());
                error = $"invalid entry '{raw}': {ex.Message}";
                return false;
            }
        }
        list = new IpAllowList(nets);
        error = null;
        return true;
    }

    private static IPNetwork ParseEntry(string entry)
    {
        if (entry.Contains('/'))
            return IPNetwork.Parse(entry);

        if (!IPAddress.TryParse(entry, out var addr))
            throw new FormatException($"'{entry}' is not a valid IP address or CIDR");

        var prefix = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return new IPNetwork(Normalize(addr), prefix);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address;
    }
}
