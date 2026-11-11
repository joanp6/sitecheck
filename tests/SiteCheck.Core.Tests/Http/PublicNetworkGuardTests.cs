using System.Net;
using SiteCheck.Http;

namespace SiteCheck.Core.Tests.Http;

public sealed class PublicNetworkGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]        // this machine
    [InlineData("127.8.9.10")]       // all of 127/8 is loopback, not just .0.0.1
    [InlineData("0.0.0.0")]          // "any address", which connects to this machine
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]      // the router
    [InlineData("169.254.169.254")]  // where cloud machines serve their credentials
    [InlineData("100.64.0.1")]       // carrier-grade NAT
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]        // multicast
    [InlineData("255.255.255.255")]  // broadcast
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]          // link-local
    [InlineData("fc00::1")]          // unique local, IPv6's 10.x
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]          // multicast
    [InlineData("::ffff:127.0.0.1")] // IPv4 in an IPv6 coat
    [InlineData("::ffff:192.168.1.1")]
    public void IsPublic_ForThisMachineOrAPrivateNetwork_IsFalse(string address)
    {
        Assert.False(PublicNetworkGuard.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]   // just below the private 172.16/12
    [InlineData("172.32.0.1")]       // just above it
    [InlineData("100.63.255.255")]   // just below carrier-grade NAT
    [InlineData("100.128.0.1")]      // just above it
    [InlineData("192.169.0.1")]
    [InlineData("169.253.0.1")]
    [InlineData("192.0.1.1")]
    [InlineData("223.255.255.255")]  // the last address before multicast
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPublic_ForAnAddressOnTheInternet_IsTrue(string address)
    {
        Assert.True(PublicNetworkGuard.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public void Allowed_WhenEveryAddressIsPrivate_RefusesAndSaysHowToAllowIt()
    {
        var refused = Assert.Throws<PrivateAddressException>(
            () => PublicNetworkGuard.Allowed("intranet.test", [IPAddress.Parse("10.0.0.5")], allowPrivate: false));

        Assert.Contains("intranet.test", refused.Message, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5", refused.Message, StringComparison.Ordinal);
        Assert.Contains("--allow-private", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_WhenANameHasBothKinds_KeepsOnlyThePublicOnes()
    {
        // A DNS answer that mixes them must not get the private one dialled when the public one
        // happens to be slow.
        var open = PublicNetworkGuard.Allowed(
            "mixed.test", [IPAddress.Parse("192.168.1.1"), IPAddress.Parse("8.8.8.8")], allowPrivate: false);

        Assert.Equal([IPAddress.Parse("8.8.8.8")], open);
    }

    [Fact]
    public void Allowed_WhenPrivateAddressesAreAllowed_KeepsThemAll()
    {
        IPAddress[] resolved = [IPAddress.Parse("192.168.1.1"), IPAddress.Parse("8.8.8.8")];

        Assert.Equal(resolved, PublicNetworkGuard.Allowed("mixed.test", resolved, allowPrivate: true));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://[::1]:9/")]
    [InlineData("http://localhost:9/")]
    public async Task AnHttpClientBehindTheGuard_RefusesThisMachine_BeforeOpeningAnything(string address)
    {
        // The real handler, wired as the CLI wires it. Nothing listens on port 9 and nothing has
        // to: the refusal comes before a connection is attempted, which is the point.
        using var handler = new SocketsHttpHandler { ConnectCallback = PublicNetworkGuard.ConnectAsync, UseProxy = false };
        using var client = new HttpClient(handler);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(new Uri(address), TestContext.Current.CancellationToken));

        Assert.Contains("--allow-private", refused.Message, StringComparison.Ordinal);
    }
}
