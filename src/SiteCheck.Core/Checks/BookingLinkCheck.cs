using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether a visitor can book online or find the shop on a map from the page.
/// </summary>
/// <remarks>
/// After "can I call them?" (<c>click-to-call</c>), the next two things a visitor to a salon or a
/// shop wants are "can I book without calling?" and "where is it?". This looks for:
/// <list type="bullet">
/// <item>Online booking: a link to, or a widget from, a booking service (Booksy, Treatwell,
/// Fresha, Calendly…), or a link on the site's own pages that reads like one ("Reservar cita",
/// "Pide cita", "Demana cita", "Book now").</item>
/// <item>A map: a link to, or an embedded, Google Maps, Apple Maps, OpenStreetMap or Waze, or the
/// business's Google profile (<c>g.page</c>).</item>
/// </list>
/// <para>
/// Either one is a <see cref="CheckStatus.Pass"/>, which says what the other would add: a card
/// shop has no appointments to book, and a salon that only takes calls is the owner's choice. Only
/// neither is a <see cref="CheckStatus.Warn"/>. Never a <see cref="CheckStatus.Fail"/>: nothing is
/// broken. Links are recognised, not followed, so a booking profile that has since been closed is
/// not caught; <c>broken-links</c> follows the page's links.
/// </para>
/// </remarks>
public sealed partial class BookingLinkCheck : ISiteCheck
{
    // Hosts of booking services, matched on the host or a parent of it (es.treatwell.com).
    private static readonly (string Host, string Name)[] BookingServices =
    [
        ("booksy.com", "Booksy"),
        ("treatwell.es", "Treatwell"),
        ("treatwell.com", "Treatwell"),
        ("treatwell.co.uk", "Treatwell"),
        ("fresha.com", "Fresha"),
        ("timify.com", "Timify"),
        ("reservio.com", "Reservio"),
        ("calendly.com", "Calendly"),
        ("setmore.com", "Setmore"),
        ("simplybook.me", "SimplyBook.me"),
        ("simplybook.it", "SimplyBook.me"),
        ("acuityscheduling.com", "Acuity"),
        ("as.me", "Acuity"),
        ("koibox.cloud", "Koibox"),
        ("agendapro.com", "AgendaPro"),
        ("bewe.io", "Bewe"),
        ("salonized.com", "Salonized"),
        ("bookitit.com", "Bookitit"),
        ("calendar.app.google", "Google Calendar"),
        ("youcanbook.me", "YouCanBookMe"),
    ];

    // Words on a link of the site's own that offer booking, after Anchors.Normalise.
    private static readonly string[] BookingWords =
    [
        "reservar", "reserva online", "reserva tu cita", "reserva cita", "reserva ara", "reserva ya",
        "pide cita", "pedir cita", "pide tu cita", "cita previa", "cita online", "demana cita", "demanar cita",
        "book now", "book online", "book an appointment", "booking",
    ];

    private readonly HttpClient _httpClient;

    public BookingLinkCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "booking-link";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page to look for booking and map links", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(page.Html, page.Address);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(string html, Uri page)
    {
        var anchors = Anchors.Find(html).ToArray();

        // Widgets and embeds come as frames and scripts rather than links.
        var embedded = HtmlTags.Find(Comment().Replace(html, " "), "iframe")
            .Concat(HtmlTags.Find(Comment().Replace(html, " "), "script"))
            .Select(tag => tag.GetValueOrDefault("src"))
            .OfType<string>();

        var addresses = anchors.Select(anchor => anchor.Href).Concat(embedded)
            .Select(href => Uri.TryCreate(page, href, out var uri) ? uri : null)
            .OfType<Uri>()
            .Where(uri => uri.Scheme is "http" or "https")
            .ToArray();

        var service = addresses.Select(BookingService).FirstOrDefault(name => name is not null);

        var ownBooking = service is null
            ? anchors.FirstOrDefault(anchor => BookingWords.Any(word => ContainsWords(Anchors.Normalise(anchor.Text), word)))
            : null;

        var booking = service is not null
            ? $"book online through {service}"
            : ownBooking is not null ? $"book online (\"{Shorten(ownBooking.Text)}\")" : null;

        var map = addresses.Any(IsMap);

        return (booking, map) switch
        {
            (not null, true) => CheckOutcome.Pass($"Visitors can {booking} and find the shop on a map."),
            (not null, false) => CheckOutcome.Pass(
                $"Visitors can {booking}. There is no map link: a Google Maps link to the shop helps people find it."),
            (null, true) => CheckOutcome.Pass(
                "Visitors can find the shop on a map. There is no online booking; if the business takes appointments, a booking link lets customers book outside opening hours."),
            _ => CheckOutcome.Warn(
                "The page has no link to book online and no map to the shop, so a visitor has to call to book and search for the address. A Google Maps link, and a booking link if the business takes appointments, save them both."),
        };
    }

    private static string? BookingService(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        foreach (var (serviceHost, name) in BookingServices)
        {
            if (host == serviceHost || host.EndsWith("." + serviceHost, StringComparison.Ordinal))
            {
                return name;
            }
        }

        // Square's appointments live under its own domain's path.
        return host is "squareup.com" or "app.squareup.com" && uri.AbsolutePath.StartsWith("/appointments", StringComparison.OrdinalIgnoreCase)
            ? "Square Appointments"
            : null;
    }

    private static bool IsMap(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;

        return host switch
        {
            "maps.app.goo.gl" or "maps.apple.com" or "g.page" or "waze.com" or "www.waze.com" or "www.openstreetmap.org" or "openstreetmap.org" => true,
            "goo.gl" => path.StartsWith("/maps", StringComparison.OrdinalIgnoreCase),
            _ when host.StartsWith("maps.google.", StringComparison.Ordinal) => true,
            _ when host is "google.com" || host.StartsWith("www.google.", StringComparison.Ordinal) || host.StartsWith("google.", StringComparison.Ordinal)
                => path.StartsWith("/maps", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // A whole-word match, so "booking" does not match inside "bookings-archive" by accident and
    // "reservar" is found in "reservar cita" but not in "reservaron".
    // Punctuation counts as a space, so "¡Reserva ya!" reads as the words "reserva ya".
    private static bool ContainsWords(string text, string words) =>
        $" {NotAWord().Replace(text, " ")} ".Contains($" {words} ", StringComparison.Ordinal);

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotAWord();

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40].TrimEnd() + "…";

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Comment();
}
