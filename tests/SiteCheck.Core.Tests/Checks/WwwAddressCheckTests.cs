using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Dns;

namespace SiteCheck.Core.Tests.Checks;

public sealed class WwwAddressCheckTests
{
    private static Task<CheckOutcome> Run(FakeDnsResolver dns, string url = "https://salon.test/") =>
        new WwwAddressCheck(dns).RunAsync(new Uri(url), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(DnsRecordType.A, "203.0.113.7")]
    [InlineData(DnsRecordType.Aaaa, "2001:db8::7")] // reachable over IPv6 only is still reachable
    public async Task RunAsync_WhenTheWwwSpellingHasAnAddress_Passes(DnsRecordType type, string address)
    {
        var outcome = await Run(new FakeDnsResolver().With("www.salon.test", type, address));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("www.salon.test", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GivenTheWwwSpelling_ChecksTheBareOne()
    {
        var dns = new FakeDnsResolver().With("salon.test", DnsRecordType.A, "203.0.113.7");

        var outcome = await Run(dns, "https://www.salon.test/");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.All(dns.Questions, question => Assert.EndsWith(" salon.test", question, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenTheOtherSpellingDoesNotExist_Warns()
    {
        var outcome = await Run(new FakeDnsResolver());

        // The audited address works; only those who type the other one are turned away.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("www.salon.test leads nowhere", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheOtherSpellingExistsButHasNoAddress_Warns()
    {
        // The name is there, with mail records say, and no address for a browser to go to.
        var outcome = await Run(new FakeDnsResolver().Mx("www.salon.test", "mx.salon.test"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheOtherSpellingIsAnAliasForANameThatExists_Passes()
    {
        var dns = new FakeDnsResolver()
            .With("www.salon.test", DnsRecordType.Cname, "salon.netlify.app.")
            .With("salon.netlify.app", DnsRecordType.A, "203.0.113.7");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheAliasPointsAtANameThatIsGone_Fails_AsATakeoverRisk()
    {
        var dns = new FakeDnsResolver().With("www.salon.test", DnsRecordType.Cname, "old-salon.herokuapp.com.");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("old-salon.herokuapp.com", outcome.Detail, StringComparison.Ordinal);

        // On a platform where anyone can sign up, the report says who could take the name.
        Assert.Contains("herokuapp.com, where anyone can sign up", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheGoneNameIsNotOnAKnownPlatform_StillFails()
    {
        var dns = new FakeDnsResolver().With("www.salon.test", DnsRecordType.Cname, "host.gone-agency.test");

        var outcome = await Run(dns);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("Whoever registers that name", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://salon.co.uk/", "www.salon.co.uk")]
    [InlineData("https://salon.com.es/", "www.salon.com.es")]
    public async Task RunAsync_TreatsATwoPartSuffixAsPartOfTheDomain(string url, string twin)
    {
        var dns = new FakeDnsResolver().With(twin, DnsRecordType.A, "203.0.113.7");

        var outcome = await Run(dns, url);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("https://booking.salon.test/")]   // a subdomain has no www twin anyone types
    [InlineData("https://salon.pages.dev/")]      // the platform's address, not the business's
    [InlineData("https://203.0.113.7/")]
    [InlineData("https://www.localhost/")]
    public async Task RunAsync_WhenThereIsNoTwinToCheck_Skips_WithoutAskingTheDns(string url)
    {
        var dns = new FakeDnsResolver();

        var outcome = await Run(dns, url);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Empty(dns.Questions);
    }

    [Fact]
    public async Task RunAsync_WhenTheDnsCannotBeAsked_IsAnErrorNotAFinding()
    {
        var outcome = await Run(FakeDnsResolver.Failing(new DnsLookupException("no server answered")));

        Assert.Equal(CheckStatus.Error, outcome.Status);
    }
}
