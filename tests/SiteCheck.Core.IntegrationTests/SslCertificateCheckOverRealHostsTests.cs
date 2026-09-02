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
    [Fact(Skip = "Fails until joanp6/sitecheck#1 is fixed. Written against the correct behaviour on " +
                 "purpose: asserting today's wrong message would bless the bug and make the fix look " +
                 "like a regression. https://github.com/joanp6/sitecheck/issues/1")]
    public async Task RunAsync_WhenTheCertificateHasExpired_SaysSoInsteadOfBlamingTheChain()
    {
        IntegrationGate.RequireEnabled();
        await RemoteHost.RequireReachableAsync("expired.badssl.com", TestContext.Current.CancellationToken);

        var check = new SslCertificateCheck(new SslStreamCertificateProvider(), TimeProvider.System);

        var outcome = await check.RunAsync(
            new Uri("https://expired.badssl.com/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);

        // The certificate expired on 2015-04-13 and has not moved since, so this date is
        // safe to pin. What the check says today is "its chain is not valid", which sends
        // the site owner to change the wrong thing.
        Assert.Contains("expired on 2015-04-13", outcome.Detail, StringComparison.Ordinal);
    }
}
