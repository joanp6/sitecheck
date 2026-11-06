using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class MixedContentCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler, Uri? url = null)
    {
        using var client = new HttpClient(handler);
        return await new MixedContentCheck(client).RunAsync(url ?? Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving($"<html><head><title>x</title></head><body>{body}</body></html>", status));

    [Theory]
    [InlineData("""<img src="/logo.png">""")]                                    // relative to a secure page
    [InlineData("""<img src="https://cdn.test/a.png">""")]
    [InlineData("""<script src="//cdn.test/app.js"></script>""")]                // protocol-relative takes https
    [InlineData("""<a href="http://partner.test/">a plain link is not loaded</a>""")]
    [InlineData("""<link rel="canonical" href="http://salon.test/">""")]         // not a stylesheet
    [InlineData("""<img src="data:image/png;base64,AAAA">""")]
    [InlineData("")]
    public async Task RunAsync_WhenEverythingComesOverHttps_Passes(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("""<script src="http://cdn.test/app.js"></script>""")]
    [InlineData("""<link rel="stylesheet" href="http://cdn.test/site.css">""")]
    [InlineData("""<link rel="preload stylesheet" href="http://cdn.test/site.css">""")]
    [InlineData("""<iframe src="http://cdn.test/widget"></iframe>""")]
    [InlineData("""<object data="http://cdn.test/menu.pdf"></object>""")]
    [InlineData("""<embed src="http://cdn.test/movie.swf">""")]
    [InlineData("""<SCRIPT SRC='HTTP://cdn.test/app.js'></SCRIPT>""")]
    [InlineData("""<script src="http&#58;//cdn.test/app.js"></script>""")]      // an entity is still http
    public async Task RunAsync_WhenTheSecurePageLoadsAScriptStyleOrFrameOverHttp_FailsAndNamesIt(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("http://cdn.test/", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""<img src="http://cdn.test/a.png">""")]
    [InlineData("""<video poster="http://cdn.test/p.jpg"></video>""")]
    [InlineData("""<audio src="http://cdn.test/a.mp3"></audio>""")]
    [InlineData("""<video><source src="http://cdn.test/v.mp4"></video>""")]
    public async Task RunAsync_WhenOnlyImagesOrMediaComeOverHttp_Warns(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("http://cdn.test/", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheBaseIsInsecure_RelativeScriptsCountAsInsecure()
    {
        var outcome = await Run(StubHttpMessageHandler.Serving(
            """<html><head><base href="http://salon.test/"><script src="app.js"></script></head></html>"""));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("http://salon.test/app.js", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheMetaTagUpgradesInsecureRequests_OnlyWarns()
    {
        var outcome = await Run(StubHttpMessageHandler.Serving(
            """<html><head><meta http-equiv="Content-Security-Policy" content="upgrade-insecure-requests"><script src="http://cdn.test/app.js"></script></head></html>"""));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheHeaderUpgradesInsecureRequests_OnlyWarns()
    {
        var outcome = await Run(StubHttpMessageHandler.Routing(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""<script src="http://cdn.test/app.js"></script>"""),
            };
            response.Headers.TryAddWithoutValidation("Content-Security-Policy", "default-src https:; upgrade-insecure-requests");
            return response;
        }));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenAPolicyOnlyMentionsTheWordInAnotherDirective_StillFails()
    {
        var outcome = await Run(StubHttpMessageHandler.Serving(
            """<meta http-equiv="Content-Security-Policy" content="report-uri /upgrade-insecure-requests-log"><script src="http://cdn.test/app.js"></script>"""));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenManyPartsAreInsecure_ListsAFewAndCountsTheRest()
    {
        var body = string.Concat(Enumerable.Range(1, 5).Select(i => $"""<script src="http://cdn.test/{i}.js"></script>"""));

        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("5 scripts", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("and 2 more", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("4.js", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CountsTheSameAddressOnce()
    {
        var outcome = await RunAgainst("""<img src="http://cdn.test/a.png"><img src="http://cdn.test/a.png">""");

        Assert.Contains("1 file ", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThePageIsServedOverHttp_SkipsAndLeavesItToHttpsRedirect()
    {
        var outcome = await Run(
            StubHttpMessageHandler.Serving("""<script src="http://cdn.test/app.js"></script>"""),
            new Uri("http://salon.test/"));

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        var outcome = await RunAgainst("""<script src="http://cdn.test/app.js"></script>""", status);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        // load-time already fails a broken page; this check had nothing to look at.
        var outcome = await RunAgainst("""<script src="http://cdn.test/app.js"></script>""", status);

        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenThePageCannotBeFetched_IsAnErrorNotAFail(bool timedOut)
    {
        Exception failure = timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("refused");

        var outcome = await Run(StubHttpMessageHandler.Throwing(failure));

        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.Null(outcome.StatusCode);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesInsteadOfReportingAnError()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MixedContentCheck(client).RunAsync(Site, cancellation.Token));
    }
}
