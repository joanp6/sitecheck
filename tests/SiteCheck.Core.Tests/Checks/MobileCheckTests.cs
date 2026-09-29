using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class MobileCheckTests
{
    private static readonly Uri Site = new("https://example.test/");

    private static async Task<CheckOutcome> RunAgainst(string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving(html, status));
        return await new MobileCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static string PageWith(string metaTag) => $"<html><head><title>x</title>{metaTag}</head><body></body></html>";

    [Theory]
    [InlineData("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")]
    [InlineData("<meta name='viewport' content='width=device-width,initial-scale=1'>")]
    [InlineData("<META NAME=\"Viewport\" CONTENT=\"Width=Device-Width\">")]
    [InlineData("<meta content=\"width=device-width, initial-scale=1\" name=\"viewport\" />")]   // attribute order
    [InlineData("<meta name=\"viewport\" content=\"initial-scale=1,user-scalable=yes\">")]      // what Wikipedia ships: no width
    [InlineData("<meta name=\"viewport\" content=\"initial-scale=1.0\">")]
    [InlineData("<meta name=viewport content=width=device-width>")]                              // unquoted
    [InlineData("<meta name=\"viewport\" content=\"width=device-width; initial-scale=1\">")]     // semicolon separator
    public async Task RunAsync_WhenThePageFollowsTheDeviceWidth_Passes(string metaTag)
    {
        var outcome = await RunAgainst(PageWith(metaTag));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenThereIsNoViewport_FailsAndSaysWhatToAdd()
    {
        var outcome = await RunAgainst(PageWith("<meta charset=\"utf-8\"><meta name=\"description\" content=\"a shop\">"));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("width=device-width", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<meta name=\"viewport\" content=\"width=1024\">")]                 // fixed desktop width
    [InlineData("<meta name=\"viewport\" content=\"initial-scale=0.5\">")]          // no width, and a scale that doubles the layout
    [InlineData("<meta name=\"viewport\" content=\"initial-scale=abc\">")]
    [InlineData("<meta name=\"viewport\" content=\"user-scalable=yes\">")]          // no width and no scale
    [InlineData("<meta name=\"viewport\">")]                                        // no content
    [InlineData("<meta name=\"viewport\" content=\"\">")]
    public async Task RunAsync_WhenTheViewportDoesNotFollowTheDeviceWidth_Fails(string metaTag)
    {
        var outcome = await RunAgainst(PageWith(metaTag));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Theory]
    [InlineData("width=device-width, user-scalable=no")]
    [InlineData("width=device-width, user-scalable=0")]
    [InlineData("width=device-width, initial-scale=1, maximum-scale=1")]
    [InlineData("width=device-width, maximum-scale=1.5")]
    public async Task RunAsync_WhenThePageFitsButBlocksZoom_Warns(string content)
    {
        var outcome = await RunAgainst(PageWith($"<meta name=\"viewport\" content=\"{content}\">"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("zoom", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("width=device-width, maximum-scale=2")]
    [InlineData("width=device-width, maximum-scale=5")]
    [InlineData("width=device-width, user-scalable=yes")]
    public async Task RunAsync_WhenZoomIsAllowed_Passes(string content)
    {
        var outcome = await RunAgainst(PageWith($"<meta name=\"viewport\" content=\"{content}\">"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_UsesTheFirstViewportWhenThereAreTwo()
    {
        // Browsers honour the first. Reading the last would grade a page differently from how a phone shows it.
        var outcome = await RunAgainst(PageWith(
            "<meta name=\"viewport\" content=\"width=device-width\"><meta name=\"viewport\" content=\"width=1024\">"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_IgnoresOtherMetaTagsThatMentionViewport()
    {
        var outcome = await RunAgainst(PageWith("<meta name=\"description\" content=\"viewport width=device-width\">"));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenThePageIsEmpty_Fails()
    {
        var outcome = await RunAgainst(string.Empty);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheSiteAnswersWithAnError_FailsWithoutGradingTheErrorPage()
    {
        // An error page can carry a perfectly good viewport; that says nothing about the site.
        var outcome = await RunAgainst(
            PageWith("<meta name=\"viewport\" content=\"width=device-width\">"),
            HttpStatusCode.InternalServerError);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("500", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheSiteCannotBeReached_FailsInsteadOfReportingAToolError()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused")));

        var outcome = await new MobileCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheHttpClientTimesOut_Fails()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException("timed out")));

        var outcome = await new MobileCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException("cancelled")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MobileCheck(client).RunAsync(Site, source.Token));
    }
}
