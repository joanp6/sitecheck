using System.Text.RegularExpressions;
using System.Xml;
using SiteCheck.Checks;

namespace SiteCheck.Sites;

/// <summary>
/// Finds more pages of a site to audit than the one address it was given.
/// </summary>
/// <remarks>
/// <para>
/// An audit of the home page says nothing about the contact page with the broken form or the
/// price list nobody gave a title. This turns one site into a short list of its pages.
/// </para>
/// <para>
/// It asks the site rather than exploring it. A site that wants its pages found publishes them
/// in a sitemap (<c>/sitemap.xml</c>, or wherever <c>robots.txt</c> says), which is one or two
/// requests for the whole list. Only when there is none are the links on the given page used
/// instead. Either way it goes one step and stops: links are not followed from page to page,
/// so the number of requests is known before the first one is made.
/// </para>
/// <para>
/// Each extra page is returned as a <see cref="Site"/> of its own that skips
/// <see cref="OncePerHost"/>: a certificate, a domain or a server's headers are the same for
/// every page, and checking them fifteen times would be fifteen times the requests for one
/// answer.
/// </para>
/// <para>
/// Never throws for something the site did: a sitemap that is missing, broken or enormous just
/// means fewer pages are found.
/// </para>
/// </remarks>
public sealed partial class PageDiscovery
{
    /// <summary>
    /// Checks whose answer belongs to the host, not the page, so they run on the given address
    /// only.
    /// </summary>
    public static IReadOnlySet<string> OncePerHost { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ssl-certificate", "https-redirect", "domain-expiry", "email-auth", "tls-config", "www-address",
        "security-headers", "info-leak", "exposed-files", "pagespeed",
    };

    /// <summary>A sitemap index lists other sitemaps. This many of them are opened, and no more.</summary>
    private const int MaxChildSitemaps = 3;

    /// <summary>Addresses that are files to download, not pages to audit.</summary>
    private static readonly string[] NotPages =
    [
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".zip", ".gz", ".xml", ".json", ".txt",
        ".css", ".js", ".mp4", ".mp3", ".doc", ".docx", ".xls", ".xlsx", ".woff", ".woff2",
    ];

    private readonly HttpClient _httpClient;

