using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the copyright year on the page makes the site look abandoned.
/// </summary>
/// <remarks>
/// "© 2017" in the footer tells a visitor nobody has touched the site in years, and makes them
/// wonder whether the shop is still open, the prices still valid, the phone still answered. It is
/// a weak signal: a site can be well kept and simply never have had its footer updated. That is
/// why it is never more than a <see cref="CheckStatus.Warn"/>, and only once the year is
/// <see cref="StaleAfterYears"/> or more behind.
/// <para>
/// The year is read from the page's visible text next to "©", "(c)" or "Copyright". With a range
/// ("© 2015–2025") the later year counts, and with several notices the latest one. A footer that
/// writes the year by script has none in the HTML, and neither has a page without a notice: both
/// are a <see cref="CheckStatus.Skip"/>, since there is nothing to judge.
/// </para>
/// </remarks>
public sealed partial class CopyrightYearCheck : ISiteCheck
{
    public const int StaleAfterYears = 3;

    private const int EarliestYear = 1990;

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public CopyrightYearCheck(HttpClient httpClient, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    public string Name => "copyright-year";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page's copyright notice", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(page.Html, _timeProvider.GetUtcNow().Year);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(string html, int thisYear)
    {
        var text = WebUtility.HtmlDecode(Tag().Replace(Invisible().Replace(html, " "), " "));

        var years = Notice().Matches(text)
            .SelectMany(match => new[] { match.Groups["before"], match.Groups["from"], match.Groups["to"] })
            .Where(group => group.Success)
            .Select(group => int.Parse(group.Value, CultureInfo.InvariantCulture))
            .Where(year => year >= EarliestYear && year <= thisYear + 1)
            .ToArray();

        if (years.Length == 0)
        {
            return CheckOutcome.Skip(
                "The page shows no copyright year, or writes it by script, so there is nothing to judge.");
        }

        var latest = years.Max();
        var behind = thisYear - latest;

        if (behind < StaleAfterYears)
        {
            return CheckOutcome.Pass($"The page's copyright notice says {latest}, which looks current.");
        }

        return CheckOutcome.Warn(
            $"The page's copyright notice says {latest}, {behind} years ago, which makes the site look abandoned even if the business is open. Update the year, or have the site write the current one by itself.");
    }

    // Scripts, styles and comments are not text a visitor reads.
    [GeneratedRegex(@"<script\b.*?</script\s*>|<style\b.*?</style\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Invisible();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    // "© 2019", "(c) 2015-2025", "Copyright © Salon Test 2019", and "2019 © Salon Test". Up to 60
    // characters of name may sit between the mark and the year, but no other digits, so a phone
    // number or a street number next to the notice is not read as part of it.
    [GeneratedRegex(
        @"(?:(?<!\d)(?<before>(?:19|20)\d{2})\s*(?:©|\(c\)))|(?:(?:©|\(c\)|\bcopyright\b)[^\d]{0,60}?(?<!\d)(?<from>(?:19|20)\d{2})(?!\d)(?:\s*[-–—/]\s*(?<to>(?:19|20)\d{2})(?!\d))?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Notice();
}
