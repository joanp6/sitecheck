using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class ClickToCallCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new ClickToCallCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving($"<html><head><title>Salon</title></head><body>{body}</body></html>", status));

    [Theory]
    [InlineData("""<a href="tel:+34600111222">Call us</a>""")]
    [InlineData("""<a href="tel:937 11 22 33">937 11 22 33</a>""")]
    [InlineData("""<a href="TEL:+34-600-111-222">Call</a>""")]
    [InlineData("""<a href=" tel:+34600111222 ">Call</a>""")]
    [InlineData("""<a href="tel:+34937112233;ext=12">Call</a>""")]
    [InlineData("""<a href="tel:%2B34600111222">Call</a>""")]
    public async Task RunAsync_WhenThereIsACallLink_Passes(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("call with one tap", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<a href="https://wa.me/34600111222">WhatsApp</a>""")]
    [InlineData("""<a href="https://wa.me/+34600111222?text=Hola">WhatsApp</a>""")]
    [InlineData("""<a href="https://wa.me/message/ABCDEF123">WhatsApp</a>""")]
    [InlineData("""<a href="https://api.whatsapp.com/send?phone=34600111222&amp;text=Hola">WhatsApp</a>""")]
    [InlineData("""<a href="https://api.whatsapp.com/send/?phone=%2B34600111222">WhatsApp</a>""")]
    [InlineData("""<a href="whatsapp://send?phone=34600111222">WhatsApp</a>""")]
    public async Task RunAsync_WhenThereIsAWhatsAppLink_Passes(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("WhatsApp chat with one tap", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThereAreBoth_PassesAndSaysSo()
    {
        var outcome = await RunAgainst("""<a href="tel:+34600111222">Call</a> <a href="https://wa.me/34600111222">WhatsApp</a>""");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("call with one tap and open a WhatsApp chat", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<a href="tel:">Call</a>""")]
    [InlineData("""<a href="tel:123">Call</a>""")]
    [InlineData("""<a href="tel:+34123456789">Call</a>""")]       // the template's number
    [InlineData("""<a href="tel:000000000">Call</a>""")]
    [InlineData("""<a href="https://wa.me/34">WhatsApp</a>""")]
    [InlineData("""<a href="https://api.whatsapp.com/send?phone=111111111">WhatsApp</a>""")]
    public async Task RunAsync_WhenALinkLeadsToNoRealNumber_Fails(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("no real number", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenOneLinkWorksAndAnotherIsBroken_StillFails()
    {
        // The visitor who taps the broken one gets nowhere, whichever one works.
        var outcome = await RunAgainst("""<a href="tel:+34600111222">Call</a> <a href="tel:123">Call the shop</a>""");

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("tel:123", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Call us on 937 11 22 33", "937 11 22 33", "+34937112233")]
    [InlineData("Tel: 600-111-222", "600-111-222", "+34600111222")]
    [InlineData("Tel.: +34 600 11 12 22", "+34 600 11 12 22", "+34600111222")]
    [InlineData("Ring +44 20 7946 0958", "+44 20 7946 0958", "+442079460958")]  // international, written with +
    [InlineData("Llámanos al <strong>93 711 22 33</strong>", "93 711 22 33", "+34937112233")]
    public async Task RunAsync_WhenANumberIsPrintedButNotALink_WarnsAndShowsTheLinkToAdd(string body, string number, string dialable)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Warn, outcome.Status);

        if (number.Length > 0)
        {
            Assert.Contains($"The number {number} is on the page", outcome.Detail, StringComparison.Ordinal);
            Assert.Contains($"tel:{dialable}", outcome.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("<p>Open since 2008. Prices from 12.50 €.</p>")]
    [InlineData("<p>CIF B12345678 · DNI 12345678Z</p>")]                                // ids are not phones
    [InlineData("<p>08301 Mataró</p>")]                                                  // a postcode
    [InlineData("<p>IBAN ES91 2100 0418 4502 0005 1332</p>")]
    [InlineData("<script>var phone = '600111222';</script><p>Welcome</p>")]             // not visible text
    [InlineData("<!-- 600 111 222 --><p>Welcome</p>")]
    [InlineData("""<a href="https://wa.me/?text=Look%20at%20this">Share on WhatsApp</a>""")] // a share button, not a contact
    [InlineData("""<a href="https://api.whatsapp.com/send?text=Hola">Share</a>""")]
    public async Task RunAsync_WhenThereIsNoNumberAtAll_WarnsWithoutInventingOne(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no phone link or WhatsApp link", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        var outcome = await RunAgainst("Access denied", status);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        var outcome = await RunAgainst("""<a href="tel:+34600111222">Call</a>""", status);

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
            () => new ClickToCallCheck(client).RunAsync(Site, cancellation.Token));
    }
}
