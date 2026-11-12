using System.Globalization;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports cookies the site hands out without the settings that keep them from being stolen.
/// </summary>
/// <remarks>
/// <para>
/// A cookie is how a site recognises a visitor it has seen before, and for a logged-in visitor
/// it <em>is</em> the login: whoever holds the cookie is that person. Each cookie carries
/// settings that say how carefully the browser must guard it, and they are off unless the site
/// sets them:
/// </para>
/// <list type="bullet">
/// <item><c>Secure</c>: only ever send it over https. Without it, one request over plain http
/// (an old link, shared wifi) carries the cookie in the clear.</item>
/// <item><c>HttpOnly</c>: keep it away from scripts. Without it, any script on the page, the
/// site's own or one injected through a hole in it, can read the cookie and send it off.</item>
/// <item><c>SameSite=None</c> without <c>Secure</c>: a contradiction browsers settle by
/// throwing the cookie away, so whatever needed it is quietly broken.</item>
/// </list>
/// <para>
/// <c>HttpOnly</c> is only asked of cookies that look like a session or a login, by their names
/// (<c>PHPSESSID</c>, <c>wordpress_logged_in_…</c>, anything with "session", "auth" or
/// "token"). Plenty of cookies exist to be read by the page's scripts, a language choice or a
/// consent banner's answer, and demanding it of them would be wrong.
/// </para>
/// <para>
/// A missing <c>SameSite</c> is not held against a cookie: browsers now treat that as
/// <c>Lax</c>, which is the safe default.
/// </para>
/// <para>
/// It sees only the cookies the server sets with the page itself. The ones scripts create
/// afterwards (analytics, most consent banners) never appear in the headers, and the ones set
/// after logging in are behind a login sitecheck never attempts. Never a
/// <see cref="CheckStatus.Fail"/>: a loose cookie is a door left unlocked, not a break-in.
/// </para>
/// </remarks>
public sealed partial class CookiesCheck : ISiteCheck
{
    private const int MaxListed = 4;

    private readonly HttpClient _httpClient;

    public CookiesCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "cookies";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (fetched, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the cookies it sets", cancellationToken)
            .ConfigureAwait(false);

        if (fetched is null)
        {
            return unread!;
        }

        if (fetched.Address.Scheme != Uri.UriSchemeHttps)
        {
            return CheckOutcome.Skip(
                "The page is served over plain http, where no cookie can be kept safe. https-redirect reports that.");
        }

        Cookie[] cookies = fetched.Headers.TryGetValues("Set-Cookie", out var lines)
            ? [.. lines.Select(Parse).OfType<Cookie>()]
            : [];

        try
        {
            return Evaluate(cookies);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The site's cookies are too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(Cookie[] cookies)
    {
        if (cookies.Length == 0)
        {
            return CheckOutcome.Pass(
                "The page sets no cookies when it is first opened. Cookies that scripts add afterwards are not seen here.");
        }

        var problems = cookies.SelectMany(Problems).ToArray();
        var count = cookies.Length.ToString(CultureInfo.InvariantCulture);

        if (problems.Length == 0)
        {
            return CheckOutcome.Pass($"The {count} cookie(s) the page sets are sent only over https, and the login ones are kept from scripts.");
        }

        var listed = string.Join("; ", problems.Take(MaxListed));
        var more = problems.Length > MaxListed
            ? $"; and {(problems.Length - MaxListed).ToString(CultureInfo.InvariantCulture)} more"
            : string.Empty;

        return CheckOutcome.Warn(
            $"Of the {count} cookie(s) the page sets, some are not guarded as they should be: {listed}{more}. "
            + "These are settings where the site creates the cookie; ask whoever built or hosts it to add them.");
    }

    private static IEnumerable<string> Problems(Cookie cookie)
    {
        if (cookie.SameSiteNone && !cookie.Secure)
        {
            yield return $"\"{cookie.Name}\" is marked SameSite=None without Secure, so browsers throw it away";
        }
        else if (!cookie.Secure)
        {
            yield return $"\"{cookie.Name}\" is not marked Secure, so it can travel over plain http where anyone on the same wifi can read it";
        }

        // Anti-forgery tokens have "token" in the name and are meant to be read by the page.
        var antiForgery = cookie.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase) || cookie.Name.Contains("xsrf", StringComparison.OrdinalIgnoreCase);

        if (!cookie.HttpOnly && !antiForgery && SessionName().IsMatch(cookie.Name))
        {
            yield return $"\"{cookie.Name}\" looks like a login and is not marked HttpOnly, so a script on the page can read it and send it elsewhere";
        }
    }

    /// <summary>One <c>Set-Cookie</c> line: <c>name=value; Attribute; Attribute=value</c>.</summary>
    private static Cookie? Parse(string line)
    {
        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        var equals = parts[0].IndexOf('=', StringComparison.Ordinal);

        if (equals <= 0)
        {
            return null;
        }

        var name = parts[0][..equals].Trim();
        bool secure = false, httpOnly = false, sameSiteNone = false;

        foreach (var attribute in parts.Skip(1))
        {
            if (attribute.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            {
                secure = true;
            }
            else if (attribute.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase))
            {
                httpOnly = true;
            }
            else if (attribute.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("SameSite=None", StringComparison.OrdinalIgnoreCase))
            {
                sameSiteNone = true;
            }
        }

        // The value is deliberately not kept: it may be someone's session, and nothing here needs it.
        // The name is the site's text, so it is cut to a length fit for a report.
        return new Cookie(name.Length <= 40 ? name : name[..40] + "…", secure, httpOnly, sameSiteNone);
    }

    /// <summary>Names that mark a cookie as holding a login rather than a preference.</summary>
    [GeneratedRegex(
        @"sess|sid$|auth|token|login|logged_in|^jwt|^connect\.sid$|\.AspNetCore\.|^ASPXAUTH",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SessionName();

    private sealed record Cookie(string Name, bool Secure, bool HttpOnly, bool SameSiteNone);
}
