using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class BrokenLinksCheckTests
{
    private static readonly Uri Site = new("https://example.test/shop/");

    private static string PageLinking(params string[] hrefs) =>
        "<html><body>" + string.Concat(hrefs.Select(href => $"<a href=\"{href}\">link</a>")) + "</body></html>";

    private static HttpResponseMessage Page(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    /// <summary>Serves <paramref name="html"/> for the page and <paramref name="statuses"/> (default 200) for any link.</summary>
    private static StubHttpMessageHandler HandlerFor(string html, Dictionary<string, HttpStatusCode>? statuses = null) =>
        StubHttpMessageHandler.Routing(request =>
            request.RequestUri == Site && request.Method == HttpMethod.Get
                ? Page(html)
                : new HttpResponseMessage(statuses?.GetValueOrDefault(request.RequestUri!.AbsoluteUri, HttpStatusCode.OK) ?? HttpStatusCode.OK));

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler, BrokenLinksCheckOptions? options = null)
    {
        using var client = new HttpClient(handler);
        return await new BrokenLinksCheck(client, options).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> Run(string html, Dictionary<string, HttpStatusCode>? statuses = null) =>
        Run(HandlerFor(html, statuses));

    [Fact]
    public async Task RunAsync_WhenEveryLinkWorks_Passes()
    {
        var outcome = await Run(PageLinking("https://other.test/a", "/shop/b"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("All 2 link(s)", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThePageHasNoLinks_PassesWithoutFollowingAnything()
    {
        var handler = HandlerFor("<html><body>Just words.</body></html>");

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Equal(["GET https://example.test/shop/"], handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task RunAsync_WhenAnOwnPageIsGone_FailsAndNamesTheLink(HttpStatusCode status)
    {
        var outcome = await Run(
            PageLinking("/shop/menu", "/shop/contact"),
            new() { ["https://example.test/shop/menu"] = status });

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("https://example.test/shop/menu (" + (int)status + ")", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("contact", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("1 of 2", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenOnlyOtherSitesAreBroken_WarnsBecauseTheOwnerDidNotBreakThem()
    {
        var outcome = await Run(
            PageLinking("https://partner.test/old", "/shop/menu"),
            new() { ["https://partner.test/old"] = HttpStatusCode.NotFound });

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenBothKindsAreBroken_FailsBecauseOneOfThemIsTheOwners()
    {
        var outcome = await Run(
            PageLinking("https://partner.test/old", "/shop/menu"),
            new()
            {
                ["https://partner.test/old"] = HttpStatusCode.NotFound,
                ["https://example.test/shop/menu"] = HttpStatusCode.NotFound,
            });

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_TreatsTheWwwAndBareNamesOfTheSiteAsOneSite()
    {
        var outcome = await Run(
            PageLinking("https://www.example.test/gone"),
            new() { ["https://www.example.test/gone"] = HttpStatusCode.NotFound });

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)999)]
    public async Task RunAsync_WhenASiteRefusesRobots_DoesNotCallTheLinkBrokenButSaysItCouldNotCheck(HttpStatusCode status)
    {
        // A person opens these fine. Calling them broken would send the owner to fix a working link.
        var outcome = await Run(
            PageLinking("https://social.test/page"),
            new() { ["https://social.test/page"] = status });

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("1 link(s) could not be verified", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenHeadIsRefusedButGetWorks_TheLinkIsFine()
    {
        var handler = StubHttpMessageHandler.Routing(request =>
        {
            if (request.RequestUri == Site)
            {
                return Page(PageLinking("https://picky.test/x"));
            }

            return new HttpResponseMessage(request.Method == HttpMethod.Head ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK);
        });

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Equal(
            ["GET https://example.test/shop/", "HEAD https://picky.test/x", "GET https://picky.test/x"],
            handler.Requests);
    }

    [Fact]
    public async Task RunAsync_WhenHeadAndGetBothSayNotFound_TheLinkIsBroken()
    {
        var handler = HandlerFor(PageLinking("https://other.test/gone"), new() { ["https://other.test/gone"] = HttpStatusCode.NotFound });

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("HEAD https://other.test/gone", handler.Requests);
        Assert.Contains("GET https://other.test/gone", handler.Requests);
    }

    [Fact]
    public async Task RunAsync_WhenAWorkingLinkAnswersHead_DoesNotDownloadIt()
    {
        var handler = HandlerFor(PageLinking("https://other.test/big"));

        await Run(handler);

        Assert.DoesNotContain("GET https://other.test/big", handler.Requests);
    }

    [Theory]
    [InlineData("HttpRequestException")]
    [InlineData("Timeout")]
    public async Task RunAsync_WhenALinkDoesNotAnswer_ItIsBroken(string failure)
    {
        var handler = StubHttpMessageHandler.Routing(request =>
        {
            if (request.RequestUri == Site)
            {
                return Page(PageLinking("https://dead.test/x"));
            }

            throw failure == "Timeout"
                ? new TaskCanceledException("timed out")
                : new HttpRequestException("no such host");
        });

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("https://dead.test/x", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/shop/menu", "https://example.test/shop/menu")]
    [InlineData("menu", "https://example.test/shop/menu")]
    [InlineData("./menu", "https://example.test/shop/menu")]
    [InlineData("../about", "https://example.test/about")]
    [InlineData("//cdn.test/x", "https://cdn.test/x")]
    [InlineData("http://plain.test/x", "http://plain.test/x")]
    [InlineData("menu?tab=1&amp;lang=en", "https://example.test/shop/menu?tab=1&lang=en")]
    public async Task RunAsync_ResolvesEveryFormOfLinkAgainstThePage(string href, string expectedRequest)
    {
        var handler = HandlerFor(PageLinking(href));

        await Run(handler);

        Assert.Contains($"HEAD {expectedRequest}", handler.Requests);
    }

    [Fact]
    public async Task RunAsync_ResolvesRelativeLinksAgainstTheBaseTagWhenThereIsOne()
    {
        var handler = HandlerFor("<html><head><base href=\"https://cdn.test/assets/\"></head><body><a href=\"file.pdf\">f</a></body></html>");

        await Run(handler);

        Assert.Contains("HEAD https://cdn.test/assets/file.pdf", handler.Requests);
    }

    [Theory]
    [InlineData("mailto:hello@example.test")]
    [InlineData("tel:+34600000000")]
    [InlineData("javascript:void(0)")]
    [InlineData("#top")]
    [InlineData("")]
    [InlineData("data:text/plain,hi")]
    [InlineData("ftp://files.test/x")]
    public async Task RunAsync_IgnoresLinksThatAreNotWebPages(string href)
    {
        var handler = HandlerFor(PageLinking(href));

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RunAsync_ChecksEachAddressOnceEvenWhenLinkedTwiceOrWithDifferentFragments()
    {
        var handler = HandlerFor(PageLinking("/shop/menu", "/shop/menu#drinks", "/shop/menu"));

        var outcome = await Run(handler);

        Assert.Contains("All 1 link(s)", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests, request => request == "HEAD https://example.test/shop/menu");
    }

    [Fact]
    public async Task RunAsync_IgnoresTagsThatOnlyLookLikeLinks()
    {
        var handler = HandlerFor("<abbr href=\"https://x.test/1\">a</abbr><a-custom href=\"https://x.test/2\"></a-custom><a name=\"anchor\">n</a>");

        await Run(handler);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RunAsync_StopsAtTheLimitAndSaysHowMuchWasChecked()
    {
        var handler = HandlerFor(PageLinking("/1", "/2", "/3", "/4", "/5"));

        var outcome = await Run(handler, new BrokenLinksCheckOptions(MaxLinks: 2, MaxConcurrency: 1, LinkTimeout: TimeSpan.FromSeconds(5)));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("first 2 of 5", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count); // the page and two links
    }

    [Fact]
    public async Task RunAsync_WhenTruncatedAndSomethingIsBroken_SaysTheRestWasNotLookedAt()
    {
        // A clean result over a partial look must not read as "the whole page is fine", and a
        // broken one must not hide that links past the limit were never tried.
        var outcome = await Run(
            HandlerFor(PageLinking("/1", "/2", "/3"), new() { ["https://example.test/1"] = HttpStatusCode.NotFound }),
            new BrokenLinksCheckOptions(MaxLinks: 2, MaxConcurrency: 2, LinkTimeout: TimeSpan.FromSeconds(5)));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("Only the first 2 of 3 links were checked", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ListsAFewBrokenLinksAndCountsTheRest()
    {
        var hrefs = Enumerable.Range(1, 8).Select(n => $"/gone{n}").ToArray();
        var statuses = hrefs.ToDictionary(href => "https://example.test" + href, _ => HttpStatusCode.NotFound);

        var outcome = await Run(PageLinking(hrefs), statuses);

        Assert.Contains("8 of 8", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("gone5", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("gone6", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("and 3 more", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ResolvesLinksAgainstWhereARedirectEnded()
    {
        // The visitor lands on /new/, so "menu" means /new/menu, not /shop/menu.
        var landed = new Uri("https://example.test/new/");
        var handler = StubHttpMessageHandler.Routing(request =>
        {
            if (request.RequestUri == Site)
            {
                var response = Page(PageLinking("menu"));
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, landed);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Run(handler);

        Assert.Contains("HEAD https://example.test/new/menu", handler.Requests);
    }

    [Fact]
    public async Task RunAsync_WhenThePageAnswersWithAnError_FailsWithoutFollowingLinks()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving(PageLinking("/x"), HttpStatusCode.NotFound));

        var outcome = await new BrokenLinksCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("404", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenThePageCannotBeFetched_Fails(bool timedOut)
    {
        Exception failure = timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("refused");
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(failure));

        var outcome = await new BrokenLinksCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException("cancelled")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new BrokenLinksCheck(client).RunAsync(Site, source.Token));
    }

    [Fact]
    public void Options_RejectValuesThatWouldCheckNothingOrNeverFinish()
    {
        var ok = TimeSpan.FromSeconds(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokenLinksCheckOptions(0, 1, ok));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokenLinksCheckOptions(1, 0, ok));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokenLinksCheckOptions(1, 1, TimeSpan.Zero));
    }
}
