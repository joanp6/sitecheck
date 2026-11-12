using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class SubresourceIntegrityCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private static async Task<CheckOutcome> RunAgainst(string head)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving($"<html><head>{head}</head><body></body></html>"));
        return await new SubresourceIntegrityCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""<script src="/js/app.js"></script>""")]                                        // the site's own
    [InlineData("""<script>console.log("inline")</script>""")]
    [InlineData("""<script src="https://www.googletagmanager.com/gtag/js?id=G-1"></script>""")]   // changes by design
    [InlineData("""<script src="https://widget.booksy.test/embed.js"></script>""")]
    [InlineData("""<link rel="preconnect" href="https://cdn.jsdelivr.net">""")]                   // not a stylesheet
    public async Task RunAsync_WhenNothingComesFromALibraryServer_Passes(string head)
    {
        var outcome = await RunAgainst(head);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("""<script src="https://code.jquery.com/jquery-3.7.1.min.js" integrity="sha256-abc" crossorigin="anonymous"></script>""")]
    [InlineData("""<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.2/dist/css/bootstrap.min.css" integrity="sha384-abc">""")]
    public async Task RunAsync_WhenLibraryFilesCarryAFingerprint_Passes(string head)
    {
        var outcome = await RunAgainst(head);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("fingerprint", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<script src="https://code.jquery.com/jquery-3.7.1.min.js"></script>""", "https://code.jquery.com/jquery-3.7.1.min.js")]
    [InlineData("""<script src="//cdnjs.cloudflare.com/ajax/libs/gsap/3.12.2/gsap.min.js"></script>""", "https://cdnjs.cloudflare.com/ajax/libs/gsap/3.12.2/gsap.min.js")]
    [InlineData("""<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">""", "https://unpkg.com/leaflet@1.9.4/dist/leaflet.css")]
    [InlineData("""<link rel="preload stylesheet" href="https://CDN.jsdelivr.net/npm/x@1/x.css">""", "https://cdn.jsdelivr.net/npm/x@1/x.css")]
    [InlineData("""<script src="https://code.jquery.com/jquery.js" integrity=""></script>""", "https://code.jquery.com/jquery.js")] // an empty one protects nothing
    public async Task RunAsync_WhenALibraryFileHasNoFingerprint_Warns_NamingIt(string head, string named)
    {
        var outcome = await RunAgainst(head);

        // Nothing is wrong today, so never a defect.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains(named, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<script src="https://polyfill.io/v3/polyfill.min.js"></script>""")]
    [InlineData("""<script src="https://cdn.polyfill.io/v2/polyfill.min.js"></script>""")]
    [InlineData("""<script src="https://cdn.bootcss.com/jquery/3.4.1/jquery.min.js" integrity="sha384-abc"></script>""")] // a fingerprint does not redeem it
    public async Task RunAsync_WhenAScriptComesFromAServerKnownToHaveServedMalware_Fails(string head)
    {
        var outcome = await RunAgainst(head);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_DoesNotMistakeALookalikeDomainForACompromisedOne()
    {
        var outcome = await RunAgainst("""<script src="https://notpolyfill.io/x.js"></script>""");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WithManyBareFiles_ListsAFewAndCountsTheRest_OnceEach()
    {
        var tags = string.Concat(Enumerable.Range(0, 5).Select(i => $"""<script src="https://unpkg.com/lib{i}@1/x.js"></script>"""));

        var outcome = await RunAgainst(tags + """<script src="https://unpkg.com/lib0@1/x.js"></script>""");

        Assert.Contains("5 files", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("and 2 more", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_LeavesAQueryStringOutOfTheReport()
    {
        // Addresses on the page are not ours; a query can carry a key.
        var outcome = await RunAgainst("""<script src="https://unpkg.com/x@1/x.js?key=SECRET"></script>""");

        Assert.DoesNotContain("SECRET", outcome.Detail, StringComparison.Ordinal);
    }
}
