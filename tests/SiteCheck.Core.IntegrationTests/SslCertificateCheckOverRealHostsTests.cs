using SiteCheck.Certificates;
using SiteCheck.Checks;

namespace SiteCheck.Core.IntegrationTests;

/// <summary>
/// Drives <see cref="SslCertificateCheck"/> end to end over a real connection, where
/// the certificate state and the reported policy errors come from the same reality
/// and cannot be set independently the way a test double allows.
/// </summary>
public sealed class SslCertificateCheckOverRealHostsTests
{
    [Fact]
    public async Task RunAsync_WhenTheCertificateHasExpired_SaysSoInsteadOfBlamingTheChain()
    {
        IntegrationGate.RequireEnabled();
        await RemoteHost.RequireReachableAsync("expired.badssl.com", TestContext.Current.CancellationToken);

        var check = new SslCertificateCheck(new SslStreamCertificateProvider(), TimeProvider.System);

        var outcome = await check.RunAsync(
            new Uri("https://expired.badssl.com/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);

        // 2015-04-12T23:59:59 UTC, and it has not moved since, so the date is safe to pin.
        // Note the day: this test first asserted 2015-04-13, the value that shows up when
        // the certificate is read in a UTC+2 timezone. The check reports UTC, so that
        // assertion would have passed in Madrid and failed on a UTC runner. Reading a date
        // off a local-time debug print is how you write a test that only holds in one
        // timezone.
        Assert.Contains("expired on 2015-04-12", outcome.Detail, StringComparison.Ordinal);
    }
}
