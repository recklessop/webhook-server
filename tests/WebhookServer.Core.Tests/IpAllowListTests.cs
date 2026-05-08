using System.Net;
using WebhookServer.Core.Auth;
using Xunit;

namespace WebhookServer.Core.Tests;

public class IpAllowListTests
{
    [Fact]
    public void Empty_list_allows_everything()
    {
        var list = IpAllowList.Parse(Array.Empty<string>());
        Assert.True(list.IsEmpty);
        Assert.True(list.Contains(IPAddress.Parse("1.2.3.4")));
        Assert.True(list.Contains(IPAddress.Parse("::1")));
    }

    [Fact]
    public void Single_v4_matches_exact_only()
    {
        var list = IpAllowList.Parse(new[] { "192.168.1.10" });
        Assert.True(list.Contains(IPAddress.Parse("192.168.1.10")));
        Assert.False(list.Contains(IPAddress.Parse("192.168.1.11")));
    }

    [Fact]
    public void V4_cidr_matches_inside_range()
    {
        var list = IpAllowList.Parse(new[] { "10.0.0.0/8" });
        Assert.True(list.Contains(IPAddress.Parse("10.10.1.42")));
        Assert.False(list.Contains(IPAddress.Parse("11.0.0.1")));
    }

    [Fact]
    public void V6_cidr_matches_inside_range()
    {
        var list = IpAllowList.Parse(new[] { "fd00::/8" });
        Assert.True(list.Contains(IPAddress.Parse("fd12:3456::1")));
        Assert.False(list.Contains(IPAddress.Parse("fc00::1")));
    }

    [Fact]
    public void Ipv4_mapped_v6_matches_v4_entry()
    {
        var list = IpAllowList.Parse(new[] { "127.0.0.1" });
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(list.Contains(mapped));
    }

    [Fact]
    public void TryParse_reports_invalid_entries()
    {
        var ok = IpAllowList.TryParse(new[] { "10.0.0.1", "garbage" }, out _, out var error);
        Assert.False(ok);
        Assert.Contains("garbage", error);
    }
}
