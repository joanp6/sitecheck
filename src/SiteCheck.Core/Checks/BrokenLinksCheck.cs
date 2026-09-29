using System.Globalization;
using System.Net;

namespace SiteCheck.Checks;

/// <summary>
/// Tuning for <see cref="BrokenLinksCheck"/>.
/// </summary>
/// <param name="MaxLinks">
/// How many links are followed. A page with hundreds of links would otherwise turn one audit into
/// a crawl of other people's servers.
/// </param>
/// <param name="MaxConcurrency">How many links are in flight at once.</param>
/// <param name="LinkTimeout">How long one link gets, in total, before it counts as not answering.</param>
public sealed record BrokenLinksCheckOptions(int MaxLinks, int MaxConcurrency, TimeSpan LinkTimeout)
{
    public static BrokenLinksCheckOptions Default { get; } = new(MaxLinks: 50, MaxConcurrency: 5, LinkTimeout: TimeSpan.FromSeconds(10));

    public int MaxLinks { get; } = MaxLinks > 0
        ? MaxLinks
        : throw new ArgumentOutOfRangeException(nameof(MaxLinks), MaxLinks, "At least one link has to be checked.");

    public int MaxConcurrency { get; } = MaxConcurrency > 0
        ? MaxConcurrency
        : throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), MaxConcurrency, "At least one link has to be checked at a time.");

    public TimeSpan LinkTimeout { get; } = LinkTimeout > TimeSpan.Zero
        ? LinkTimeout
        : throw new ArgumentOutOfRangeException(nameof(LinkTimeout), LinkTimeout, "The link timeout must be positive.");
}

/// <summary>
/// Reports links on the page that lead nowhere.
/// </summary>
/// <remarks>
/// Only the page given is read: this does not crawl the site. A link is broken when the target
/// answers 404, 410 or a server error, or does not answer at all. Answers that mean "we will not
/// talk to a robot" (401, 403, 429, and 999 as LinkedIn sends) say nothing about whether the link
/// works for a person, so they are counted as unverified rather than broken; reporting them as
/// defects would send an owner to fix links that are fine.
/// <para>
/// A broken link to the site's own pages is a <see cref="CheckStatus.Fail"/>, because the owner
/// controls it. A broken link to someone else's site is a <see cref="CheckStatus.Warn"/>: still
/// visible to visitors, but not something the owner broke.
/// </para>
/// <para>
/// Links are checked in parallel inside this check. That does not disturb
/// <see cref="LoadTimeCheck"/>: <see cref="Running.CheckRunner"/> never runs two checks at once.
/// </para>
/// </remarks>
public sealed class BrokenLinksCheck : ISiteCheck
{
    private const int ListedLinks = 5;

    private readonly HttpClient _httpClient;
    private readonly BrokenLinksCheckOptions _options;

    public BrokenLinksCheck(HttpClient httpClient, BrokenLinksCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _options = options ?? BrokenLinksCheckOptions.Default;
    }

