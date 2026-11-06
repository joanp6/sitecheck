using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class BookingLinkCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string Map = """<a href="https://maps.app.goo.gl/AbCdEf123">How to get here</a>""";
    private const string Booksy = """<a href="https://booksy.com/es-es/12345_salon-test_peluqueria_mataro">Book</a>""";

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new BookingLinkCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving($"<!doctype html><html><head><title>Salon</title></head><body>{body}</body></html>", status));

    [Fact]
    public async Task RunAsync_WhenThereIsABookingServiceAndAMap_PassesAndNamesTheService()
    {
        var outcome = await RunAgainst(Booksy + Map);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Equal("Visitors can book online through Booksy and find the shop on a map.", outcome.Detail);
    }

    [Theory]
    [InlineData("""<a href="https://www.treatwell.es/establecimiento/salon-test/">Treatwell</a>""", "Treatwell")]
    [InlineData("""<a href="https://www.fresha.com/a/salon-test-mataro-abc">Book</a>""", "Fresha")]
    [InlineData("""<a href="https://calendly.com/salon-test/cita">Cita</a>""", "Calendly")]
    [InlineData("""<a href="https://salontest.simplybook.me/v2/">Book</a>""", "SimplyBook.me")]             // a subdomain of the service
    [InlineData("""<a href="https://squareup.com/appointments/book/abc/salon-test">Book</a>""", "Square Appointments")]
    [InlineData("""<iframe src="https://www.timify.com/es-es/widget/?accountId=abc"></iframe>""", "Timify")]   // an embedded widget
    [InlineData("""<script src="https://booksy.com/widget/code.js?id=12345"></script>""", "Booksy")]
    public async Task RunAsync_RecognisesBookingServices(string body, string service)
    {
        var outcome = await RunAgainst(body + Map);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains($"book online through {service}", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<a href="/reservas">Reservar cita</a>""", "Reservar cita")]
    [InlineData("""<a href="/cita">¡Pide cita!</a>""", "¡Pide cita!")]
    [InlineData("""<a href="/cita">Demana cita</a>""", "Demana cita")]
    [InlineData("""<a href="/book" class="btn">BOOK NOW</a>""", "BOOK NOW")]
    [InlineData("""<a href="#" title="Reserva online"><svg></svg></a>""", "Reserva online")]
    public async Task RunAsync_RecognisesTheSitesOwnBookingLinks(string body, string quoted)
    {
        var outcome = await RunAgainst(body + Map);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains($"book online (\"{quoted}\")", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<a href="https://www.google.com/maps/place/Salon+Test/@41.54,2.44,17z">Map</a>""")]
    [InlineData("""<a href="https://maps.google.es/?q=Salon+Test">Map</a>""")]
    [InlineData("""<a href="https://goo.gl/maps/AbC123">Map</a>""")]
    [InlineData("""<a href="https://g.page/salon-test">Google</a>""")]
    [InlineData("""<a href="https://maps.apple.com/?q=Salon+Test">Map</a>""")]
    [InlineData("""<a href="https://www.waze.com/ul?ll=41.54,2.44">Waze</a>""")]
    [InlineData("""<iframe src="https://www.google.com/maps/embed?pb=abc"></iframe>""")]
    [InlineData("""<a href="https://www.openstreetmap.org/#map=18/41.54/2.44">Map</a>""")]
    public async Task RunAsync_RecognisesMaps(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("find the shop on a map", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithBookingButNoMap_PassesAndSuggestsOne()
    {
        var outcome = await RunAgainst(Booksy);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("There is no map link", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithAMapButNoBooking_PassesSinceNotEveryShopTakesAppointments()
    {
        var outcome = await RunAgainst(Map);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("if the business takes appointments", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""<a href="/contacto">Contacto</a><a href="tel:+34600111222">Call</a>""")]
    [InlineData("""<p>Reservar cita en el 600 111 222</p>""")]                                           // the words, but no link
    [InlineData("""<!-- <a href="https://booksy.com/es-es/1">Book</a> <a href="https://maps.app.goo.gl/x">Map</a> -->""")]
    [InlineData("""<a href="https://www.google.com/search?q=salon">Google</a>""")]                       // Google, not its maps
    [InlineData("""<a href="https://notbooksy.com.test/">Partner</a><a href="/archivo">Las reservaron todas</a>""")]
    public async Task RunAsync_WhenThereIsNeither_Warns(string body)
    {
        var outcome = await RunAgainst(body);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("no link to book online and no map", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverFails()
    {
        var outcome = await RunAgainst(string.Empty);

        Assert.NotEqual(CheckStatus.Fail, outcome.Status);
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
        var outcome = await RunAgainst(Booksy + Map, status);

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
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesInsteadOfReportingAnError()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new BookingLinkCheck(client).RunAsync(Site, cancellation.Token));
    }
}
