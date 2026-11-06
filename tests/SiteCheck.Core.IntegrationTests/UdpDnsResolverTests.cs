using SiteCheck.Dns;

namespace SiteCheck.Core.IntegrationTests;

/// <summary>
/// Asks this machine's real DNS servers. example.com belongs to IANA and is reserved for
/// documentation, so its records are stable and no business is looked up.
/// </summary>
public sealed class UdpDnsResolverTests
{
    [Fact]
    public async Task QueryAsync_ReadsARealTxtRecord()
    {
        IntegrationGate.RequireEnabled();

        var answer = await new UdpDnsResolver().QueryAsync("example.com", DnsRecordType.Txt, TestContext.Current.CancellationToken);

        Assert.True(answer.NameExists);
        Assert.Contains(answer.Records, record => record.StartsWith("v=spf1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryAsync_ReportsANameThatDoesNotExist()
    {
        IntegrationGate.RequireEnabled();

        // .invalid is reserved never to exist (RFC 2606).
        var answer = await new UdpDnsResolver().QueryAsync("sitecheck.invalid", DnsRecordType.Txt, TestContext.Current.CancellationToken);

        Assert.False(answer.NameExists);
        Assert.Empty(answer.Records);
    }

    [Fact]
    public async Task QueryAsync_WhenNoServerAnswers_ThrowsInsteadOfHanging()
    {
        IntegrationGate.RequireEnabled();

        // 192.0.2.0/24 is reserved for documentation: nothing there answers.
        var resolver = new UdpDnsResolver([new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.0.2.1"), 53)], TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<DnsLookupException>(
            () => resolver.QueryAsync("example.com", DnsRecordType.Txt, TestContext.Current.CancellationToken));
    }
}