    public PageDiscovery(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    /// <summary>
    /// <paramref name="site"/> followed by up to <paramref name="maxPages"/> − 1 more of its pages.
    /// </summary>
    /// <param name="site">The site as given. Always first in the result, unchanged.</param>
    /// <param name="maxPages">The most pages to return, the given one included.</param>
    /// <param name="cancellationToken">Stops looking.</param>
    public async Task<IReadOnlyList<Site>> ExpandAsync(Site site, int maxPages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);

        if (maxPages == 1)
        {
            return [site];
        }

        var found = await FromSitemapsAsync(site.Url, cancellationToken).ConfigureAwait(false);
        var fromLinks = found.Count == 0;

        if (fromLinks)
        {
            found = await FromLinksAsync(site.Url, cancellationToken).ConfigureAwait(false);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { Key(site.Url) };

        Uri[] candidates = [.. found
            .Where(address => IsPageOf(site.Url, address) && seen.Add(Key(address)))

            // The pages nearest the top are the ones visitors reach: /contact before /blog/2019/03/x.
            // Stable, so pages of equal depth keep the order the site listed them in.
            .OrderBy(address => address.Segments.Length)];

        var skip = new HashSet<string>(site.Skip.Concat(OncePerHost), StringComparer.OrdinalIgnoreCase);
        var pages = new List<Site>(maxPages) { site };

        // A sitemap is the site saying "these are my pages". A link is only a link, and a broken one
        // is broken-links' finding on the page that has it, not a page to audit: fourteen checks
        // failing on a 404 would bury that one line. So a linked page has to answer first. No more
        // are tried than twice the number wanted, to keep the requests bounded.
        foreach (var address in candidates.Take(fromLinks ? (maxPages - 1) * 2 : maxPages - 1))
        {
            if (pages.Count == maxPages)
            {
                break;
            }

            if (!fromLinks || await AnswersAsync(address, cancellationToken).ConfigureAwait(false))
            {
                pages.Add(new Site(address, $"{site.DisplayName} › {address.AbsolutePath}") { Skip = skip });
            }
        }

        return pages;
    }

    private async Task<bool> AnswersAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    private async Task<List<Uri>> FromSitemapsAsync(Uri page, CancellationToken cancellationToken)
    {
        var root = new Uri(page.GetLeftPart(UriPartial.Authority) + "/");
        var declared = await SitemapsInRobotsAsync(root, cancellationToken).ConfigureAwait(false);
        var sitemaps = new Queue<Uri>(declared.Count > 0 ? declared : [new Uri(root, "sitemap.xml")]);

        var pages = new List<Uri>();
        var opened = 0;

        while (sitemaps.Count > 0 && opened <= MaxChildSitemaps)
        {
            opened++;

            var (locations, isIndex) = Parse(await TextAsync(sitemaps.Dequeue(), cancellationToken).ConfigureAwait(false));

            foreach (var location in locations)
            {
                if (!Uri.TryCreate(location.Trim(), UriKind.Absolute, out var address))
                {
                    continue;
                }

                if (isIndex)
                {
                    sitemaps.Enqueue(address);
                }
                else
                {
                    pages.Add(address);
                }
            }
        }

        return pages;
    }

    private async Task<List<Uri>> SitemapsInRobotsAsync(Uri root, CancellationToken cancellationToken)
    {
        var robots = await TextAsync(new Uri(root, "robots.txt"), cancellationToken).ConfigureAwait(false);

        try
        {
            return [.. SitemapLine().Matches(robots ?? string.Empty)
                .Select(match => Uri.TryCreate(match.Groups["url"].Value.Trim(), UriKind.Absolute, out var address) ? address : null)
                .OfType<Uri>()
                .Where(address => address.Scheme is "http" or "https")
                .Take(MaxChildSitemaps)];
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private async Task<List<Uri>> FromLinksAsync(Uri page, CancellationToken cancellationToken)
    {
        var html = await TextAsync(page, cancellationToken).ConfigureAwait(false);

        if (html is null)
        {
            return [];
        }

        try
        {
            return [.. Anchors.Find(html)
                .Select(anchor => Uri.TryCreate(page, anchor.Href, out var address) ? address : null)
                .OfType<Uri>()];
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary>The body of a successful answer, or <see langword="null"/> for anything else.</summary>
    private async Task<string?> TextAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>The <c>&lt;loc&gt;</c> addresses in a sitemap, and whether it is an index of other sitemaps.</summary>
    private static (List<string> Locations, bool IsIndex) Parse(string? xml)
    {
        var locations = new List<string>();
        var isIndex = false;

        if (string.IsNullOrWhiteSpace(xml))
        {
            return (locations, isIndex);
        }

        try
        {
            // A sitemap is the site's file, not ours. No DTDs and no resolver, so it cannot make
            // this machine fetch or read anything (XXE), and expanding entities cannot blow up.
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "sitemapindex")
                {
                    isIndex = true;
                }
                else if (reader.LocalName == "loc")
                {
                    locations.Add(reader.ReadElementContentAsString());
                }
            }
        }
        catch (XmlException)
        {
            // What was read before the file went wrong is still worth having.
        }

        return (locations, isIndex);
    }

    /// <summary>Whether <paramref name="address"/> is a web page on the same host as <paramref name="site"/>.</summary>
    private static bool IsPageOf(Uri site, Uri address) =>
        address.Scheme is "http" or "https"
        && Host(address) == Host(site)
        && !NotPages.Any(extension => address.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    // A sitemap written for www.salon.es is still this site's when the audit was given salon.es.
    private static string Host(Uri address)
    {
        var host = address.IdnHost.ToLowerInvariant();

        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>What makes two addresses the same page: not the scheme, a fragment or a trailing slash.</summary>
    private static string Key(Uri address) => Host(address) + address.AbsolutePath.TrimEnd('/') + address.Query;

    [GeneratedRegex(@"^\s*sitemap\s*:\s*(?<url>\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SitemapLine();
}
