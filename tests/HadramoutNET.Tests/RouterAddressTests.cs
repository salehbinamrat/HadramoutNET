using HadramoutNET.Core.RouterOS;
using Xunit;

namespace HadramoutNET.Tests;

/// <summary>
/// Contract imported from reference/python-v1.1.3/core/connection_address.py.
/// The explicitly entered RouterOS API port always wins over pasted WinBox URL ports.
/// </summary>
public sealed class RouterAddressTests
{
    [Theory]
    [InlineData("192.168.88.1", "8728", "local", "192.168.88.1", 8728)]
    [InlineData("Router.LAN", "8729", "local", "Router.LAN", 8729)]
    [InlineData("[2001:db8::1]", "8728", "local", "2001:db8::1", 8728)]
    [InlineData("2001:db8::1", "8728", "local", "2001:db8::1", 8728)]
    [InlineData("https://Router.Example:8291/", "8729", "remote", "router.example", 8729)]
    [InlineData("winbox://Router.Example:8291", "18728", "remote", "router.example", 18728)]
    [InlineData("router.example:8291", "8728", "remote", "router.example", 8728)]
    [InlineData("2001:db8::1", "8728", "remote", "2001:db8::1", 8728)]
    [InlineData("[2001:db8::1]:8291", "8729", "remote", "2001:db8::1", 8729)]
    public void Accepted_addresses_keep_the_explicit_api_port(
        string address, string port, string mode, string expectedHost, int expectedPort)
    {
        var result = RouterAddress.Normalize(address, port, mode);
        Assert.Equal(expectedHost, result.Host);
        Assert.Equal(expectedPort, result.Port);
    }

    [Theory]
    [InlineData("", "8728", "local")]
    [InlineData("router lan", "8728", "local")]
    [InlineData("router\tlan", "8728", "local")]
    [InlineData("192.168.88.1:8291", "8728", "local")]
    [InlineData("http://router.example", "8728", "local")]
    [InlineData("router.example/path", "8728", "remote")]
    [InlineData("https://router.example/login", "8728", "remote")]
    [InlineData("https://user:password@router.example", "8728", "remote")]
    [InlineData("https://router.example?query=1", "8728", "remote")]
    [InlineData("ftp://router.example", "8728", "remote")]
    [InlineData("router.example", "0", "remote")]
    [InlineData("router.example", "65536", "remote")]
    [InlineData("router.example", "8291", "vpn")]
    public void Unsafe_or_ambiguous_addresses_are_rejected(string address, string port, string mode)
    {
        Assert.Throws<ArgumentException>(() => RouterAddress.Normalize(address, port, mode));
    }

    [Fact]
    public void Address_formatter_brackets_ipv6_only()
    {
        Assert.Equal("[2001:db8::1]:8729", RouterAddress.EndpointText("2001:db8::1", 8729));
        Assert.Equal("router.example:8728", RouterAddress.EndpointText("router.example", 8728));
    }
}
