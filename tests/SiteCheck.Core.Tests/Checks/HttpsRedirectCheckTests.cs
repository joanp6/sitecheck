using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class HttpsRedirectCheckTests
{
    private static readonly Uri Site = new("https://example.test/menu");

    /// <summary>Answers the http request as if the client had followed redirects to <paramref name="landedOn"/>.</summary>
    private static StubHttpMessageHandler LandingOn(string landedOn, HttpStatusCode status = HttpStatusCode.OK) =>
        StubHttpMessageHandler.Routing(_ => new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, landedOn),
        });

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler, Uri? url = null)
    {
        using var client = new HttpClient(handler);
        return await new HttpsRedirectCheck(client).RunAsync(url ?? Site, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_AsksForTheHttpVersionOfTheSameAddress()
    {
        var handler = LandingOn("https://example.test/menu");

        await Run(handler, new Uri("https://example.test:8443/menu?x=1"));

        // The default http port, not the https one: that is what a visitor typing http:// reaches.
        Assert.Equal(["GET http://example.test/menu?x=1"], handler.Requests);
    }

    [Theory]
    [InlineData("https://example.test/menu")]
    [InlineData("https://www.example.test/")]
    [InlineData("https://example.test/not-found")] // where it lands is load-time's business
    public async Task RunAsync_WhenHttpEndsUpOnHttps_Passes(string landedOn)
    {
        var outcome = await Run(LandingOn(landedOn));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("http://example.test/menu", HttpStatusCode.OK)]
    [InlineData("http://www.example.test/", HttpStatusCode.OK)]
    [InlineData("http://example.test/menu", HttpStatusCode.NotFound)]
    public async Task RunAsync_WhenHttpIsAnsweredWithoutRedirectingToHttps_Fails(string landedOn, HttpStatusCode status)
    {
        var outcome = await Run(LandingOn(landedOn, status));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("Not secure", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenNoRedirectHappensAtAll_Fails()
    {
        var outcome = await Run(StubHttpMessageHandler.Serving("<html></html>"));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenNothingAnswersOnHttp_WarnsRatherThanFails(bool timedOut)
    {
        Exception failure = timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("refused");

        var outcome = await Run(StubHttpMessageHandler.Throwing(failure));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException("cancelled")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpsRedirectCheck(client).RunAsync(Site, source.Token));
    }
}
