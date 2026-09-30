using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class ContactFormCheckTests
{
    private static readonly Uri Site = new("https://salon.test/contact");

    private static HttpResponseMessage Page(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    /// <summary>Serves <paramref name="html"/> for the page and <paramref name="statuses"/> (default 405) for anything else.</summary>
    private static StubHttpMessageHandler HandlerFor(string html, Dictionary<string, HttpStatusCode>? statuses = null) =>
        StubHttpMessageHandler.Routing(request =>
            request.RequestUri == Site
                ? Page(html)
                : new HttpResponseMessage(statuses?.GetValueOrDefault(request.RequestUri!.AbsoluteUri, HttpStatusCode.MethodNotAllowed) ?? HttpStatusCode.MethodNotAllowed));

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new ContactFormCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> Run(string html, Dictionary<string, HttpStatusCode>? statuses = null) =>
        Run(HandlerFor(html, statuses));

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)] // the usual answer of a POST-only endpoint to a GET
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Found)]
    public async Task RunAsync_WhenTheFormSendsSomewhereThatAnswers_Passes(HttpStatusCode status)
    {
        var outcome = await Run(
            """<form method="post" action="https://forms.test/f/abc"><textarea name="m"></textarea></form>""",
            new() { ["https://forms.test/f/abc"] = status });

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("can only be tested by sending one", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverSendsTheForm()
    {
        // Submitting a stranger's contact form is spam. Only GET ever leaves this check.
        var handler = HandlerFor("""<form method="POST" action="/send"><input name="email" type="email"></form>""");

        await Run(handler);

        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request, StringComparison.Ordinal));
        Assert.Contains("GET https://salon.test/send", handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task RunAsync_WhenTheFormSendsToAnAddressThatIsGone_FailsAndSaysMessagesAreLost(HttpStatusCode status)
    {
        var outcome = await Run(
            """<form method="post" action="/old-contact.php"></form>""",
            new() { ["https://salon.test/old-contact.php"] = status });

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("https://salon.test/old-contact.php", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("lost", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheEndpointAnswersAGetWithAServerError_OnlyWarns()
    {
        // Some endpoints break on a GET and accept posts fine. Not proof of a problem.
        var outcome = await Run(
            """<form method="post" action="https://forms.test/x"></form>""",
            new() { ["https://forms.test/x"] = HttpStatusCode.InternalServerError });

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheEndpointCannotBeReached_Fails()
    {
        var handler = StubHttpMessageHandler.Routing(request => request.RequestUri == Site
            ? Page("""<form method="post" action="https://gone.test/x"></form>""")
            : throw new HttpRequestException("no such host"));

        Assert.Equal(CheckStatus.Fail, (await Run(handler)).Status);
    }

    [Fact]
    public async Task RunAsync_WhenASecurePageSendsOverHttp_Fails()
    {
        var outcome = await Run("""<form method="post" action="http://salon.test/send"></form>""");

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("plain http", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheFormReliesOnMailto_Warns()
    {
        var handler = HandlerFor("""<form action="mailto:angela@salon.test" method="get"><textarea></textarea></form>""");

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("mailto:", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // nothing to ask about a mailto: address
    }

    [Theory]
    [InlineData("""<form method="post"><textarea></textarea></form>""")]
    [InlineData("""<form method="post" action=""></form>""")]
    [InlineData("""<form method="post" action="/contact?sent=1"></form>""")]
    [InlineData("""<form method="post" action="javascript:void(0)"></form>""")]
    public async Task RunAsync_WhenTheFormSendsToItsOwnPageOrToScript_PassesWithoutAskingAgain(string html)
    {
        var handler = HandlerFor(html);

        Assert.Equal(CheckStatus.Pass, (await Run(handler)).Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""<form action="/search"><input name="q"></form>""")] // a search box sends nothing worth checking
    [InlineData("""<p>Call us.</p>""")]
    public async Task RunAsync_WhenNoFormSendsAnything_Skips(string html)
    {
        var outcome = await Run(html);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Contains("no form", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<div id="contact"><input type="email" name="e"><button>Send</button></div>""")]
    [InlineData("""<form><textarea name="m"></textarea></form>""")]
    [InlineData("""<form onsubmit="send()"><input type='email'></form>""")]
    public async Task RunAsync_WhenAFormIsSentByScript_SkipsAndSaysItCannotBeChecked(string html)
    {
        var outcome = await Run(html);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Contains("sent by script", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithSeveralForms_ReportsTheWorstAndEachProblemOnce()
    {
        var outcome = await Run(
            """
            <form method="post" action="https://ok.test/a"></form>
            <form method="post" action="/gone"></form>
            <form method="post" action="mailto:x@salon.test"></form>
            <form method="post" action="mailto:y@salon.test"></form>
            """,
            new() { ["https://salon.test/gone"] = HttpStatusCode.NotFound });

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("does not exist", outcome.Detail, StringComparison.Ordinal);
        Assert.Single(outcome.Detail.Split("mailto:", StringSplitOptions.None).Skip(1));
    }

    [Fact]
    public async Task RunAsync_WhenThePageAnswersWithAnError_Fails()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("x", HttpStatusCode.ServiceUnavailable));

        var outcome = await new ContactFormCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenThePageCannotBeFetched_Fails(bool timedOut)
    {
        Exception failure = timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("refused");
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(failure));

        Assert.Equal(CheckStatus.Fail, (await new ContactFormCheck(client).RunAsync(Site, TestContext.Current.CancellationToken)).Status);
    }
}
