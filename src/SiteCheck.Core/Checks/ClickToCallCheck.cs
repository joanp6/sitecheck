using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether a visitor on a phone can call or message the business with one tap.
/// </summary>
/// <remarks>
/// For a hairdresser or a corner shop, the phone is the booking system. Most visitors arrive on a
/// phone, and a number printed as plain text has to be memorised or copied digit by digit, which
/// many people will not bother to do. A <c>tel:</c> link dials on a tap; a WhatsApp link
/// (<c>wa.me</c>, <c>api.whatsapp.com/send</c>, <c>whatsapp://send</c>) opens a chat.
/// <list type="bullet">
/// <item><see cref="CheckStatus.Pass"/>: at least one working <c>tel:</c> or WhatsApp link.</item>
/// <item><see cref="CheckStatus.Warn"/>: neither, whether or not a number is printed on the page.
/// Not a fail: nothing is broken, a visitor just has to work harder.</item>
/// <item><see cref="CheckStatus.Fail"/>: a link that is there but goes nowhere, such as
/// <c>tel:</c> with no number, or a template's <c>123456789</c> left in place. A visitor taps it
/// and nothing happens, which is worse than having no link.</item>
/// </list>
/// <para>
/// Only the page given is read, usually the home page. A number on the contact page alone is not
/// seen. Finding a printed number is a heuristic tuned for Spanish numbers (nine digits starting
/// with 6, 7, 8 or 9, with or without +34) and international ones written with a leading +; it
/// only changes the wording of a warning, never the verdict. WhatsApp share buttons, which carry
/// no number, are not contact links and are ignored.
/// </para>
/// </remarks>
public sealed partial class ClickToCallCheck : ISiteCheck
{
    private readonly HttpClient _httpClient;

    public ClickToCallCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "click-to-call";

    private enum Kind
    {
        Phone,
        WhatsApp,
    }

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page to look for a phone link", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(page.Html);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(string html)
    {
        var links = HtmlTags.Find(html, "a")
            .Select(a => WebUtility.HtmlDecode(a.GetValueOrDefault("href", string.Empty)).Trim())
            .Select(Classify)
            .OfType<ContactLink>()
            .ToArray();

        var broken = links.Where(link => !link.Works).Select(link => link.Href).Distinct(StringComparer.Ordinal).ToArray();

        if (broken.Length > 0)
        {
            return CheckOutcome.Fail(
                $"The page has a call or WhatsApp link that leads to no real number ({string.Join(", ", broken.Take(3))}). Visitors who tap it get nowhere. Put the business's number in it.");
        }

        var phone = links.Any(link => link.Kind == Kind.Phone);
        var whatsApp = links.Any(link => link.Kind == Kind.WhatsApp);

        if (phone && whatsApp)
        {
            return CheckOutcome.Pass("Visitors on a phone can call with one tap and open a WhatsApp chat.");
        }

        if (phone)
        {
            return CheckOutcome.Pass("Visitors on a phone can call with one tap. There is no WhatsApp link, which many customers prefer for bookings.");
        }

        if (whatsApp)
        {
            return CheckOutcome.Pass("Visitors on a phone can open a WhatsApp chat with one tap. There is no link to call, for those who would rather phone.");
        }

        return PrintedNumber(html) is { } number
            ? CheckOutcome.Warn(
                $"The number {number} is on the page, but as plain text: on a phone it cannot be tapped to call. Make it a link (<a href=\"tel:{Dialable(number)}\">).")
            : CheckOutcome.Warn(
                "The page has no phone link or WhatsApp link, so a visitor on a phone cannot contact the business with one tap.");
    }

    private sealed record ContactLink(Kind Kind, string Href, bool Works);

    private static ContactLink? Classify(string href)
    {
        if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            // Anything after ';' or ',' is an extension or a pause, not the number.
            var number = href[4..].Split([';', ','], 2)[0];

            return new ContactLink(Kind.Phone, href, IsRealNumber(Digits(Uri.UnescapeDataString(number), keepPlus: false), minimum: 6));
        }

