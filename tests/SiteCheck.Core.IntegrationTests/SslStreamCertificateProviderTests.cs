using System.Net.Security;
using System.Net.Sockets;
using SiteCheck.Certificates;

namespace SiteCheck.Core.IntegrationTests;

/// <summary>
/// Drives <see cref="SslStreamCertificateProvider"/> against real TLS endpoints over
/// real sockets. This is the one part of the library with no unit tests, because
/// there is no logic in it to test without a connection.
/// </summary>
/// <remarks>
/// Assertions are deliberately relative ("expires after now") rather than absolute
/// ("expires on this date"), so that no test here starts failing on a calendar date
/// when a borrowed host renews its certificate.
/// </remarks>
public sealed class SslStreamCertificateProviderTests
{
    private static readonly SslStreamCertificateProvider Provider = new();

    [Fact]
    public async Task GetAsync_AgainstAHealthyHost_ReturnsATrustedCertificate()
    {
        // example.com, not badssl.com: if the healthy case and the broken cases shared a
        // domain, one outage would take out the whole suite and "the host is down" would
        // be indistinguishable from "the certificate is bad".
        var info = await GetAsync("example.com");

        Assert.Equal(SslPolicyErrors.None, info.PolicyErrors);
        Assert.True(info.Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow);
        Assert.Contains("example.com", info.Certificate.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_AgainstAnExpiredCertificate_StillReturnsItSoTheCheckCanExplainWhy()
    {
        var info = await GetAsync("expired.badssl.com");

        Assert.True(info.Certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow);

        // Note the shape of this: expiry reaches us as a chain error. There is no
        // dedicated "expired" flag, which is the root of joanp6/sitecheck#1.
        Assert.True(info.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task GetAsync_WhenTheCertificateWasIssuedForAnotherHost_ReportsANameMismatch()
    {
        var info = await GetAsync("wrong.host.badssl.com");

        Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, info.PolicyErrors);

        // The dates are fine; only the name is wrong. Worth pinning, because it proves
        // the check cannot fall back on dates to catch this one.
        Assert.True(info.Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow);
    }

    [Theory]
    [InlineData("self-signed.badssl.com")]
    [InlineData("untrusted-root.badssl.com")]
    public async Task GetAsync_WhenTheChainCannotBeValidated_ReportsAChainError(string host)
    {
        // incomplete-chain.badssl.com is deliberately absent: Windows completes the chain
        // by fetching the missing intermediate over AIA and reports None, while OpenSSL
        // does not. See docs/testing.md.
        var info = await GetAsync(host);

        Assert.True(info.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task GetAsync_WhenTheHostDoesNotResolve_ThrowsSocketException()
    {
        IntegrationGate.RequireEnabled();

        // .invalid is reserved by RFC 2606 and can never resolve, so unlike a borrowed
        // host this case cannot be "down" and needs no preflight.
        var failure = await Assert.ThrowsAsync<SocketException>(
            () => Provider.GetAsync(new Uri("https://sitecheck.invalid/"), TestContext.Current.CancellationToken));

        Assert.Equal(SocketError.HostNotFound, failure.SocketErrorCode);
    }

    [Fact]
    public async Task GetAsync_WhenTheHostAcceptsTheConnectionAndThenGoesSilent_HonoursTheCallersDeadline()
    {
        IntegrationGate.RequireEnabled();

        using var wedgedHost = new BlackHoleListener();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));

        // The provider has no timeout of its own: it stops when the caller says so.
        // This test is what documents that contract.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provider.GetAsync(wedgedHost.Url, deadline.Token));
    }

    private static async Task<CertificateInfo> GetAsync(string host)
    {
        IntegrationGate.RequireEnabled();
        await RemoteHost.RequireReachableAsync(host, TestContext.Current.CancellationToken);

        return await Provider.GetAsync(new Uri($"https://{host}/"), TestContext.Current.CancellationToken);
    }
}
