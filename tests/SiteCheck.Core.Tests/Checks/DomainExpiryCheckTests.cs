using System.Net;
using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class DomainExpiryCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // Shaped like the real IANA file: note .es is absent, as it is in reality.
    private const string Bootstrap = """
        {
          "services": [
            [ ["com", "net"], ["http://rdap.example-registry.test/com/", "https://rdap.example-registry.test/com/"] ],
            [ ["uk"], ["https://rdap.uk.test/uk"] ]
          ]
        }
        """;

    private static string RecordExpiringOn(DateTimeOffset expiry) => $$"""
        {
          "objectClassName": "domain",
          "events": [
            { "eventAction": "registration", "eventDate": "2001-01-01T00:00:00Z" },
            { "eventAction": "expiration", "eventDate": "{{expiry:yyyy-MM-ddTHH:mm:ssZ}}" }
          ]
        }
        """;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>
    /// The bootstrap, plus <paramref name="records"/> keyed by the full RDAP address. Any other
    /// name gets the 404 a registry gives for a name nobody registered.
    /// </summary>
    private static StubHttpMessageHandler Registry(Dictionary<string, HttpResponseMessage> records) =>
        StubHttpMessageHandler.Routing(request =>
            request.RequestUri == DomainExpiryCheck.BootstrapUrl
                ? Json(Bootstrap)
                : records.GetValueOrDefault(request.RequestUri!.AbsoluteUri) ?? new HttpResponseMessage(HttpStatusCode.NotFound));

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler, string site, DomainExpiryCheckOptions? options = null)
    {
        using var client = new HttpClient(handler);
        var check = new DomainExpiryCheck(client, new FakeTimeProvider(Now), options);

        return await check.RunAsync(new Uri(site), TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunExpiringIn(int days, DomainExpiryCheckOptions? options = null) =>
        Run(
            Registry(new() { ["https://rdap.example-registry.test/com/domain/example.com"] = Json(RecordExpiringOn(Now.AddDays(days))) }),
            "https://www.example.com/",
            options);

    [Fact]
    public async Task RunAsync_WhenTheDomainIsFarFromExpiry_PassesAndReportsTheDate()
    {
        var outcome = await RunExpiringIn(200);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("example.com", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-08-03", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(Now.AddDays(200), outcome.ValidUntil);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task RunAsync_WhenTheDomainExpiresInsideTheWarningWindow_Warns(int days)
    {
        var outcome = await RunExpiringIn(days);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Equal(Now.AddDays(days), outcome.ValidUntil);
    }

    [Fact]
    public async Task RunAsync_WhenTheDomainExpiresJustOutsideTheWarningWindow_Passes() =>
        Assert.Equal(CheckStatus.Pass, (await RunExpiringIn(31)).Status);

    [Fact]
    public async Task RunAsync_WhenTheDomainHasExpired_Fails()
    {
        var outcome = await RunExpiringIn(-3);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("expired on 2026-01-12", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HonoursACustomWarningWindow() =>
        Assert.Equal(CheckStatus.Warn, (await RunExpiringIn(45, new DomainExpiryCheckOptions(WarnWithinDays: 60))).Status);

    [Fact]
    public async Task RunAsync_TriesTheLongestNameFirstAndStopsAtTheFirstRegisteredOne()
    {
        // The registry answers for the bare suffix too, as Nominet really does for co.uk. Asking
        // shortest first would grade bbc.co.uk on co.uk's record.
        var handler = Registry(new()
        {
            ["https://rdap.uk.test/uk/domain/co.uk"] = Json(RecordExpiringOn(Now.AddDays(-999))),
            ["https://rdap.uk.test/uk/domain/shop.co.uk"] = Json(RecordExpiringOn(Now.AddDays(200))),
        });

        var outcome = await Run(handler, "https://www.shop.co.uk/");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("shop.co.uk", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("GET https://rdap.uk.test/uk/domain/co.uk", handler.Requests);
    }

    [Fact]
    public async Task RunAsync_TreatsABadRequestLikeNotFound_AsGooglesRegistryAnswersForSubdomains()
    {
        var handler = Registry(new()
        {
            ["https://rdap.example-registry.test/com/domain/www.example.com"] = new HttpResponseMessage(HttpStatusCode.BadRequest),
            ["https://rdap.example-registry.test/com/domain/example.com"] = Json(RecordExpiringOn(Now.AddDays(200))),
        });

        Assert.Equal(CheckStatus.Pass, (await Run(handler, "https://www.example.com/")).Status);
    }

    [Fact]
    public async Task RunAsync_PrefersTheHttpsServerWhenTheRegistryOffersBoth()
    {
        var handler = Registry(new()
        {
            ["https://rdap.example-registry.test/com/domain/example.com"] = Json(RecordExpiringOn(Now.AddDays(200))),
        });

        await Run(handler, "https://example.com/");

        Assert.DoesNotContain(handler.Requests, request => request.StartsWith("GET http://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_DownloadsTheBootstrapOnceForManySites()
    {
        var handler = Registry(new()
        {
            ["https://rdap.example-registry.test/com/domain/a.com"] = Json(RecordExpiringOn(Now.AddDays(200))),
            ["https://rdap.example-registry.test/com/domain/b.com"] = Json(RecordExpiringOn(Now.AddDays(200))),
        });
        using var client = new HttpClient(handler);
        var check = new DomainExpiryCheck(client, new FakeTimeProvider(Now));

        await check.RunAsync(new Uri("https://a.com/"), TestContext.Current.CancellationToken);
        await check.RunAsync(new Uri("https://b.com/"), TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests, request => request == $"GET {DomainExpiryCheck.BootstrapUrl}");
    }

    [Theory]
    [InlineData("https://tienda.es/", "RDAP")]                                  // .es publishes no RDAP, for real
    [InlineData("https://perruqueria-angela.pages.dev/", "pages.dev")]         // a hosting platform's address
    [InlineData("https://someone.github.io/site/", "github.io")]
    [InlineData("https://192.0.2.10/", "IP")]
    [InlineData("https://[2001:db8::1]/", "IP")]
    [InlineData("http://localhost/", "not a registered domain")]
    public async Task RunAsync_WhenThereIsNoDomainToCheck_SkipsAndSaysWhy(string site, string reason)
    {
        var outcome = await Run(Registry([]), site);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Contains(reason, outcome.Detail, StringComparison.Ordinal);
        Assert.Null(outcome.ValidUntil);
    }

    [Fact]
    public async Task RunAsync_DoesNotMistakeADomainThatMerelyEndsLikeAPlatformForOne()
    {
        // mypages.dev is somebody's own domain, not an address on pages.dev.
        var handler = StubHttpMessageHandler.Routing(request => request.RequestUri == DomainExpiryCheck.BootstrapUrl
            ? Json("""{ "services": [ [ ["dev"], ["https://rdap.dev.test/"] ] ] }""")
            : request.RequestUri!.AbsoluteUri == "https://rdap.dev.test/domain/mypages.dev"
                ? Json(RecordExpiringOn(Now.AddDays(200)))
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(CheckStatus.Pass, (await Run(handler, "https://mypages.dev/")).Status);
    }

    [Fact]
    public async Task RunAsync_WhenNoNameIsRegistered_Skips() =>
        Assert.Equal(CheckStatus.Skip, (await Run(Registry([]), "https://www.example.com/")).Status);

    [Theory]
    [InlineData("""{ "events": [ { "eventAction": "registration", "eventDate": "2001-01-01T00:00:00Z" } ] }""")]
    [InlineData("""{ "objectClassName": "domain" }""")]
    [InlineData("""{ "events": [ { "eventAction": "expiration", "eventDate": "not a date" } ] }""")]
    public async Task RunAsync_WhenTheRecordCarriesNoUsableExpiry_Skips(string record)
    {
        var handler = Registry(new() { ["https://rdap.example-registry.test/com/domain/example.com"] = Json(record) });

        Assert.Equal(CheckStatus.Skip, (await Run(handler, "https://example.com/")).Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheRegistryIsBroken_ThrowsSoTheRunnerRecordsAToolError()
    {
        // The registry being down says nothing about the domain, so this must not be a Fail.
        var handler = Registry(new()
        {
            ["https://rdap.example-registry.test/com/domain/example.com"] = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => Run(handler, "https://example.com/"));
    }

    [Fact]
    public void Options_RejectANegativeWarningWindow() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DomainExpiryCheckOptions(WarnWithinDays: -1));
}
