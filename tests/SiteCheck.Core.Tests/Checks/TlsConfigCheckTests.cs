using System.Net.Sockets;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Http;
using SiteCheck.Tls;

namespace SiteCheck.Core.Tests.Checks;

public sealed class TlsConfigCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private static Task<CheckOutcome> Run(FakeTlsProbe probe, Uri? url = null) =>
        new TlsConfigCheck(probe).RunAsync(url ?? Site, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RunAsync_WhenTheServerRefusesBothOldVersions_Passes_HavingAskedAboutEach()
    {
        var probe = new FakeTlsProbe();

        var outcome = await Run(probe);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Equal([LegacyTlsVersion.Tls10, LegacyTlsVersion.Tls11], probe.Asked.Select(asked => asked.Version));
    }

    [Theory]
    [InlineData(new[] { LegacyTlsVersion.Tls10, LegacyTlsVersion.Tls11 }, "TLS 1.0 and TLS 1.1")]
    [InlineData(new[] { LegacyTlsVersion.Tls10 }, "TLS 1.0,")]
    [InlineData(new[] { LegacyTlsVersion.Tls11 }, "TLS 1.1,")]
    public async Task RunAsync_WhenTheServerStillAcceptsAnOldVersion_Warns_NamingWhich(LegacyTlsVersion[] accepted, string named)
    {
        var outcome = await Run(new FakeTlsProbe().Accepting(accepted));

        // No current browser uses them, so no visitor is affected: never a defect.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("still accepts " + named, outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GivenAnHttpAddress_AsksTheHttpsSite()
    {
        var probe = new FakeTlsProbe();

        await Run(probe, new Uri("http://salon.test:8080/menu"));

        Assert.All(probe.Asked, asked => Assert.Equal("https://salon.test/menu", asked.Url.AbsoluteUri));
    }

    public static TheoryData<Exception> Unreachable =>
    [
        new SocketException((int)SocketError.ConnectionRefused),
        new TimeoutException("silent"),
        new IOException("reset"),
    ];

    [Theory]
    [MemberData(nameof(Unreachable))]
    public async Task RunAsync_WhenTheServerCannotBeReached_IsAnErrorNotAFinding(Exception failure)
    {
        // ssl-certificate already fails a site with no https. Saying it twice would bury it.
        var outcome = await Run(FakeTlsProbe.Failing(failure));

        Assert.Equal(CheckStatus.Error, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheAddressIsPrivateAndSitecheckDeclinesToOpenIt_SaysHowToAllowIt()
    {
        var outcome = await Run(FakeTlsProbe.Failing(new PrivateAddressException("salon.test is at 10.0.0.5. Pass --allow-private.")));

        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.Contains("--allow-private", outcome.Detail, StringComparison.Ordinal);
    }
}
