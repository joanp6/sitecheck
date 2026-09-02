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
    public async Task RunAsync_WhenTheCertificateHasExpired_Fails()
    {
        var outcome = await RunAgainst(validFrom: Now.AddDays(-300), validUntil: Now.AddDays(-5));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("expired", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenTheCertificateIsNotValidYet_Fails()
    {
        var outcome = await RunAgainst(validFrom: Now.AddDays(2), validUntil: Now.AddDays(100));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("not valid until", outcome.Detail, StringComparison.Ordinal);
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
        var check = new SslCertificateCheck(
            FakeCertificateProvider.Presenting(certificate, policyErrors),
            new FakeTimeProvider(Now),
            options);

        return await check.RunAsync(SecureSite, TestContext.Current.CancellationToken);
    }
}
