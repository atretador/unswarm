using System.Net;
using System.Net.Sockets;

namespace Unswarm.Core.Services;

/// <summary>
/// SSRF egress policy: classifies IP addresses that must not be reachable by the
/// cloud-provider HTTP client unless the operator explicitly opts in.
/// </summary>
public static class NetworkAddressPolicy
{
    /// <summary>
    /// True when <paramref name="address"/> is loopback, link-local, private,
    /// shared/CGNAT, documentation, multicast, reserved, or otherwise not a
    /// public unicast address. IPv4-mapped IPv6 addresses are unmapped first.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPrivateOrReservedV4(address.GetAddressBytes());

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return IsPrivateOrReservedV6(address);

        // Unknown family — fail closed.
        return true;
    }

    private static bool IsPrivateOrReservedV4(byte[] b)
    {
        // 0.0.0.0/8
        if (b[0] == 0) return true;
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 100.64.0.0/10 (CGNAT / shared address space)
        if (b[0] == 100 && (b[1] & 0xC0) == 0x40) return true;
        // 127.0.0.0/8 (loopback)
        if (b[0] == 127) return true;
        // 169.254.0.0/16 (link-local)
        if (b[0] == 169 && b[1] == 254) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && (b[1] & 0xF0) == 0x10) return true;
        // 192.0.0.0/24
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;
        // 192.0.2.0/24 (TEST-NET-1)
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        // 198.18.0.0/15 (benchmarking)
        if (b[0] == 198 && (b[1] & 0xFE) == 18) return true;
        // 198.51.100.0/24 (TEST-NET-2)
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
        // 203.0.113.0/24 (TEST-NET-3)
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
        // 224.0.0.0/4 (multicast)
        if ((b[0] & 0xF0) == 0xE0) return true;
        // 240.0.0.0/4 (reserved) — includes 255.255.255.255
        if ((b[0] & 0xF0) == 0xF0) return true;

        return false;
    }

    private static bool IsPrivateOrReservedV6(IPAddress address)
    {
        // :: (unspecified) and ::1 (loopback)
        if (address.Equals(IPAddress.IPv6Any)) return true;
        if (IPAddress.IsLoopback(address)) return true;

        var b = address.GetAddressBytes();

        // ::/96 (IPv4-compatible, e.g. ::10.0.0.1). :: and ::1 are already
        // handled above; this also catches the remaining compatible range.
        if (IsAllZero(b, 0, 12)) return true;
        // 64:ff9b::/96 (NAT64 well-known prefix — embeds IPv4)
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return true;
        // 2002::/16 (6to4 — embeds IPv4)
        if (b[0] == 0x20 && b[1] == 0x02) return true;
        // fe80::/10 (link-local)
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
        // fc00::/7 (unique local)
        if ((b[0] & 0xFE) == 0xFC) return true;
        // ff00::/8 (multicast)
        if (b[0] == 0xFF) return true;
        // 2001:db8::/32 (documentation)
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;

        return false;
    }

    private static bool IsAllZero(byte[] bytes, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (bytes[i] != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Host allow-list matching for <c>CloudProviders:AllowedPrivateHosts</c>.
    /// Matching is case-insensitive and port-agnostic: an entry matches the host
    /// exactly, or as a ".suffix" of the host.
    /// </summary>
    public static bool MatchesAllowedHost(string? host, IEnumerable<string>? allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(host) || allowedHosts is null) return false;

        var candidate = host.Trim().TrimEnd('.');

        foreach (var raw in allowedHosts)
        {
            var entry = raw?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(entry)) continue;

            if (entry.StartsWith('.'))
            {
                // ".suffix" matches "sub.suffix" (and deeper), not "suffix".
                if (candidate.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (candidate.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
