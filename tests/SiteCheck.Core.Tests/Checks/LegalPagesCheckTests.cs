using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class LegalPagesCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string Legal = """<a href="/aviso-legal">Aviso legal</a>""";
    private const string Privacy = """<a href="/privacidad">Política de privacidad</a>""";
    private const string Cookies = """<a href="/cookies">Política de cookies</a>""";
    private const string Analytics = """<script async src="https://www.googletagmanager.com/gtag/js?id=G-TEST"></script>""";

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new LegalPagesCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string footer, string head = "", HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving(
            $"<!doctype html><html><head><title>Salon</title>{head}</head><body><main>Welcome</main><footer>{footer}</footer></body></html>",
            status));

    [Fact]
    public async Task RunAsync_WhenAllThreeAreLinked_Passes()
    {
        var outcome = await RunAgainst(Legal + Privacy + Cookies, Analytics);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("a legal notice, a privacy policy and a cookie policy", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenNothingSetsCookies_DoesNotAskForACookiePolicy()
    {
        var outcome = await RunAgainst(Legal + Privacy);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("no cookie policy was looked for", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<a href="/avis-legal">Avís legal</a><a href="/privacitat">Política de privacitat</a>""")]          // Catalan
    [InlineData("""<a href="/legal-notice">Legal notice</a><a href="/privacy">Privacy policy</a>""")]               // English
    [InlineData("""<a href="/aviso-legal-y-privacidad">Aviso legal y política de privacidad</a>""")]                // one page for both
    [InlineData("""<a href="/legal">Legal</a><a href="/proteccion-de-datos">Protección de datos</a>""")]
    [InlineData("""<a href="/legal.html">Información</a><a href="/rgpd">RGPD</a>""")]                             // by address alone
    [InlineData("""<a href="/p/1">AVISO LEGAL</a><a href="/p/2">PRIVACIDAD</a>""")]                               // by words alone, any case
    [InlineData("""<a href="/p/1"><span>Aviso</span> <b>legal</b></a><a href="/p/2" title="Privacidad"><svg></svg></a>""")]
    [InlineData("""<a href="/Politica_De_Privacidad.html">x</a><a href="/aviso-legal/">x</a>""")]
    [InlineData("""<a href="/p/1">Aviso&nbsp;legal</a><a href="/p/2">Pol&iacute;tica de privacidad</a>""")]
    public async Task RunAsync_RecognisesTheLinksInTheWaysSitesWriteThem(string footer)
    {
        var outcome = await RunAgainst(footer);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheLegalNoticeIsMissing_WarnsAndNamesIt()
    {
        var outcome = await RunAgainst(Privacy);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("could not find a link to a legal notice", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("privacy policy (", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThePrivacyPolicyIsMissing_WarnsAndNamesIt()
    {
        var outcome = await RunAgainst(Legal);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("could not find a link to a privacy policy", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<script src="https://www.googletagmanager.com/gtag/js?id=G-TEST"></script>""", "Google Tag Manager")]
    [InlineData("""<script src="https://connect.facebook.net/en_US/fbevents.js"></script>""", "the Facebook pixel")]
    [InlineData("""<iframe src="https://www.youtube.com/embed/abc"></iframe>""", "an embedded YouTube video")]
    [InlineData("""<iframe src="https://www.google.com/maps/embed?pb=x"></iframe>""", "an embedded Google map")]
    public async Task RunAsync_WhenSomethingSetsCookiesWithoutACookiePolicy_WarnsAndSaysWhat(string setter, string named)
    {
        var outcome = await RunAgainst(Legal + Privacy, setter);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains($"a cookie policy, which the page needs because it loads {named}", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenSeveralThingsSetCookies_ListsThemAll()
    {
        var outcome = await RunAgainst(
            Legal + Privacy,
            Analytics + """<iframe src="https://www.youtube.com/embed/abc"></iframe>""");

        Assert.Contains("loads Google Tag Manager and an embedded YouTube video", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<script id="Cookiebot" src="https://consent.cookiebot.com/uc.js"></script>""")]
    [InlineData("""<script src="https://cdn-cookieyes.com/client_data/abc/script.js"></script>""")]
    [InlineData("""<script src="https://cdn.cookielaw.org/scripttemplates/otSDKStub.js"></script>""")]
    [InlineData("""<script src="/wp-content/plugins/complianz-gdpr/assets/js/complianz.min.js"></script>""")]
    public async Task RunAsync_WhenAConsentToolIsPresent_CountsItAsTheCookiePolicy(string tool)
    {
        var outcome = await RunAgainst(Legal + Privacy, Analytics + tool);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenNoneIsLinked_ListsAllThatApply()
    {
        var outcome = await RunAgainst("<a href=\"/contact\">Contact</a>", Analytics);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("a legal notice", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("a privacy policy", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("a cookie policy", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<p>Aviso legal · Política de privacidad</p>""")]                                  // words, but no link
    [InlineData("""<!-- <a href="/aviso-legal">Aviso legal</a><a href="/privacidad">Privacidad</a> -->""")]
    [InlineData("""<a href="https://privacy-first.test/">Partner</a><a href="https://legal.test/">Partner</a>""")] // a host is not a page name
    [InlineData("""<a href="/legales-y-mas">Ofertas</a><a href="/blog/illegal-parking">Blog</a>""")]   // "legal" inside other words
    public async Task RunAsync_DoesNotCountWhatIsNotALinkToTheLegalPages(string footer)
    {
        var outcome = await RunAgainst(footer);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("a legal notice", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverFailsAndNeverSaysTheSiteBreaksTheLaw()
    {
        var outcome = await RunAgainst(string.Empty, Analytics);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("could not find", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("not legal advice", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("illegal", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("breaks", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        var outcome = await RunAgainst(string.Empty, status: status);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        var outcome = await RunAgainst(Legal + Privacy, status: status);

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
            () => new LegalPagesCheck(client).RunAsync(Site, cancellation.Token));
    }
}
