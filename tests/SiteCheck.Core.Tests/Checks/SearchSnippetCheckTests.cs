using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class SearchSnippetCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string GoodTitle = "Hair salon in Mataró | Salon Test";
    private const string GoodDescription = "Cuts, colour and styling in the centre of Mataró. Book by phone or WhatsApp, open Tuesday to Saturday.";

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new SearchSnippetCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string head, string body = "<p>Welcome</p>", HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving($"<!doctype html><html><head>{head}</head><body>{body}</body></html>", status));

    private static string Head(string? title = GoodTitle, string? description = GoodDescription) =>
        (title is null ? string.Empty : $"<title>{title}</title>")
        + (description is null ? string.Empty : $"<meta name=\"description\" content=\"{description}\">");

    [Fact]
    public async Task RunAsync_WhenTitleAndDescriptionAreTheBusinessesOwn_PassesAndQuotesTheTitle()
    {
        var outcome = await RunAgainst(Head());

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains(GoodTitle, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<title>\n   Hair salon in Mataró\n   | Salon Test </title>")]                  // whitespace collapses
    [InlineData("<TITLE lang=\"en\">Hair salon in Mataró | Salon Test</TITLE>")]
    [InlineData("<title>Hair salon in Matar&oacute; | Salon Test</title>")]                   // entities decode
    public async Task RunAsync_ReadsTheTitleAsABrowserShowsIt(string title)
    {
        var outcome = await RunAgainst(title + $"<meta name=\"description\" content=\"{GoodDescription}\">");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("\"Hair salon in Mataró | Salon Test\"", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<title></title>")]
    [InlineData("<title>   </title>")]
    [InlineData("<!-- <title>Old salon</title> -->")]                                         // commented out
    [InlineData("<script>document.write('<title>Salon</title>')</script>")]                 // only inside a script
    public async Task RunAsync_WhenThereIsNoTitle_Fails(string head)
    {
        var outcome = await RunAgainst(head + $"<meta name=\"description\" content=\"{GoodDescription}\">");

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("no title", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotTakeAnIconsTitleInTheBodyForThePages()
    {
        var outcome = await RunAgainst(
            $"<meta name=\"description\" content=\"{GoodDescription}\">",
            "<svg><title>Phone icon</title></svg>");

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("Inicio")]
    [InlineData("inici")]
    [InlineData("Document")]                // what an editor's HTML template starts with
    [InlineData("Untitled Document")]
    [InlineData("Welcome!")]
    [InlineData("My WordPress Blog")]
    public async Task RunAsync_WhenTheTitleIsATemplates_WarnsAndQuotesIt(string title)
    {
        var outcome = await RunAgainst(Head(title: title));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains($"\"{title}\", which a template left behind", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Home | Salon Test")]       // a generic word next to the name is fine
    [InlineData("Salon Test")]
    public async Task RunAsync_WhenTheTitleOnlyContainsAGenericWord_DoesNotCallItATemplates(string title)
    {
        var outcome = await RunAgainst(Head(title: title));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheTitleIsTooLong_WarnsAndShowsWhereGoogleCutsIt()
    {
        var title = "Salon Test, the best hair salon in Mataró for cuts, colour, styling and much more";

        var outcome = await RunAgainst(Head(title: title));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains($"{title.Length} characters", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains(title[..SearchSnippetCheck.TitleMax].TrimEnd() + "…", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheTitleIsExactlyAtTheLimit_Passes()
    {
        var outcome = await RunAgainst(Head(title: new string('a', SearchSnippetCheck.TitleMax - 1) + "b"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_WhenThereIsNoDescription_WarnsAndSaysWhatToAdd(string? description)
    {
        var outcome = await RunAgainst(Head(description: description));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no description", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"description\">", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReadsTheDescriptionWhateverTheAttributeOrderOrCase()
    {
        var outcome = await RunAgainst($"<title>{GoodTitle}</title><META CONTENT=\"{GoodDescription}\" NAME=\"Description\">");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("Just another WordPress site")]
    [InlineData("Otro sitio realizado con WordPress")]
    [InlineData("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor.")]
    public async Task RunAsync_WhenTheDescriptionIsATemplates_Warns(string description)
    {
        var outcome = await RunAgainst(Head(description: description));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("which a template left behind", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheDescriptionIsTooShort_Warns()
    {
        var outcome = await RunAgainst(Head(description: "Hair salon"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("too short", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheDescriptionIsTooLong_Warns()
    {
        var outcome = await RunAgainst(Head(description: string.Concat(Enumerable.Repeat(GoodDescription + " ", 3))));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("cuts it after about 160", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenBothHaveProblems_ReportsBothInOneWarning()
    {
        var outcome = await RunAgainst(Head(title: "Home", description: null));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("\"Home\"", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("no description", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        // The block page has no title of the business, but people in a browser never see it.
        var outcome = await RunAgainst("<title>Just a moment...</title>", status: status);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        var outcome = await RunAgainst(Head(), status: status);

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
            () => new SearchSnippetCheck(client).RunAsync(Site, cancellation.Token));
    }
}