        if (WhatsAppNumber(href) is { } whatsApp)
        {
            return new ContactLink(Kind.WhatsApp, href, whatsApp.Length == 0 || IsRealNumber(whatsApp, minimum: 8));
        }

        return null;
    }

    /// <summary>
    /// The digits of the number a WhatsApp contact link opens a chat with; empty for a business's
    /// short link (<c>wa.me/message/…</c>), which carries a code instead; <see langword="null"/>
    /// when the address is not a WhatsApp contact link at all.
    /// </summary>
    private static string? WhatsAppNumber(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();

        if (uri.Scheme is "http" or "https" && host is "wa.me" or "www.wa.me")
        {
            var path = uri.AbsolutePath.Trim('/');

            if (path.StartsWith("message/", StringComparison.OrdinalIgnoreCase) && path.Length > "message/".Length)
            {
                return string.Empty;
            }

            // wa.me/?text=… is a share button: it lets the visitor pick whom to send to.
            return path.Length == 0 ? null : Digits(Uri.UnescapeDataString(path), keepPlus: false);
        }

        var isSend = (uri.Scheme is "http" or "https" && host is "api.whatsapp.com" or "web.whatsapp.com" && uri.AbsolutePath.TrimEnd('/') == "/send")
                     || (uri.Scheme == "whatsapp" && host == "send");

        if (!isSend)
        {
            return null;
        }

        var phone = QueryValue(uri.Query, "phone");

        return string.IsNullOrEmpty(phone) ? null : Digits(phone, keepPlus: false);
    }

    private static string? QueryValue(string query, string key) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')))
            .FirstOrDefault();

    /// <summary>
    /// False for a number nobody can be reached on: too short, one digit repeated, or the
    /// <c>123456789</c> a template ships with.
    /// </summary>
    private static bool IsRealNumber(string digits, int minimum) =>
        digits.Length >= minimum
        && digits.Distinct().Count() > 1
        && !digits.EndsWith("123456789", StringComparison.Ordinal);

    private static string Digits(string text, bool keepPlus)
    {
        var digits = new string([.. text.Where(char.IsAsciiDigit)]);

        return keepPlus && text.TrimStart().StartsWith('+') ? "+" + digits : digits;
    }

    /// <summary>The first phone number printed in the page's visible text, as written.</summary>
    private static string? PrintedNumber(string html)
    {
        var text = WebUtility.HtmlDecode(Tag().Replace(Invisible().Replace(html, " "), " "));

        foreach (Match candidate in PhoneLike().Matches(text))
        {
            var written = candidate.Value.Trim();
            var digits = Digits(written, keepPlus: false);

            if (!IsRealNumber(digits, minimum: 9))
            {
                continue;
            }

            if (written.StartsWith('+') ? digits.Length is >= 9 and <= 15 : IsSpanish(digits))
            {
                return written;
            }
        }

        return null;
    }

    private static bool IsSpanish(string digits)
    {
        var national = National(digits);

        return national.Length == 9 && national[0] is '6' or '7' or '8' or '9';
    }

    private static string National(string digits) =>
        digits.StartsWith("0034", StringComparison.Ordinal) ? digits[4..]
        : digits.Length == 11 && digits.StartsWith("34", StringComparison.Ordinal) ? digits[2..]
        : digits;

    /// <summary>The number as a <c>tel:</c> link wants it: international, digits only.</summary>
    private static string Dialable(string written) =>
        written.StartsWith('+') ? Digits(written, keepPlus: true) : "+34" + National(Digits(written, keepPlus: false));

    // Scripts, styles and comments are not text a visitor reads.
    [GeneratedRegex(@"<script\b.*?</script\s*>|<style\b.*?</style\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Invisible();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    // Digits with the separators people write numbers with. The lookarounds keep it from starting
    // or ending inside a word, so a tax id like B12345678 or an identity number like 12345678Z is
    // not read as a phone.
    [GeneratedRegex(@"(?<![\w+])\+?\d[\d  .\-()]{7,17}\d(?!\w)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PhoneLike();
}
