using System.Net.Security;
using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class SslCertificateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri SecureSite = new("https://example.test/");

    [Fact]
    public async Task RunAsync_WhenTheSiteIsNotServedOverHttps_FailsWithoutConnecting()
    {
        var provider = FakeCertificateProvider.NeverCalled();
        var check = new SslCertificateCheck(provider, new FakeTimeProvider(Now));

        var outcome = await check.RunAsync(new Uri("http://example.test/"), TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Equal(0, provider.Invocations);
    }

    [Fact]
    public async Task RunAsync_WhenTheCertificateIsValidAndFarFromExpiry_Passes()
    {
        var outcome = await RunAgainst(validFrom: Now.AddDays(-30), validUntil: Now.AddDays(90));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("2026-04-15", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    public async Task RunAsync_WhenTheCertificateExpiresInsideTheWarningWindow_Warns(int daysLeft)
    {
        var outcome = await RunAgainst(validFrom: Now.AddDays(-300), validUntil: Now.AddDays(daysLeft));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCertificateExpiresJustOutsideTheWarningWindow_Passes()
    {
        var outcome = await RunAgainst(validFrom: Now.AddDays(-300), validUntil: Now.AddDays(31));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_HonoursACustomWarningWindow()
    {
        var outcome = await RunAgainst(
            validFrom: Now.AddDays(-300),
            validUntil: Now.AddDays(45),
            options: new SslCertificateCheckOptions(WarnWithinDays: 60));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCertificateHasExpired_SaysSoRatherThanBlamingTheChain()
    {
        // The pairing is the point. Expiry has no flag of its own and reaches us as a chain
        // error, so this is the only shape this case takes in reality — and it is the shape
        // no test used before joanp6/sitecheck#1.
        var outcome = await RunAgainst(
            validFrom: Now.AddDays(-300),
            validUntil: Now.AddDays(-5),
            policyErrors: SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("expired on 2026-01-10", outcome.Detail, StringComparison.Ordinal);

        // Telling the owner their chain is broken sends them to renew the wrong thing.
        Assert.DoesNotContain("chain", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenTheCertificateIsNotValidYet_SaysSoRatherThanBlamingTheChain()
    {
        var outcome = await RunAgainst(
            validFrom: Now.AddDays(2),
            validUntil: Now.AddDays(100),
            policyErrors: SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("not valid until 2026-01-17", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("chain", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFakeRefusesToDescribeAHandshakeThatCannotHappen()
    {
        // This guard is the actual fix for joanp6/sitecheck#1. Reordering the branches in
        // SslCertificateCheck only treats the symptom: without this, the next check to use
        // the fake can go green over states the TLS stack never produces.
        var clock = new FakeTimeProvider(Now);
        var expired = TestCertificates.ValidBetween(Now.AddDays(-300), Now.AddDays(-5));

        var refusal = Assert.Throws<InvalidOperationException>(
            () => FakeCertificateProvider.Presenting(expired, clock, SslPolicyErrors.None));

        Assert.Contains("cannot happen", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, "different host name")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, "self-signed")]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, "no certificate")]
    public async Task RunAsync_WhenTheHandshakeRejectedTheCertificate_FailsAndSaysWhy(
        SslPolicyErrors policyErrors,
        string expectedReason)
    {
        // The dates are impeccable; the certificate is still unusable in a browser.
        var outcome = await RunAgainst(validFrom: Now.AddDays(-30), validUntil: Now.AddDays(300), policyErrors: policyErrors);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains(expectedReason, outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RejectANegativeWarningWindow() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SslCertificateCheckOptions(WarnWithinDays: -1));

    private static async Task<CheckOutcome> RunAgainst(
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        SslPolicyErrors policyErrors = SslPolicyErrors.None,
        SslCertificateCheckOptions? options = null)
    {
        // The check disposes the certificate it is handed, so the test must not.
        var certificate = TestCertificates.ValidBetween(validFrom, validUntil);

        // One clock for both: the fake validates the dates against the same instant the
        // check will read, so it can reject impossible pairings.
        var clock = new FakeTimeProvider(Now);

        var check = new SslCertificateCheck(
            FakeCertificateProvider.Presenting(certificate, clock, policyErrors),
            clock,
            options);

        return await check.RunAsync(SecureSite, TestContext.Current.CancellationToken);
    }
}
