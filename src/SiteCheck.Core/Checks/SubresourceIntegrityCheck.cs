using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports scripts and stylesheets loaded from public libraries' servers without a fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// A page that loads jQuery from a public server (a CDN) runs whatever that server sends, with
/// full access to the page: its forms, its visitors' cookies. If the server is broken into, or
/// its domain changes hands, every site that points at it serves the attacker's script the same
/// afternoon. That is not hypothetical: in 2024 the domain <c>polyfill.io</c> was sold and began
/// sending malware to the visitors of over a hundred thousand sites.
/// </para>
/// <para>
/// The defence is one attribute, <c>integrity="sha384-…"</c>: the fingerprint of the file the
/// site's author meant to load. The browser refuses anything that does not match. It is called
/// Subresource Integrity (SRI). A library file without it is a <see cref="CheckStatus.Warn"/>.
/// </para>
/// <para>
/// Only servers that hand out fixed, versioned library files are held to it
/// (<c>cdnjs.cloudflare.com</c>, <c>cdn.jsdelivr.net</c>, <c>unpkg.com</c>,
/// <c>code.jquery.com</c> and the like). Analytics, maps, chat and booking widgets come from
/// other servers too, but their files change whenever their makers ship, so a fingerprint would
/// break them; they are left alone rather than nagged about something that cannot be done.
/// </para>
/// <para>
/// A script from a server known to have served malware is a <see cref="CheckStatus.Fail"/>,
/// fingerprint or not: at best the file no longer loads and something on the page is broken.
/// </para>
/// <para>
/// Reads the HTML only, so scripts that other scripts add are not seen.
/// </para>
/// </remarks>
public sealed class SubresourceIntegrityCheck : ISiteCheck
{
    private const int MaxListed = 3;

    /// <summary>Servers whose files are versioned and never change, so a fingerprint always fits.</summary>
    private static readonly string[] LibraryHosts =
    [
        "cdnjs.cloudflare.com", "cdn.jsdelivr.net", "unpkg.com", "code.jquery.com", "ajax.googleapis.com",
        "ajax.aspnetcdn.com", "stackpath.bootstrapcdn.com", "maxcdn.bootstrapcdn.com", "netdna.bootstrapcdn.com",
        "cdn.bootcdn.net", "lib.baomitu.com", "use.fontawesome.com", "kit.fontawesome.com", "cdn.datatables.net",
    ];

    /// <summary>Domains taken over in 2024 and used to send malware to the sites that pointed at them.</summary>
    private static readonly string[] CompromisedDomains = ["polyfill.io", "bootcss.com", "bootcdn.net", "staticfile.org", "staticfile.net"];

    private readonly HttpClient _httpClient;

    public SubresourceIntegrityCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "sri";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (fetched, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page to see where its scripts come from", cancellationToken)
            .ConfigureAwait(false);

        if (fetched is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(fetched.Html, fetched.Address);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(string html, Uri page)
    {
        var loaded = HtmlTags.Find(html, "script")
            .Select(tag => (Address: tag.GetValueOrDefault("src"), Tag: tag))
            .Concat(HtmlTags.Find(html, "link")
                .Where(tag => tag.GetValueOrDefault("rel", string.Empty)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
                .Select(tag => (Address: tag.GetValueOrDefault("href"), Tag: tag)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Address))
            .Select(entry => (
                Address: Uri.TryCreate(page, WebUtility.HtmlDecode(entry.Address!).Trim(), out var resolved) ? resolved : null,
                Fingerprinted: !string.IsNullOrWhiteSpace(entry.Tag.GetValueOrDefault("integrity"))))
            .Where(entry => entry.Address is { Scheme: "http" or "https" })
            .Select(entry => (Address: entry.Address!, entry.Fingerprinted))
            .ToArray();

        var compromised = loaded.Where(entry => IsOn(entry.Address, CompromisedDomains)).Select(entry => entry.Address).Distinct().ToArray();

        if (compromised.Length > 0)
        {
            return CheckOutcome.Fail(
                $"The page loads {Count(compromised.Length)} from a server that was taken over in 2024 and used to send malware to visitors: {List(compromised)}. "
                + "Remove them, or load the same library from a server you trust; at best they no longer load and something on the page is broken.");
        }

        var libraries = loaded.Where(entry => LibraryHosts.Contains(entry.Address.IdnHost, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (libraries.Length == 0)
        {
            return CheckOutcome.Pass("The page loads no scripts or stylesheets from public library servers, so there is none to fingerprint.");
        }

        var bare = libraries.Where(entry => !entry.Fingerprinted).Select(entry => entry.Address).Distinct().ToArray();

        return bare.Length == 0
            ? CheckOutcome.Pass(
                $"The {Count(libraries.Length)} the page loads from public library servers each carry a fingerprint, so a tampered copy would be refused.")
            : CheckOutcome.Warn(
                $"The page loads {Count(bare.Length)} from public library servers without a fingerprint: {List(bare)}. "
                + "If one of those servers were broken into, its script would run on this site with nobody noticing. "
                + "Adding integrity=\"sha384-…\" and crossorigin=\"anonymous\" to each tag, as the library's own site shows, closes that.");
    }

    private static bool IsOn(Uri address, string[] domains)
    {
        var host = address.IdnHost;

        return domains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private static string Count(int files) =>
        files == 1 ? "1 file" : files.ToString(CultureInfo.InvariantCulture) + " files";

    private static string List(Uri[] addresses)
    {
        var listed = string.Join(", ", addresses.Take(MaxListed).Select(address => address.GetLeftPart(UriPartial.Path)));

        return addresses.Length > MaxListed
            ? $"{listed} and {(addresses.Length - MaxListed).ToString(CultureInfo.InvariantCulture)} more"
            : listed;
    }
}
