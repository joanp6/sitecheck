using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class LocalBusinessCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string Address = """
        "address": { "@type": "PostalAddress", "streetAddress": "Carrer de Prova 1", "addressLocality": "Mataró", "postalCode": "08301" }
        """;

    private const string Hours = """
        "openingHoursSpecification": [{ "@type": "OpeningHoursSpecification", "dayOfWeek": ["Tuesday", "Saturday"], "opens": "09:30", "closes": "19:00" }]
        """;

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new LocalBusinessCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string head, string body = "<p>Welcome</p>", HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving($"<!doctype html><html><head><title>Salon</title>{head}</head><body>{body}</body></html>", status));

    private static string JsonLd(string json) => $"<script type=\"application/ld+json\">{json}</script>";

    private static string Business(string type, params string[] properties) =>
        JsonLd($$"""{ "@context": "https://schema.org", "@type": {{type}}, "name": "Salon Test"{{string.Concat(properties.Select(p => ", " + p))}} }""");

    [Theory]
    [InlineData("\"HairSalon\"", "HairSalon")]
    [InlineData("\"LocalBusiness\"", "LocalBusiness")]
    [InlineData("\"HobbyShop\"", "HobbyShop")]
    [InlineData("[\"LocalBusiness\", \"HairSalon\"]", "HairSalon")]
    [InlineData("[\"LocalBusiness\", \"CardShop\"]", "CardShop")]          // the owner's own word, next to LocalBusiness
    [InlineData("\"https://schema.org/BeautySalon\"", "BeautySalon")]
    [InlineData("\"schema:Store\"", "Store")]
    public async Task RunAsync_WhenABusinessDeclaresItsAddressAndHours_PassesAndNamesTheKind(string type, string named)
    {
        var outcome = await RunAgainst(Business(type, Address, Hours));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains($"it is a {named},", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"address\": \"Carrer de Prova 1, 08301 Mataró\"")]                           // plain text is allowed
    [InlineData("\"address\": [{ \"@type\": \"PostalAddress\", \"addressLocality\": \"Mataró\" }]")]
    public async Task RunAsync_AcceptsTheOtherShapesOfAnAddress(string address)
    {
        var outcome = await RunAgainst(Business("\"HairSalon\"", address, Hours));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("\"openingHours\": \"Tu-Sa 09:30-19:00\"")]
    [InlineData("\"openingHours\": [\"Tu-Fr 09:30-19:00\", \"Sa 09:00-14:00\"]")]
    [InlineData("\"openingHoursSpecification\": { \"dayOfWeek\": \"Monday\", \"opens\": \"10:00\", \"closes\": \"14:00\" }")]
    public async Task RunAsync_AcceptsTheOtherShapesOfOpeningHours(string hours)
    {
        var outcome = await RunAgainst(Business("\"HairSalon\"", Address, hours));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_FindsABusinessInsideAGraph()
    {
        // What Yoast and other SEO plugins write: one block, many things.
        var outcome = await RunAgainst(JsonLd($$"""
            { "@context": "https://schema.org", "@graph": [
                { "@type": "WebSite", "name": "Salon Test" },
                { "@type": "HairSalon", "name": "Salon Test", {{Address}}, {{Hours}} }
            ] }
            """));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_FindsABusinessNestedInAnotherThing()
    {
        var outcome = await RunAgainst(JsonLd($$"""
            { "@context": "https://schema.org", "@type": "WebPage", "publisher": { "@type": "HobbyShop", "name": "Cards Test", {{Address}}, {{Hours}} } }
            """));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenOneOfSeveralShopsIsComplete_Passes()
    {
        var outcome = await RunAgainst(Business("\"HairSalon\"", Address) + Business("\"HairSalon\"", Address, Hours));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData(false, true, "not the address")]
    [InlineData(true, false, "not the opening hours")]
    [InlineData(false, false, "not the address or the opening hours")]
    public async Task RunAsync_WhenTheBusinessLeavesOutAddressOrHours_WarnsAndSaysWhich(bool address, bool hours, string said)
    {
        var properties = new List<string>();

        if (address)
        {
            properties.Add(Address);
        }

        if (hours)
        {
            properties.Add(Hours);
        }

        var outcome = await RunAgainst(Business("\"HairSalon\"", [.. properties]));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains(said, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"address\": \"\"")]
    [InlineData("\"address\": { \"@type\": \"PostalAddress\", \"addressCountry\": \"ES\" }")]   // a country is not an address
    public async Task RunAsync_DoesNotCountAnEmptyAddress(string address)
    {
        var outcome = await RunAgainst(Business("\"HairSalon\"", address, Hours));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("not the address", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<script type=\"application/ld+json\">{ \"@context\": \"https://schema.org\", \"@type\": \"Organization\", \"name\": \"Salon Test\" }</script>")]
    [InlineData("<script type=\"application/ld+json\">{ \"@context\": \"https://schema.org\", \"@type\": \"WebSite\", \"name\": \"Salon Test\" }</script>")]
    [InlineData("<!-- <script type=\"application/ld+json\">{ \"@type\": \"HairSalon\" }</script> -->")]   // commented out
    [InlineData("<script>var data = { \"@type\": \"HairSalon\" };</script>")]                                // not JSON-LD
    public async Task RunAsync_WhenThePageDoesNotSayItIsALocalBusiness_Warns(string head)
    {
        var outcome = await RunAgainst(head);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("does not tell Google it is a local business", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheOnlyBlockIsMalformed_WarnsAndSaysGoogleIgnoresIt()
    {
        var outcome = await RunAgainst(JsonLd("""{ "@type": "HairSalon", "name": "Salon Test" """));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("could not be read", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenACompleteBusinessSitsNextToAMalformedBlock_StillWarnsAboutTheBrokenOne()
    {
        var outcome = await RunAgainst(Business("\"HairSalon\"", Address, Hours) + JsonLd("{ broken"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("with its address and opening hours", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("could not be read", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ToleratesTrailingCommasAsGoogleDoes()
    {
        var outcome = await RunAgainst(JsonLd($$"""{ "@type": "HairSalon", {{Address}}, {{Hours}}, }"""));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_ReadsTheTypeAttributeWhateverItsQuotesOrCase()
    {
        var outcome = await RunAgainst(
            $$"""<SCRIPT TYPE='application/ld+json' id="schema">{ "@type": "HairSalon", {{Address}}, {{Hours}} }</SCRIPT>""");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_ReadsMicrodata()
    {
        var outcome = await RunAgainst(string.Empty, """
            <div itemscope itemtype="https://schema.org/HairSalon">
              <span itemprop="name">Salon Test</span>
              <div itemprop="address" itemscope itemtype="https://schema.org/PostalAddress"><span itemprop="streetAddress">Carrer de Prova 1</span></div>
              <meta itemprop="openingHours" content="Tu-Sa 09:30-19:00">
            </div>
            """);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("HairSalon", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenMicrodataLeavesOutTheHours_Warns()
    {
        var outcome = await RunAgainst(string.Empty, """
            <div itemscope itemtype="http://schema.org/LocalBusiness"><span itemprop="address">Carrer de Prova 1, Mataró</span></div>
            """);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("not the opening hours", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverFails()
    {
        // Visitors are not affected by any of this, so nothing here is a defect they see.
        foreach (var head in new[] { string.Empty, JsonLd("{ broken"), Business("\"HairSalon\"") })
        {
            var outcome = await RunAgainst(head);

            Assert.NotEqual(CheckStatus.Fail, outcome.Status);
        }
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
        var outcome = await RunAgainst(Business("\"HairSalon\"", Address, Hours), status: status);

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
            () => new LocalBusinessCheck(client).RunAsync(Site, cancellation.Token));
    }
}
