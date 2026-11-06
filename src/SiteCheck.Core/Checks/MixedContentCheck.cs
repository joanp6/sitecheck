using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether a secure page pulls in parts of itself over plain http.
/// </summary>
/// <remarks>
/// A page served over https that loads a script, a stylesheet or a frame from an <c>http://</c>
/// address is "mixed content". Browsers refuse to load those parts at all, so the page arrives
/// without its menu, its styling or its booking widget, and the owner, who may have an old cached
/// copy, never sees why. Those are a <see cref="CheckStatus.Fail"/>.
/// <para>
/// Images, audio and video over http are treated more gently by current browsers: they try the
/// https address instead, and drop the file only if that fails. That is a
/// <see cref="CheckStatus.Warn"/>: it works today only if the other server happens to speak https.
/// A page that asks browsers to upgrade everything (<c>upgrade-insecure-requests</c>, as a header
/// or a meta tag) gets the same treatment for all its parts.
/// </para>
/// <para>
/// It reads the HTML only. Addresses inside stylesheets (<c>url(http://…)</c>), inline styles and
/// anything a script adds later are not seen, so a pass means "nothing in the HTML", not
/// "nothing at all". Forms are left to <see cref="ContactFormCheck"/>, which already fails a form
/// that sends over http.
/// </para>
/// <para>
/// When the page itself cannot be read, this is an <see cref="CheckStatus.Error"/>, not a
/// <see cref="CheckStatus.Fail"/>: there was nothing to look at, and <c>load-time</c> already
/// reports a site that is down or broken as the defect it is.
/// </para>
/// </remarks>
public sealed partial class MixedContentCheck : ISiteCheck
{
    private const int MaxListed = 3;

    /// <summary>Tags whose insecure source browsers block outright, and the attribute holding it.</summary>
    private static readonly (string Tag, string Attribute)[] Blocked =
    [
        ("script", "src"),
        ("iframe", "src"),
        ("frame", "src"),
        ("object", "data"),
        ("embed", "src"),
    ];

    /// <summary>Tags whose insecure source browsers try over https first.</summary>
    private static readonly (string Tag, string Attribute)[] Upgraded =
    [
        ("img", "src"),
        ("audio", "src"),
        ("video", "src"),
        ("video", "poster"),
        ("source", "src"),
        ("track", "src"),
    ];

    private readonly HttpClient _httpClient;

    public MixedContentCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "mixed-content";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        string html;
        Uri page;
        bool upgradeHeader;

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (RobotBlock.TurnsRobotsAway(response.StatusCode))
            {
                return RobotBlock.Skip(response, "read the page to look for insecure parts");
            }

            if (!response.IsSuccessStatusCode)
            {
                return CheckOutcome.Error(
                    $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}), so there was no page to look for insecure parts in.")
                    with { StatusCode = (int)response.StatusCode };
            }

            page = response.RequestMessage?.RequestUri ?? url;
            upgradeHeader = response.Headers.TryGetValues("Content-Security-Policy", out var policies)
                            && policies.Any(UpgradesInsecureRequests);
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckOutcome.Error(
                $"The site did not respond within {_httpClient.Timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s, so its page could not be read.");
        }
        catch (HttpRequestException ex)
        {
            return CheckOutcome.Error($"The page could not be read: {ex.Message}");
        }

        if (page.Scheme != Uri.UriSchemeHttps)
        {
            return CheckOutcome.Skip(
                "The page itself is served over plain http, so there is no secure page for insecure parts to spoil. https-redirect reports that.");
        }

        try
        {
            return Evaluate(html, page, upgradeHeader);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(string html, Uri page, bool upgradeHeader)
    {
        var upgradesAll = upgradeHeader || HtmlTags.Find(html, "meta").Any(meta =>
            string.Equals(meta.GetValueOrDefault("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase)
            && UpgradesInsecureRequests(meta.GetValueOrDefault("content", string.Empty)));

        var baseUri = BaseOf(html, page);

        var blocked = Insecure(html, baseUri, Blocked).Concat(InsecureStylesheets(html, baseUri)).Distinct().ToArray();
        var upgraded = Insecure(html, baseUri, Upgraded).Distinct().ToArray();

        if (blocked.Length == 0 && upgraded.Length == 0)
        {
            return CheckOutcome.Pass("Everything the page's HTML loads comes over https.");
        }

        if (blocked.Length > 0 && !upgradesAll)
        {
            return CheckOutcome.Fail(
                $"The secure page loads {Count(blocked.Length, "script, stylesheet or frame", "scripts, stylesheets or frames")} over plain http, which browsers refuse to load, so the page shows up incomplete: {List(blocked)}. Change those addresses to https.");
        }

        var all = blocked.Concat(upgraded).ToArray();

        return CheckOutcome.Warn(
            $"The secure page loads {Count(all.Length, "file", "files")} over plain http: {List(all)}. Browsers try https instead and drop the file if that fails, so it works only while the other server answers on https. Change those addresses to https.");
    }

    private static IEnumerable<Uri> Insecure(string html, Uri baseUri, (string Tag, string Attribute)[] sources) =>
        sources.SelectMany(source => HtmlTags.Find(html, source.Tag)
            .Select(tag => Resolve(baseUri, tag.GetValueOrDefault(source.Attribute))))
            .OfType<Uri>()
            .Where(uri => uri.Scheme == Uri.UriSchemeHttp);

    private static IEnumerable<Uri> InsecureStylesheets(string html, Uri baseUri) =>
        HtmlTags.Find(html, "link")
            .Where(link => link.GetValueOrDefault("rel", string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            .Select(link => Resolve(baseUri, link.GetValueOrDefault("href")))
            .OfType<Uri>()
            .Where(uri => uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// The address relative ones resolve against: the first <c>&lt;base href&gt;</c>, else the page.
    /// </summary>
    /// <remarks>
    /// Matters here more than anywhere: <c>&lt;base href="http://…"&gt;</c> turns every relative
    /// address on a secure page into an insecure one.
    /// </remarks>
    private static Uri BaseOf(string html, Uri page)
    {
        var href = HtmlTags.Find(html, "base").Select(tag => tag.GetValueOrDefault("href")).FirstOrDefault(value => value is not null);

        return Resolve(page, href) ?? page;
    }

    private static Uri? Resolve(Uri baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(baseUri, WebUtility.HtmlDecode(value).Trim(), out var resolved) ? resolved : null;
    }

    private static bool UpgradesInsecureRequests(string policy) => UpgradeDirective().IsMatch(policy);

    private static string Count(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : $"{count.ToString(CultureInfo.InvariantCulture)} {many}";

    private static string List(Uri[] uris)
    {
        var listed = string.Join(", ", uris.Take(MaxListed).Select(uri => uri.AbsoluteUri));

        return uris.Length > MaxListed ? $"{listed} and {uris.Length - MaxListed} more" : listed;
    }

    [GeneratedRegex(@"(^|;)\s*upgrade-insecure-requests\s*(;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UpgradeDirective();
}
