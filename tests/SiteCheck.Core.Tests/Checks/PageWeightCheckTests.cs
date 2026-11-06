using System.Collections.Concurrent;
using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class PageWeightCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    /// <summary>
    /// Serves <paramref name="html"/> for the page, and for anything else a body of the size in
    /// <paramref name="sizes"/>, declared in Content-Length without allocating it.
    /// </summary>
    private sealed class FakeSite(string html, Dictionary<string, long> sizes)
    {
        public ConcurrentQueue<string> Requests { get; } = [];

        public HashSet<string> RefuseHead { get; init; } = [];

        public HashSet<string> NoLength { get; init; } = [];

        public Dictionary<string, HttpStatusCode> Statuses { get; init; } = [];

        public StubHttpMessageHandler Handler => StubHttpMessageHandler.Routing(request =>
        {
            var address = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue($"{request.Method} {address} {request.Headers.AcceptEncoding}");

            if (request.RequestUri == Site)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
            }

            if (Statuses.TryGetValue(address, out var status))
            {
                return new HttpResponseMessage(status);
            }

            if (request.Method == HttpMethod.Head && RefuseHead.Contains(address))
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            if (!sizes.TryGetValue(address, out var size))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (NoLength.Contains(address))
            {
                // A chunked answer: the size is only known by reading it.
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(size) };
            }

            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = size;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
    }

    private static async Task<CheckOutcome> Run(FakeSite site)
    {
        using var client = new HttpClient(site.Handler);
        return await new PageWeightCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static string Page(string body) => $"<!doctype html><html><head><title>Salon</title></head><body>{body}</body></html>";

    [Fact]
    public async Task RunAsync_WhenThePageIsLight_PassesAndReportsTheTotalAsAValue()
    {
        var html = Page("""<img src="/logo.png"><script src="/app.js"></script><link rel="stylesheet" href="/site.css">""");
        var site = new FakeSite(html, new()
        {
            ["https://salon.test/logo.png"] = 40_000,
            ["https://salon.test/app.js"] = 30_000,
            ["https://salon.test/site.css"] = 10_000,
        });

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Equal(80_000 + System.Text.Encoding.UTF8.GetByteCount(html), outcome.Bytes);
        Assert.Contains("3 of the images, scripts and stylesheets", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AsksForTheSizeWithoutDownloadingAndAsksForCompression()
    {
        var site = new FakeSite(Page("""<script src="/app.js"></script>"""), new() { ["https://salon.test/app.js"] = 30_000 });

        await Run(site);

        var request = Assert.Single(site.Requests, r => r.Contains("app.js", StringComparison.Ordinal));
        Assert.StartsWith("HEAD ", request, StringComparison.Ordinal);
        Assert.Contains("br", request, StringComparison.Ordinal);
        Assert.Contains("gzip", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenOneImageIsHeavy_WarnsAndNamesIt()
    {
        var site = new FakeSite(Page("""<img src="/fotos/IMG_2041.jpg"><img src="/logo.png">"""), new()
        {
            ["https://salon.test/fotos/IMG_2041.jpg"] = 900_000,
            ["https://salon.test/logo.png"] = 20_000,
        });

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("One image is far heavier than a web page needs: IMG_2041.jpg (900 kB)", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("logo.png", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenAnImageIsAPhotoStraightFromTheCamera_Fails()
    {
        var site = new FakeSite(Page("""<img src="/hero.jpg">"""), new() { ["https://salon.test/hero.jpg"] = 2_400_000 });

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("hero.jpg (2.4 MB)", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("WebP", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenManyHeavyImagesAddUp_FailsListsTheHeaviestAndCountsTheRest()
    {
        var images = Enumerable.Range(1, 6).ToDictionary(i => $"https://salon.test/g/{i}.jpg", i => 1_000_000L + (i * 10_000));
        var site = new FakeSite(Page(string.Concat(images.Keys.Select(k => $"<img src=\"{k}\">"))), images);

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("6 images are", outcome.Detail, StringComparison.Ordinal);
        Assert.StartsWith("6.jpg", outcome.Detail[(outcome.Detail.IndexOf("needs: ", StringComparison.Ordinal) + 7)..], StringComparison.Ordinal);
        Assert.Contains("and 3 more", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("mobile data", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenScriptsMakeThePageHeavy_WarnsAndNamesTheHeaviestParts()
    {
        var site = new FakeSite(Page("""<script src="/bundle.js"></script><script src="/vendor.js"></script>"""), new()
        {
            ["https://salon.test/bundle.js"] = 2_000_000,
            ["https://salon.test/vendor.js"] = 1_500_000,
        });

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("heavy for a phone", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("The heaviest parts: bundle.js (2.0 MB), vendor.js (1.5 MB)", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenHeadIsRefused_AsksWithGet()
    {
        var site = new FakeSite(Page("""<img src="/a.jpg">"""), new() { ["https://salon.test/a.jpg"] = 900_000 })
        {
            RefuseHead = ["https://salon.test/a.jpg"],
        };

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains(site.Requests, r => r.StartsWith("GET https://salon.test/a.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenTheServerWillNotSayTheSize_ReadsIt()
    {
        var site = new FakeSite(Page("""<img src="/a.jpg">"""), new() { ["https://salon.test/a.jpg"] = 600_000 })
        {
            NoLength = ["https://salon.test/a.jpg"],
        };

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("a.jpg (600 kB)", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenAPartCannotBeWeighed_LeavesItOutAndSaysSo()
    {
        var site = new FakeSite(Page("""<img src="/gone.jpg"><img src="/blocked.jpg"><img src="/ok.png">"""), new() { ["https://salon.test/ok.png"] = 10_000 })
        {
            Statuses = { ["https://salon.test/blocked.jpg"] = HttpStatusCode.Forbidden },
        };

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("2 more could not be weighed", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(site.Requests, r => r.StartsWith("GET https://salon.test/gone.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenAPartFailsToAnswer_IsNotAnErrorOfTheCheck()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Routing(request =>
            request.RequestUri == Site
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page("""<img src="https://cdn.test/a.jpg">""")) }
                : throw new HttpRequestException("refused")));

        var outcome = await new PageWeightCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("1 more could not be weighed", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WeighsTheSameAddressOnceAndSkipsWhatIsNotFetched()
    {
        var site = new FakeSite(
            Page("""<img src="/a.png"><img src="/a.png"><img src="data:image/png;base64,AAAA"><img alt="no source"><script>inline()</script>"""),
            new() { ["https://salon.test/a.png"] = 10_000 });

        var outcome = await Run(site);

        Assert.Contains("1 of the images", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(site.Requests, r => r.Contains("a.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_UsesTheFirstSrcsetCandidateWhenThereIsNoSrc()
    {
        var site = new FakeSite(Page("""<img srcset="/big.jpg 2x, /small.jpg 1x">"""), new() { ["https://salon.test/big.jpg"] = 900_000 });

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("big.jpg", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WeighsNoMoreThanTheLimitOfParts()
    {
        var images = Enumerable.Range(1, PageWeightCheck.MaxParts + 10).ToDictionary(i => $"https://salon.test/{i}.png", _ => 1_000L);
        var site = new FakeSite(Page(string.Concat(images.Keys.Select(k => $"<img src=\"{k}\">"))), images);

        await Run(site);

        Assert.Equal(PageWeightCheck.MaxParts, site.Requests.Count(r => r.Contains(".png", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("Access denied", status));

        var outcome = await new PageWeightCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
        Assert.Null(outcome.Bytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("x", status));

        var outcome = await new PageWeightCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenThePageCannotBeFetched_IsAnErrorNotAFail(bool timedOut)
    {
        Exception failure = timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("refused");
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(failure));

        var outcome = await new PageWeightCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Error, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesInsteadOfReportingAnError()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PageWeightCheck(client).RunAsync(Site, cancellation.Token));
    }

    [Fact]
    public void Options_RejectSwappedThresholds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageWeightCheckOptions(2_000_000, 500_000, 3_000_000, 6_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageWeightCheckOptions(500_000, 2_000_000, 6_000_000, 3_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageWeightCheckOptions(0, 2_000_000, 3_000_000, 6_000_000));
    }
}

/// <summary>A body whose length is only known by reading it, as with a chunked answer.</summary>
internal sealed class UnsizedContent(long size) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        var chunk = new byte[8192];

        for (var left = size; left > 0; left -= chunk.Length)
        {
            await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)));
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
