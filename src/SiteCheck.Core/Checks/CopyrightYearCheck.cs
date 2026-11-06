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

        var years = Mark().Matches(text)
            .SelectMany(mark => YearsOf(text, mark))
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

    /// <summary>
    /// The year a copyright mark stands for: the one after it, or only when there is none, the
    /// one right before it.
    /// </summary>
    /// <remarks>
    /// The year after the mark wins because tags are removed before reading. "Established 2010"
    /// in one paragraph and "© 2026 Salon Test" in the footer become "Established 2010 © 2026
    /// Salon Test", and taking 2010 as the notice would call a current site abandoned.
    /// </remarks>
    private static IEnumerable<Group> YearsOf(string text, Match mark)
    {
        var after = YearAfterMark().Match(text, mark.Index + mark.Length);

        if (after.Success)
        {
            yield return after.Groups["from"];

            if (after.Groups["to"].Success)
            {
                yield return after.Groups["to"];
            }

            yield break;
        }

        var before = YearBeforeMark().Match(text[..mark.Index]);

        if (before.Success)
        {
            yield return before.Groups["year"];
        }
    }

    [GeneratedRegex(@"©|\(c\)|\bcopyright\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Mark();

    // "© 2019", "(c) 2015-2025", "Copyright © Salon Test 2019". Up to 60 characters of name may sit
    // between the mark and the year, but no other digits, so a phone number or a street number
    // next to the notice is not read as part of it. \G keeps it starting at the mark.
    [GeneratedRegex(
        @"\G[^\d]{0,60}?(?<!\d)(?<from>(?:19|20)\d{2})(?!\d)(?:\s*[-–—/]\s*(?<to>(?:19|20)\d{2})(?!\d))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex YearAfterMark();

    // "2019 © Salon Test": a year with nothing but spaces between it and the mark.
    [GeneratedRegex(@"(?<!\d)(?<year>(?:19|20)\d{2})\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex YearBeforeMark();
}
