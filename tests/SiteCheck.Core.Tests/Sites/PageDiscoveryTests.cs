using System.Net;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Sites;

public sealed class PageDiscoveryTests
{
    private static readonly Site Salon = new(new Uri("https://salon.test/"), "Salon");

    private static string Sitemap(params string[] addresses) =>
        $"""<?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(addresses.Select(address => $"<url><loc>{address}</loc></url>"))}</urlset>""";

    private static string Index(params string[] sitemaps) =>
        $"""<?xml version="1.0"?><sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(sitemaps.Select(address => $"<sitemap><loc>{address}</loc></sitemap>"))}</sitemapindex>""";

    /// <summary>A site where only the given addresses answer, each with its body.</summary>
    private static StubHttpMessageHandler SiteWith(params (string Address, string Body)[] files) =>
        StubHttpMessageHandler.Routing(request =>
            files.Where(file => file.Address == request.RequestUri!.AbsoluteUri)
                .Select(file => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(file.Body) })
                .FirstOrDefault()
            ?? new HttpResponseMessage(HttpStatusCode.NotFound));

    private static async Task<IReadOnlyList<Site>> Expand(StubHttpMessageHandler handler, int maxPages = 10, Site? site = null)
    {
        using var client = new HttpClient(handler);
        return await new PageDiscovery(client).ExpandAsync(site ?? Salon, maxPages, TestContext.Current.CancellationToken);
    }

    private static string[] Addresses(IEnumerable<Site> sites) => [.. sites.Select(site => site.Url.AbsoluteUri)];

    [Fact]
    public async Task ExpandAsync_AskedForOnePage_ReturnsTheSiteAndAsksNothing()
    {
        var handler = SiteWith();

        var pages = await Expand(handler, maxPages: 1);

        Assert.Equal([Salon], pages);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExpandAsync_ReadsThePagesFromTheSitemap_WithTheGivenSiteFirstAndUnchanged()
    {
        var handler = SiteWith(("https://salon.test/sitemap.xml", Sitemap("https://salon.test/", "https://salon.test/services", "https://salon.test/contact")));

        var pages = await Expand(handler);

        Assert.Same(Salon, pages[0]);
        Assert.Equal(["https://salon.test/", "https://salon.test/services", "https://salon.test/contact"], Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_NamesEachExtraPage_AndRunsHostWideChecksOnTheFirstOnly()
    {
        var site = Salon with { Skip = new HashSet<string> { "booking-link" } };
        var handler = SiteWith(("https://salon.test/sitemap.xml", Sitemap("https://salon.test/contact")));

        var pages = await Expand(handler, site: site);

        Assert.Equal("Salon › /contact", pages[1].Name);

        // A certificate is the same for every page; checking it per page is the same answer again.
        Assert.Contains("ssl-certificate", pages[1].Skip);
        Assert.Contains("security-headers", pages[1].Skip);
        Assert.DoesNotContain("search-snippet", pages[1].Skip);

        // What the site itself skips, its pages skip too.
        Assert.Contains("booking-link", pages[1].Skip);
        Assert.DoesNotContain("ssl-certificate", pages[0].Skip);
    }

    [Fact]
    public async Task ExpandAsync_StopsAtTheNumberOfPagesAskedFor_PreferringTheOnesNearestTheTop()
    {
        var handler = SiteWith(("https://salon.test/sitemap.xml", Sitemap(
            "https://salon.test/blog/2019/03/old-post", "https://salon.test/contact", "https://salon.test/blog/news", "https://salon.test/prices")));

        var pages = await Expand(handler, maxPages: 3);

        Assert.Equal(["https://salon.test/", "https://salon.test/contact", "https://salon.test/prices"], Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_UsesTheSitemapRobotsTxtPointsAt()
    {
        var handler = SiteWith(
            ("https://salon.test/robots.txt", "User-agent: *\nDisallow: /admin\nSitemap: https://salon.test/pages.xml\n"),
            ("https://salon.test/pages.xml", Sitemap("https://salon.test/about")));

        var pages = await Expand(handler);

        Assert.Equal(["https://salon.test/", "https://salon.test/about"], Addresses(pages));
        Assert.DoesNotContain("GET https://salon.test/sitemap.xml", handler.Requests);
    }

    [Fact]
    public async Task ExpandAsync_FollowsASitemapIndex_ButOnlySoFar()
    {
        var children = Enumerable.Range(1, 6).Select(i => $"https://salon.test/map{i}.xml").ToArray();
        var files = children.Select((child, i) => (child, Sitemap($"https://salon.test/page{i + 1}")))
            .Append(("https://salon.test/sitemap.xml", Index(children)))
            .ToArray();
        var handler = SiteWith(files);

        var pages = await Expand(handler, maxPages: 50);

        // The index and three of its six children: the number of requests is bounded up front.
        Assert.Equal(["https://salon.test/", "https://salon.test/page1", "https://salon.test/page2", "https://salon.test/page3"], Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_KeepsOnlyWebPagesOfTheSameSite_OnceEach()
    {
        var handler = SiteWith(("https://salon.test/sitemap.xml", Sitemap(
            "https://salon.test/",                 // the site itself, already first
            "https://salon.test/contact",
            "https://salon.test/contact/",         // the same page, with a slash
            "http://www.salon.test/contact",       // and again, as www over http
            "https://www.salon.test/team",         // www is still this site
            "https://other.test/page",             // someone else's
            "https://salon.test/menu.pdf",         // a file, not a page
            "https://salon.test/logo.PNG",
            "mailto:info@salon.test",
            "not an address")));

        var pages = await Expand(handler);

        Assert.Equal(["https://salon.test/", "https://salon.test/contact", "https://www.salon.test/team"], Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_WithNoSitemap_FallsBackToTheLinksOnTheGivenPage()
    {
        var handler = SiteWith(("https://salon.test/", """
            <html><body>
              <a href="/services">Services</a> <a href="contact.html">Contact</a>
              <a href="https://instagram.test/salon">Instagram</a> <a href="#top">Top</a> <a href="tel:+34600000000">Call</a>
            </body></html>
            """));

        var pages = await Expand(handler);

        // /services and /contact.html are linked but do not answer here, so they are left to
        // broken-links; only the given page comes back.
        Assert.Equal(["https://salon.test/"], Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_FromLinks_KeepsOnlyThePagesThatAnswer()
    {
        // A broken link is broken-links' finding on the page that has it, not a page to audit.
        var handler = SiteWith(
            ("https://salon.test/", """<a href="/services">Services</a> <a href="/gone">Old</a> <a href="/contact">Contact</a>"""),
            ("https://salon.test/services", "<html></html>"),
            ("https://salon.test/contact", "<html></html>"));

        var pages = await Expand(handler);

        Assert.Equal(["https://salon.test/", "https://salon.test/services", "https://salon.test/contact"], Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_FromLinks_TriesOnlySoManyBeforeGivingUp()
    {
        // A page with a hundred dead links must not cost a hundred requests to find that out.
        var links = string.Concat(Enumerable.Range(0, 100).Select(i => $"""<a href="/gone{i}">x</a>"""));
        var handler = SiteWith(("https://salon.test/", links));

        await Expand(handler, maxPages: 4);

        Assert.Equal(6, handler.Requests.Count(request => request.Contains("/gone", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExpandAsync_FromASitemap_TrustsTheListWithoutAskingForEachPage()
    {
        var handler = SiteWith(("https://salon.test/sitemap.xml", Sitemap("https://salon.test/a", "https://salon.test/b")));

        var pages = await Expand(handler);

        Assert.Equal(3, pages.Count);
        Assert.DoesNotContain("GET https://salon.test/a", handler.Requests);
    }

    public static TheoryData<string> BrokenSitemaps =>
    [
        "this is not xml",
        "<urlset><url><loc>https://salon.test/a</loc></url><url><loc>https://salon.test/b",                       // cut off
        """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><urlset><url><loc>&e;</loc></url></urlset>""", // XXE
        string.Empty,
    ];

    [Theory]
    [MemberData(nameof(BrokenSitemaps))]
    public async Task ExpandAsync_WhenTheSitemapIsBrokenOrHostile_NeverThrows(string sitemap)
    {
        var handler = SiteWith(("https://salon.test/sitemap.xml", sitemap));

        var pages = await Expand(handler);

        Assert.Same(Salon, pages[0]);
        Assert.DoesNotContain(pages, page => page.Url.AbsolutePath.Contains("passwd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpandAsync_KeepsWhatItReadBeforeASitemapBrokeOff()
    {
        var handler = SiteWith(("https://salon.test/sitemap.xml", "<urlset><url><loc>https://salon.test/a</loc></url><url><loc>https://salon.test/b"));

        var pages = await Expand(handler);

        Assert.Contains("https://salon.test/a", Addresses(pages));
    }

    [Fact]
    public async Task ExpandAsync_WhenTheSiteDoesNotAnswer_ReturnsJustTheSite()
    {
        var pages = await Expand(StubHttpMessageHandler.Throwing(new HttpRequestException("down")));

        Assert.Equal([Salon], pages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ExpandAsync_AskedForNoPages_Throws(int maxPages)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Expand(SiteWith(), maxPages));
    }
}