    public string Name => "broken-links";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        string html;
        Uri page;

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return CheckOutcome.Fail(
                    $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}), so there was no page to check the links of.");
            }

            // After redirects, so relative links resolve against the page the visitor lands on.
            page = response.RequestMessage?.RequestUri ?? url;
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. See LoadTimeCheck.
            return CheckOutcome.Fail($"The site did not respond within {_httpClient.Timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s.");
        }
        catch (HttpRequestException ex)
        {
            return CheckOutcome.Fail($"The site could not be reached: {ex.Message}");
        }

        var links = ExtractLinks(html, page);

        if (links.Count == 0)
        {
            return CheckOutcome.Pass("The page has no links to check.");
        }

        var checkedLinks = links.Take(_options.MaxLinks).ToArray();
        var verdicts = new LinkVerdict[checkedLinks.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, checkedLinks.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = cancellationToken },
            async (index, token) => verdicts[index] = await VerifyAsync(checkedLinks[index], token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return Grade(page, links.Count, verdicts);
    }

    private static CheckOutcome Grade(Uri page, int found, LinkVerdict[] verdicts)
    {
        var broken = verdicts.Where(v => v.State == LinkState.Broken).ToArray();
        var unverified = verdicts.Count(v => v.State == LinkState.Unverified);
        var truncated = verdicts.Length < found;

        var unverifiedNote = unverified == 0
            ? string.Empty
            : $" {unverified} link(s) could not be verified because their sites refuse automated requests.";

        if (broken.Length == 0)
        {
            var scope = truncated ? $"The first {verdicts.Length} of {found} links" : $"All {verdicts.Length} link(s)";
            return CheckOutcome.Pass($"{scope} on the page work.{unverifiedNote}");
        }

        var truncatedNote = truncated ? $" Only the first {verdicts.Length} of {found} links were checked." : string.Empty;
        var detail = $"{broken.Length} of {verdicts.Length} checked link(s) are broken: {Describe(broken)}.{truncatedNote}{unverifiedNote}";

        return broken.Any(v => SameSite(v.Link, page)) ? CheckOutcome.Fail(detail) : CheckOutcome.Warn(detail);
    }

    private static string Describe(LinkVerdict[] broken)
    {
        var listed = string.Join(", ", broken.Take(ListedLinks).Select(v => $"{v.Link} ({v.Reason})"));

        return broken.Length > ListedLinks ? $"{listed} and {broken.Length - ListedLinks} more" : listed;
    }

    private async Task<LinkVerdict> VerifyAsync(Uri link, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.LinkTimeout);

        try
        {
            var status = await StatusOfAsync(link, HttpMethod.Head, deadline.Token).ConfigureAwait(false);

            // Plenty of servers refuse or mishandle HEAD while serving the same URL fine to GET,
            // so a HEAD that fails is not believed until GET agrees.
            if ((int)status >= 400)
            {
                status = await StatusOfAsync(link, HttpMethod.Get, deadline.Token).ConfigureAwait(false);
            }

            return new LinkVerdict(link, Classify(status), ((int)status).ToString(CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LinkVerdict(link, LinkState.Broken, "no answer in time");
        }
        catch (HttpRequestException)
        {
            return new LinkVerdict(link, LinkState.Broken, "could not be reached");
        }
    }

    private async Task<HttpStatusCode> StatusOfAsync(Uri link, HttpMethod method, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, link);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return response.StatusCode;
    }

    private static LinkState Classify(HttpStatusCode status) => (int)status switch
    {
        < 400 => LinkState.Working,
        401 or 403 or 429 or 999 => LinkState.Unverified,
        _ => LinkState.Broken,
    };

    /// <summary>
    /// The web links on the page in document order, resolved to absolute addresses, without
    /// fragments and without repeats.
    /// </summary>
    private static List<Uri> ExtractLinks(string html, Uri page)
    {
        var root = page;
        var declaredBase = HtmlTags.Find(html, "base").FirstOrDefault(tag => tag.ContainsKey("href"));

        if (declaredBase is not null
            && Uri.TryCreate(page, WebUtility.HtmlDecode(declaredBase["href"]).Trim(), out var resolvedBase)
            && IsWeb(resolvedBase))
        {
            root = resolvedBase;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Uri>();

        foreach (var anchor in HtmlTags.Find(html, "a"))
        {
            if (!anchor.TryGetValue("href", out var raw))
            {
                continue;
            }

            var href = WebUtility.HtmlDecode(raw).Trim();

            // mailto:, tel: and javascript: resolve to non-web schemes and drop out at IsWeb.
            if (href.Length == 0 || href[0] == '#'
                || !Uri.TryCreate(root, href, out var resolved) || !IsWeb(resolved))
            {
                continue;
            }

            var withoutFragment = new Uri(resolved.GetLeftPart(UriPartial.Query));

            if (seen.Add(withoutFragment.AbsoluteUri))
            {
                links.Add(withoutFragment);
            }
        }

        return links;
    }

    private static bool IsWeb(Uri uri) => uri.Scheme is "http" or "https";

    // www.example.com and example.com are one site to the person who owns them.
    private static bool SameSite(Uri link, Uri page) =>
        string.Equals(WithoutWww(link.Host), WithoutWww(page.Host), StringComparison.OrdinalIgnoreCase);

    private static string WithoutWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private enum LinkState
    {
        Working,
        Unverified,
        Broken,
    }

    private readonly record struct LinkVerdict(Uri Link, LinkState State, string Reason);
}
