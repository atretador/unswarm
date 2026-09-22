using System.Net;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class NetworkAddressPolicyTests
{
    [Theory]
    // IPv4 private / reserved
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6 private / reserved
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    // IPv4-mapped IPv6 must be unmapped first
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    // IPv4-embedding IPv6 ranges
    [InlineData("::10.0.0.1")]        // ::/96 IPv4-compatible
    [InlineData("::192.168.1.1")]
    [InlineData("64:ff9b::1")]         // NAT64
    [InlineData("64:ff9b::c0a8:101")]
    [InlineData("2002::1")]            // 6to4
    [InlineData("2002:c0a8:101::1")]
    public void IsPrivateOrReserved_BlocksNonPublicAddresses(string address)
        => Assert.True(NetworkAddressPolicy.IsPrivateOrReserved(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPrivateOrReserved_AllowsPublicAddresses(string address)
        => Assert.False(NetworkAddressPolicy.IsPrivateOrReserved(IPAddress.Parse(address)));

    [Fact]
    public void MatchesAllowedHost_ExactAndSuffix()
    {
        var allowed = new[] { "gpu.lan", ".corp.example.com" };

        Assert.True(NetworkAddressPolicy.MatchesAllowedHost("gpu.lan", allowed));
        Assert.True(NetworkAddressPolicy.MatchesAllowedHost("GPU.LAN", allowed));
        Assert.True(NetworkAddressPolicy.MatchesAllowedHost("a.corp.example.com", allowed));
        Assert.True(NetworkAddressPolicy.MatchesAllowedHost("deep.a.corp.example.com", allowed));

        Assert.False(NetworkAddressPolicy.MatchesAllowedHost("corp.example.com", allowed));
        Assert.False(NetworkAddressPolicy.MatchesAllowedHost("evilcorp.example.com", allowed));
        Assert.False(NetworkAddressPolicy.MatchesAllowedHost("gpu.lan.evil.com", allowed));
        Assert.False(NetworkAddressPolicy.MatchesAllowedHost("other.lan", allowed));
        Assert.False(NetworkAddressPolicy.MatchesAllowedHost(null, allowed));
    }
}
