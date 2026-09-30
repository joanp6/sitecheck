using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class PageSpeedCheckTests
{
    private static readonly Uri Site = new("https://salon.test/a page?x=1&y=2");

    // The fields read, in the shape the v5 API returns them.
    private static string Scored(double score, double lcpMilliseconds = 2500) => $$"""
        {
          "lighthouseResult": {
            "categories": { "performance": { "score": {{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } },
            "audits": { "largest-contentful-paint": { "numericValue": {{lcpMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "displayValue": "x" } }
          }
        }
        """;

    private static StubHttpMessageHandler Answering(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        StubHttpMessageHandler.Routing(_ => new HttpResponseMessage(status) { Content = new StringContent(json) });

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler, string? key = "k")
    {
        using var client = new HttpClient(handler);
        return await new PageSpeedCheck(client, key).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0.95, CheckStatus.Pass)]
    [InlineData(0.9, CheckStatus.Pass)]
    [InlineData(0.89, CheckStatus.Warn)]
    [InlineData(0.5, CheckStatus.Warn)]
    [InlineData(0.49, CheckStatus.Fail)]
    [InlineData(0.0, CheckStatus.Fail)]
    public async Task RunAsync_GradesOnLighthousesOwnBands(double score, CheckStatus expected) =>
        Assert.Equal(expected, (await Run(Answering(Scored(score)))).Status);

    [Fact]
    public async Task RunAsync_SaysTheScoreAndHowLongTheMainContentTakes()
    {
        var outcome = await Run(Answering(Scored(0.42, lcpMilliseconds: 6340)));

        Assert.Contains("42/100", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("appears after 6.3 s", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AsksForTheMobileScoreOfTheExactAddressWithTheKey()
    {
        var handler = Answering(Scored(0.95));

        await Run(handler, key: " my-key ");

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("GET https://www.googleapis.com/pagespeedonline/v5/runPagespeed?", request, StringComparison.Ordinal);
        Assert.Contains("url=https%3A%2F%2Fsalon.test%2Fa%2520page%3Fx%3D1%26y%3D2", request, StringComparison.Ordinal);
        Assert.Contains("strategy=mobile", request, StringComparison.Ordinal);
        Assert.Contains("&key=my-key", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithoutAKey_SendsNone()
    {
        var handler = Answering(Scored(0.95));

        await Run(handler, key: null);

        Assert.DoesNotContain("key=", Assert.Single(handler.Requests), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "SITECHECK_PAGESPEED_KEY")]    // the anonymous quota, which is spent in practice
    [InlineData("k", "quota for this API key")]
    public async Task RunAsync_WhenTheQuotaIsSpent_ThrowsAndSaysWhatToDo(string? key, string expected)
    {
        // The error shape the real API returned during development.
        var handler = Answering(
            """{ "error": { "code": 429, "message": "Quota exceeded for quota metric 'Queries'" } }""",
            HttpStatusCode.TooManyRequests);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Run(handler, key));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenGoogleRefuses_ThrowsWithItsReasonButNeverTheKey()
    {
        var handler = Answering(
            """{ "error": { "code": 400, "message": "API key not valid. Please pass a valid API key." } }""",
            HttpStatusCode.BadRequest);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Run(handler, key: "secret-key-123"));

        Assert.Contains("API key not valid", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-123", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenLighthouseCouldNotScoreThePage_ThrowsWithItsReason()
    {
        var handler = Answering("""
            { "lighthouseResult": { "categories": { "performance": { "score": null } },
              "runtimeError": { "code": "NO_FCP", "message": "The page did not paint any content." } } }
            """);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Run(handler));

        Assert.Contains("did not paint", failure.Message, StringComparison.Ordinal);
    }
}
