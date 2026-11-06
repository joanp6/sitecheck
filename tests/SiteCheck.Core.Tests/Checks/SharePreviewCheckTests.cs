using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class SharePreviewCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string Image = """<meta property="og:image" content="https://salon.test/share.jpg">""";
    private const string Title = """<meta property="og:title" content="Salon Test, hair salon in Mataró">""";
    private const string Icon = """<link rel="icon" href="/icon.png">""";

    /// <summary>
    /// Serves the page, and for any other address the answer in <paramref name="answers"/>:
    /// a content type for a 200, or a status. Anything unlisted is a 404.
    /// </summary>
    private sealed class FakeSite(string head, Dictionary<string, object> answers)
    {
        public ConcurrentQueue<string> Requests { get; } = [];

        public StubHttpMessageHandler Handler => StubHttpMessageHandler.Routing(Respond);

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var address = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue($"{request.Method} {address}");

            if (request.RequestUri == Site)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<!doctype html><html><head><title>Salon</title>{head}</head><body></body></html>"),
                };
            }

            switch (answers.GetValueOrDefault(address))
            {
                case string type:
                    var content = new ByteArrayContent([]);
                    content.Headers.ContentType = new MediaTypeHeaderValue(type);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                case HttpStatusCode status:
                    return new HttpResponseMessage(status);
                case Exception failure:
                    throw failure;
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    private static readonly Dictionary<string, object> Healthy = new()
    {
        ["https://salon.test/share.jpg"] = "image/jpeg",
        ["https://salon.test/icon.png"] = "image/png",
    };

    private static async Task<CheckOutcome> Run(FakeSite site)
    {
        using var client = new HttpClient(site.Handler);
        return await new SharePreviewCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> Run(string head, Dictionary<string, object>? answers = null) =>
        Run(new FakeSite(head, answers ?? Healthy));

    [Fact]
    public async Task RunAsync_WhenThePreviewAndIconAreThere_Passes()
    {
        var outcome = await Run(Image + Title + Icon);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("its own title and picture", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithoutOgTitle_PassesSinceThePagesTitleIsUsed()
    {
        var outcome = await Run(Image + Icon);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("under the page's title", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<meta name="og:image" content="https://salon.test/share.jpg">""")]          // name instead of property
    [InlineData("""<meta content="https://salon.test/share.jpg" PROPERTY="OG:IMAGE">""")]
    [InlineData("""<meta property="og:image" content="https://salon.test/share.jpg?v=1&amp;x=2">""")]
    public async Task RunAsync_ReadsTheImageTagAsCrawlersDo(string image)
    {
        var answers = new Dictionary<string, object>(Healthy) { ["https://salon.test/share.jpg?v=1&x=2"] = "image/jpeg" };

        var outcome = await Run(image + Icon, answers);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""<meta property="og:image" content="">""")]
    [InlineData("""<meta name="twitter:image" content="https://salon.test/share.jpg">""")]   // not what WhatsApp reads
    public async Task RunAsync_WhenThereIsNoOgImage_WarnsAndSaysWhatToAdd(string head)
    {
        var outcome = await Run(head + Icon);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("shows no picture: it has no og:image", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/share.jpg")]
    [InlineData("share.jpg")]
    [InlineData("//salon.test/share.jpg")]
    public async Task RunAsync_WhenTheOgImageIsNotAFullAddress_Warns(string content)
    {
        var outcome = await Run($"""<meta property="og:image" content="{content}">""" + Icon);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("not a full address", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheOgImageIsGone_Warns()
    {
        var outcome = await Run(Image + Icon, new() { ["https://salon.test/icon.png"] = "image/png" });

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("https://salon.test/share.jpg, does not load", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheOgImageAnswersWithAWebPage_WarnsThatItIsNotAnImage()
    {
        // A site that sends every unknown address to its home page answers 200, with HTML.
        var answers = new Dictionary<string, object>(Healthy) { ["https://salon.test/share.jpg"] = "text/html" };

        var outcome = await Run(Image + Icon, answers);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("answers with a web page rather than an image", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenNoIconIsDeclared_LooksForFaviconIco()
    {
        var answers = new Dictionary<string, object>(Healthy) { ["https://salon.test/favicon.ico"] = "image/x-icon" };
        var site = new FakeSite(Image, answers);

        var outcome = await Run(site);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("HEAD https://salon.test/favicon.ico", site.Requests);
    }

    [Theory]
    [InlineData(null)]           // nothing at /favicon.ico
    [InlineData("text/html")]    // the home page served for /favicon.ico
    public async Task RunAsync_WhenThereIsNoFavicon_WarnsAndSaysWhatToAdd(string? faviconType)
    {
        var answers = new Dictionary<string, object>(Healthy);

        if (faviconType is not null)
        {
            answers["https://salon.test/favicon.ico"] = faviconType;
        }

        var outcome = await Run(Image, answers);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("The page has no favicon", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"icon\"", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<link rel="shortcut icon" href="/icon.png">""")]
    [InlineData("""<link rel="apple-touch-icon" href="/icon.png">""")]
    [InlineData("""<link rel="icon" href="data:image/svg+xml,%3Csvg%3E%3C/svg%3E">""")]
    public async Task RunAsync_RecognisesTheWaysAnIconIsDeclared(string icon)
    {
        var outcome = await Run(Image + icon);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheDeclaredIconIsGone_WarnsAndNamesIt()
    {
        var outcome = await Run(Image + """<link rel="icon" href="/old-logo.png">""");

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("https://salon.test/old-logo.png, does not load", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenHeadIsRefused_AsksWithGet()
    {
        var site = new FakeSite(Image + Icon, Healthy);
        using var client = new HttpClient(StubHttpMessageHandler.Routing(request =>
            request.Method == HttpMethod.Head && request.RequestUri != Site
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : site.Respond(request)));

        var outcome = await new SharePreviewCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenTheImageServerTurnsRobotsAway_ConcludesNothingAboutIt(HttpStatusCode status)
    {
        var answers = new Dictionary<string, object>(Healthy) { ["https://salon.test/share.jpg"] = status };

        var outcome = await Run(Image + Icon, answers);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("could not ask for the og:image", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheImageCannotBeReached_ConcludesNothingAboutIt()
    {
        var answers = new Dictionary<string, object>(Healthy) { ["https://salon.test/share.jpg"] = new HttpRequestException("refused") };

        var outcome = await Run(Image + Icon, answers);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("could not ask for the og:image", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenEverythingIsMissing_ReportsBothInOneWarning()
    {
        var outcome = await Run(string.Empty, new Dictionary<string, object>());

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no og:image", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("no favicon", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("Access denied", status));

        var outcome = await new SharePreviewCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("x", status));

        var outcome = await new SharePreviewCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

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

        var outcome = await new SharePreviewCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Error, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesInsteadOfReportingAnError()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SharePreviewCheck(client).RunAsync(Site, cancellation.Token));
    }
}
