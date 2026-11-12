using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class CmsVersionCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string XmlRpcSentence = "XML-RPC server accepts POST requests only.";

    private static string Page(string head, string body = "") => $"<html><head>{head}</head><body>{body}</body></html>";

    private static string Generator(string content) => $"""<meta name="generator" content="{content}">""";

    /// <summary>
    /// A site serving <paramref name="html"/>, with wordpress.org saying the current release is
    /// <paramref name="latest"/> (or failing when it is <see langword="null"/>).
    /// </summary>
    private static StubHttpMessageHandler SiteServing(string html, string? latest = "6.8.3", bool xmlRpcOpen = false) =>
        StubHttpMessageHandler.Routing(request =>
        {
            if (request.RequestUri!.Host == "api.wordpress.org")
            {
                return latest is null
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""{"offers":[{"response":"upgrade","version":"{{latest}}"}]}"""),
                    };
            }

            if (request.RequestUri.AbsolutePath == "/xmlrpc.php")
            {
                return xmlRpcOpen
                    ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { Content = new StringContent(XmlRpcSentence) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new CmsVersionCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WhenThePageNamesNoSystem_Skips_AndAsksNobodyElse()
    {
        var handler = SiteServing(Page("<title>Hand-written</title>"));

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal(["GET https://salon.test/"], handler.Requests);
    }

    [Fact]
    public async Task RunAsync_WhenWordPressIsBehindTheCurrentRelease_Warns_SayingSo()
    {
        var outcome = await Run(SiteServing(Page(Generator("WordPress 5.8.2"))));

        // A risk, not a break-in.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("WordPress 5.8.2", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("now at 6.8", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("behind", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("6.8.3")] // exactly current
    [InlineData("6.8.1")] // a patch behind: the same release line, which still gets fixes
    [InlineData("6.9")]   // ahead of what wordpress.org offers, a beta
    public async Task RunAsync_WhenWordPressIsOnTheCurrentLine_StillWarnsAboutAnnouncingIt_ButNotAboutBeingBehind(string version)
    {
        var outcome = await Run(SiteServing(Page(Generator("WordPress " + version))));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.DoesNotContain("behind", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenWordPressOrgCannotBeAsked_ReportsTheVersionWithoutComparing()
    {
        var outcome = await Run(SiteServing(Page(Generator("WordPress 5.8.2")), latest: null));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("WordPress 5.8.2", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("behind", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<link rel="stylesheet" href="/wp-content/themes/salon/style.css">""")]
    [InlineData("""<script src="https://salon.test/wp-includes/js/jquery/jquery.min.js"></script>""")]
    public async Task RunAsync_WhenWordPressHidesItsVersion_Passes_NamingIt(string head)
    {
        var outcome = await Run(SiteServing(Page(head)));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("WordPress", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenWordPressLeavesXmlRpcOpen_MentionsIt_WithoutMakingItAWarningOfItsOwn()
    {
        var outcome = await Run(SiteServing(Page("""<link rel="stylesheet" href="/wp-content/x.css">"""), xmlRpcOpen: true));

        // It is how WordPress ships, and some sites need it.
        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("/xmlrpc.php", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotTakeAnyPageAtXmlRpcForTheRealThing()
    {
        // A site that answers 200 with its home page for every address has no XML-RPC.
        var handler = StubHttpMessageHandler.Routing(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page("""<link rel="stylesheet" href="/wp-content/x.css">""")) });

        var outcome = await Run(handler);

        Assert.DoesNotContain("/xmlrpc.php", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OnASiteThatIsNotWordPress_DoesNotAskForXmlRpcOrWordPressOrg()
    {
        var handler = SiteServing(Page(Generator("Joomla! - Open Source Content Management")));

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("Joomla!", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(["GET https://salon.test/"], handler.Requests);
    }

    [Theory]
    [InlineData("Elementor 3.18.3; features: e_dom_optimization", "Elementor 3.18.3")]
    [InlineData("WooCommerce 8.4.0", "WooCommerce 8.4.0")]
    [InlineData("Site Kit by Google 1.116.0", "Site Kit by Google 1.116.0")]
    [InlineData("Hugo 0.121.1", "Hugo 0.121.1")]
    [InlineData("Gatsby v5.13.1", "Gatsby 5.13.1")]
    [InlineData("PrestaShop - 8.1", "PrestaShop 8.1")]
    public async Task RunAsync_ReadsNameAndVersionHoweverTheTagWritesThem(string content, string reported)
    {
        var outcome = await Run(SiteServing(Page(Generator(content))));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains(reported, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Wix.com Website Builder")]
    [InlineData("Drupal 10 (https://www.drupal.org)")] // a major alone says little, and Drupal says no more
    public async Task RunAsync_WhenAGeneratorGivesNoExactVersion_Passes(string content)
    {
        var outcome = await Run(SiteServing(Page(Generator(content))));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_ListsEveryVersionAnnounced_PluginsIncluded()
    {
        var outcome = await Run(SiteServing(Page(Generator("WordPress 6.8.3") + Generator("WooCommerce 8.4.0"))));

        Assert.Contains("WordPress 6.8.3", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("WooCommerce 8.4.0", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AsksWordPressOrgOncePerRun_NotOncePerSite()
    {
        var handler = SiteServing(Page(Generator("WordPress 5.8.2")));
        using var client = new HttpClient(handler);
        var check = new CmsVersionCheck(client);

        await check.RunAsync(Site, TestContext.Current.CancellationToken);
        await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests, request => request.Contains("api.wordpress.org", StringComparison.Ordinal));
    }
}
