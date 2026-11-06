using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the page links to the legal notice, privacy policy and cookie policy that a
/// business website in Spain is expected to have.
/// </summary>
/// <remarks>
/// Spanish law asks a business website to say who runs it (the <i>aviso legal</i> of the LSSI,
/// article 10), how it handles personal data (the privacy policy of the GDPR) and, when it uses
/// cookies that are not strictly necessary, what they are for (LSSI, article 22). They are usually
/// linked from the footer of every page.
/// <para>
/// This only looks for links, in Spanish, Catalan and English, by their words and addresses. It
/// cannot read the pages, and it is not legal advice: a link proves nothing about what is behind
/// it, and a missing one may be added later by script. That is why it never goes beyond a
/// <see cref="CheckStatus.Warn"/>, and why its sentences say "could not find", never "breaks the
/// law".
/// </para>
/// <para>
/// A cookie policy is only asked for when the page shows it needs one: it loads something known to
/// set cookies (analytics, advertising pixels, embedded YouTube videos or Google Maps). A consent
/// tool (Cookiebot, CookieYes, OneTrust, Complianz, iubenda…) counts as the cookie policy, since
/// those show it from their banner.
/// </para>
/// </remarks>
public sealed partial class LegalPagesCheck : ISiteCheck
{
    private static readonly string[] LegalWords =
    [
        "aviso legal", "avis legal", "nota legal", "informacion legal", "informacio legal", "legal notice",
        "imprint", "impressum", "terminos legales", "condiciones legales",
    ];

    private static readonly string[] PrivacyWords =
    [
        "privacidad", "privacitat", "privacy", "proteccion de datos", "proteccio de dades", "rgpd", "gdpr",
    ];

    private static readonly string[] CookieWords = ["cookie", "galeta", "galetes"];

    // Substrings of the address of things that set cookies a visitor must be told about.
    private static readonly (string Marker, string Name)[] CookieSetters =
    [
        ("googletagmanager.com", "Google Tag Manager"),
        ("google-analytics.com", "Google Analytics"),
        ("connect.facebook.net", "the Facebook pixel"),
        ("static.hotjar.com", "Hotjar"),
        ("youtube.com/embed", "an embedded YouTube video"),
        ("google.com/maps/embed", "an embedded Google map"),
        ("maps.google.", "an embedded Google map"),
        ("tiktok.com/i18n/pixel", "the TikTok pixel"),
    ];

    private static readonly string[] ConsentTools =
    [
        "cookiebot.com", "cookieyes.com", "cookielaw.org", "onetrust", "complianz", "iubenda.com",
        "cookie-law-info", "cookie-notice", "usercentrics", "termly.io", "cookiehub", "cookie-script.com",
    ];

    private readonly HttpClient _httpClient;

    public LegalPagesCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "legal-pages";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page to look for its legal links", cancellationToken)
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
        var visible = Comment().Replace(html, " ");
        var links = Links(visible).ToArray();

        var legal = links.Any(link => Mentions(link, LegalWords) || IsBareLegal(link));
        var privacy = links.Any(link => Mentions(link, PrivacyWords));
        var cookieLink = links.Any(link => Mentions(link, CookieWords));

        var sources = HtmlTags.All(visible)
            .SelectMany(tag => new[] { tag.GetValueOrDefault("src"), tag.GetValueOrDefault("href") })
            .OfType<string>()
            .ToArray();

        var consentTool = sources.Any(source => ConsentTools.Any(tool => source.Contains(tool, StringComparison.OrdinalIgnoreCase)));
        var setters = CookieSetters
            .Where(setter => sources.Any(source => source.Contains(setter.Marker, StringComparison.OrdinalIgnoreCase)))
            .Select(setter => setter.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var cookies = cookieLink || consentTool;
        var cookiesNeeded = setters.Length > 0;

        var missing = new List<string>();

        if (!legal)
        {
            missing.Add("a legal notice (aviso legal: who runs the site)");
        }

        if (!privacy)
        {
            missing.Add("a privacy policy (how personal data is handled)");
        }

        if (!cookies && cookiesNeeded)
        {
            missing.Add($"a cookie policy, which the page needs because it loads {Join(setters, "and")}");
        }

        if (missing.Count == 0)
        {
            var found = cookies ? "a legal notice, a privacy policy and a cookie policy" : "a legal notice and a privacy policy";
            var note = cookies || cookiesNeeded ? string.Empty : " It loads nothing known to set cookies, so no cookie policy was looked for.";

            return CheckOutcome.Pass($"The page links to {found}.{note} sitecheck only sees the links, not what the pages say.");
        }

        return CheckOutcome.Warn(
            $"sitecheck could not find a link to {Join([.. missing], "or")} on this page. Spanish law (LSSI and GDPR) expects a business website to have them, usually linked from the footer. They may exist where sitecheck cannot see, such as a menu built by script; this is not legal advice.");
    }

    private sealed record Link(string Text, string Href);

    private static IEnumerable<Link> Links(string html) =>
        Anchors.Find(html).Select(anchor => new Link(Anchors.Normalise(anchor.Text), Anchors.Normalise(PathOf(anchor.Href))));

    // The address without its host, so a domain such as privacy-first.test does not count.
    private static string PathOf(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.PathAndQuery
            : href;

    private static bool Mentions(Link link, string[] words) =>
        words.Any(word => link.Text.Contains(word, StringComparison.Ordinal) || link.Href.Contains(word, StringComparison.Ordinal));

    // "Legal" on its own, or an address such as /legal or /legal/, is the legal notice.
    private static bool IsBareLegal(Link link) =>
        link.Text.Trim() == "legal" || LegalSegment().IsMatch(link.Href);

    private static string Join(string[] items, string conjunction) => items.Length switch
    {
        1 => items[0],
        2 => $"{items[0]} {conjunction} {items[1]}",
        _ => string.Join(", ", items[..^1]) + $", {conjunction} " + items[^1],
    };

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Comment();

    [GeneratedRegex(@"(^|/)legal(/|\s|\?|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LegalSegment();
}
