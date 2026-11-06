using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Dns;

namespace SiteCheck.Core.Tests.Checks;

public sealed class EmailAuthCheckTests
{
    private static readonly Uri Site = new("https://www.salon.test/");

    private const string Spf = "v=spf1 include:spf.mail.test ~all";

    private static Task<CheckOutcome> Run(IDnsResolver dns, Uri? url = null) =>
        new EmailAuthCheck(dns).RunAsync(url ?? Site, TestContext.Current.CancellationToken);

    /// <summary>A domain with mail, SPF and the given DMARC record (none when null).</summary>
    private static FakeDnsResolver Domain(string? dmarc, params string[] txt)
    {
        var dns = new FakeDnsResolver()
            .Mx("salon.test", "mx1.mail.test")
            .Txt("salon.test", txt.Length == 0 ? [Spf] : txt);

        return dmarc is null ? dns : dns.Txt("_dmarc.salon.test", dmarc);
    }

    [Theory]
    [InlineData("v=DMARC1; p=reject; rua=mailto:dmarc@salon.test", "rejected")]
    [InlineData("v=DMARC1; p=quarantine", "sent to spam")]
    [InlineData("v=DMARC1;p=REJECT", "rejected")]
    [InlineData("v=DMARC1; p=quarantine; pct=100", "sent to spam")]
    public async Task RunAsync_WhenDmarcIsEnforced_Passes(string dmarc, string fate)
    {
        var outcome = await Run(Domain(dmarc));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains($"Mail forged in salon.test's name is {fate}", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("DKIM", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithSoftFailSpfAndEnforcedDmarc_Passes()
    {
        // ~all is what the big providers recommend alongside DMARC: DMARC decides.
        var outcome = await Run(Domain("v=DMARC1; p=reject", "v=spf1 include:spf.mail.test ~all"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenThereIsNoDmarc_FailsAndSaysWhatAForgerCanDo()
    {
        var outcome = await Run(Domain(dmarc: null));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("salon.test has no DMARC record", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("Anyone can email the business's customers", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThereAreTwoDmarcRecords_Fails()
    {
        var dns = Domain(dmarc: null).Txt("_dmarc.salon.test", "v=DMARC1; p=reject", "v=DMARC1; p=none");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("2 DMARC records", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_IgnoresTxtRecordsUnderDmarcThatAreNotDmarc()
    {
        var dns = Domain(dmarc: null).Txt("_dmarc.salon.test", "v=DMARC1; p=reject", "some-verification=abc");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenDmarcOnlyMonitors_Warns()
    {
        var outcome = await Run(Domain("v=DMARC1; p=none; rua=mailto:dmarc@salon.test"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("p=none", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("p=quarantine or p=reject", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v=DMARC1; rua=mailto:dmarc@salon.test")]
    [InlineData("v=DMARC1; p=whatever")]
    public async Task RunAsync_WhenDmarcHasNoValidPolicy_Warns(string dmarc)
    {
        var outcome = await Run(Domain(dmarc));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no valid p=", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenDmarcAppliesToPartOfTheMail_Warns()
    {
        var outcome = await Run(Domain("v=DMARC1; p=reject; pct=25"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("only 25 %", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThereIsNoSpf_Warns()
    {
        var outcome = await Run(Domain("v=DMARC1; p=reject", "google-site-verification=abc"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no SPF record", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThereAreTwoSpfRecords_Fails()
    {
        var outcome = await Run(Domain("v=DMARC1; p=reject", "v=spf1 include:a.test -all", "v=spf1 include:b.test -all"));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("2 SPF records", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v=spf1 +all")]
    [InlineData("v=spf1 a mx all")]       // no qualifier means +
    public async Task RunAsync_WhenSpfAuthorisesEveryone_Fails(string spf)
    {
        var outcome = await Run(Domain("v=DMARC1; p=reject", spf));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("+all", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotTakeASimilarVersionForSpf()
    {
        var outcome = await Run(Domain("v=DMARC1; p=reject", "v=spf10 +all"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no SPF record", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenSeveralThingsAreWrong_ReportsThemAllUnderTheWorst()
    {
        var outcome = await Run(Domain(dmarc: null, "google-site-verification=abc"));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("no DMARC record", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("no SPF record", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AsksAboutTheDomainWithoutWww()
    {
        var dns = Domain("v=DMARC1; p=reject");

        await Run(dns);

        Assert.Contains("Mx salon.test", dns.Questions);
        Assert.Contains("Txt _dmarc.salon.test", dns.Questions);
        Assert.DoesNotContain(dns.Questions, q => q.Contains("www.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ForASubdomainWithoutItsOwnDmarc_UsesTheParents()
    {
        var dns = new FakeDnsResolver()
            .Mx("shop.salon.test", "mx1.mail.test")
            .Txt("shop.salon.test", Spf)
            .Txt("_dmarc.salon.test", "v=DMARC1; p=reject");

        var outcome = await Run(dns, new Uri("https://shop.salon.test/"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_TreatsANullMxAsNoMail()
    {
        // MX "." (RFC 7505): the domain says outright that it receives no mail.
        var dns = new FakeDnsResolver().Mx("salon.test", string.Empty).Txt("salon.test", "google-site-verification=abc");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheDomainHasNoMailAtAll_SkipsAndSaysHowToLockItDown()
    {
        var dns = new FakeDnsResolver().Txt("salon.test", "google-site-verification=abc");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Contains("v=spf1 -all", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://salon-test.pages.dev/")]
    [InlineData("https://salon.netlify.app/")]
    public async Task RunAsync_OnAHostingPlatformsAddress_SkipsWithoutAskingTheDns(string address)
    {
        var dns = new FakeDnsResolver();

        var outcome = await Run(dns, new Uri(address));

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Empty(dns.Questions);
    }

    [Fact]
    public async Task RunAsync_OnAnIpAddress_Skips()
    {
        var outcome = await Run(new FakeDnsResolver(), new Uri("https://192.0.2.10/"));

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheDnsCannotBeAsked_IsAnErrorNotAFail()
    {
        var outcome = await Run(FakeDnsResolver.Failing(new DnsLookupException("No DNS server answered.")));

        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.Contains("could not be asked", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new EmailAuthCheck(Domain("v=DMARC1; p=reject")).RunAsync(Site, cancellation.Token));
    }
}
