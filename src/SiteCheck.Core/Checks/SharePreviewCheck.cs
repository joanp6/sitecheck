using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether a link to the page, sent on WhatsApp or posted on Facebook, shows a proper
/// preview, and whether the page has its own icon.
/// </summary>
/// <remarks>
/// Small businesses are shared by word of mouth, which today means a link pasted into WhatsApp.
/// The preview that link unfolds into comes from Open Graph tags: <c>og:image</c> for the picture,
/// <c>og:title</c> and <c>og:description</c> for the words (falling back to the page's title and
/// description). Without <c>og:image</c> the link arrives as a bare line of text. The favicon is
/// the small icon on the browser tab, in bookmarks and beside the site in Google's results.
/// <para>
/// Each address is asked whether it exists (HEAD, or GET when HEAD is refused). An image address
/// that answers with a web page, as a site that sends every unknown address to its home page
/// does, is not an image and counts as missing. When the site cannot be asked at all (timeout,
/// refused, a robot block) nothing is concluded about that address.
/// </para>
/// <para>
/// Never more than a <see cref="CheckStatus.Warn"/>: visitors on the site itself are not affected.
/// Image dimensions are not checked, since that means downloading and decoding the image.
/// </para>
/// </remarks>
public sealed class SharePreviewCheck : ISiteCheck
{
    private readonly HttpClient _httpClient;

    public SharePreviewCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "share-preview";

    private enum Probe
    {
        Works,
        Missing,
        NotAnImage,
        Unknown,
    }

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page's sharing tags", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        IReadOnlyDictionary<string, string> metas;
        string? iconHref;

        try
        {
            metas = OpenGraph(page.Html);
            iconHref = IconOf(page.Html);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }

        var findings = new List<string>();
        var unverified = new List<string>();

        // The picture.
        if (!metas.TryGetValue("og:image", out var image) || string.IsNullOrWhiteSpace(image))
        {
            findings.Add("Shared on WhatsApp or Facebook, a link to the page shows no picture: it has no og:image. Add <meta property=\"og:image\" content=\"https://…/photo.jpg\"> with a photo of the business.");
        }
        else if (!Uri.TryCreate(image.Trim(), UriKind.Absolute, out var imageUri) || imageUri.Scheme is not ("http" or "https"))
        {
            findings.Add($"The page's og:image is \"{image.Trim()}\", not a full address, and WhatsApp and Facebook only read full ones (https://…). Shared, the link shows no picture.");
        }
        else
        {
            switch (await ProbeAsync(imageUri, cancellationToken).ConfigureAwait(false))
            {
                case Probe.Missing:
                    findings.Add($"The page's og:image, {imageUri}, does not load, so a shared link shows no picture.");
                    break;
                case Probe.NotAnImage:
                    findings.Add($"The page's og:image, {imageUri}, answers with a web page rather than an image, so a shared link shows no picture.");
                    break;
                case Probe.Unknown:
                    unverified.Add("the og:image");
                    break;
            }
        }

        // The words. Without og:title, WhatsApp and Facebook use the page's <title>, so only a page
        // with neither is short of words; search-snippet already reports a missing title.
        var hasTitle = metas.ContainsKey("og:title");

        // The icon: the declared one, or /favicon.ico, where browsers look when none is declared.
        var iconUri = iconHref is null ? new Uri(page.Address, "/favicon.ico") : Resolve(page.Address, iconHref);

        if (iconUri is null)
        {
            findings.Add("The page declares an icon that is not an address sitecheck can follow, so browsers show a blank one on the tab and in bookmarks.");
        }
        else
        {
            switch (await ProbeAsync(iconUri, cancellationToken).ConfigureAwait(false))
            {
                case Probe.Missing or Probe.NotAnImage:
                    findings.Add(iconHref is null
                        ? "The page has no favicon, so browsers show a blank icon on the tab and in bookmarks, and Google a generic globe beside it in results. Add <link rel=\"icon\" href=\"/favicon.png\"> with the business's logo."
                        : $"The page's icon, {iconUri}, does not load, so browsers show a blank one on the tab and in bookmarks.");
                    break;
                case Probe.Unknown:
                    unverified.Add("the favicon");
                    break;
            }
        }

        var unknown = unverified.Count == 0
            ? string.Empty
            : $" sitecheck could not ask for {string.Join(" or ", unverified)}, so whether {(unverified.Count == 1 ? "it loads" : "they load")} is not known.";

        if (findings.Count == 0)
        {
            var words = hasTitle ? "its own title and picture" : "its picture, under the page's title";

            return CheckOutcome.Pass($"Shared on WhatsApp or Facebook, a link to the page shows {words}, and the page has a favicon.{unknown}");
        }

        return CheckOutcome.Warn(string.Join(" ", findings) + unknown);
    }

    private async Task<Probe> ProbeAsync(Uri address, CancellationToken cancellationToken)
    {
        // An icon written into the page itself (data:image/svg+xml,…) is there by definition.
        if (address.Scheme == "data")
        {
            return Probe.Works;
        }

        try
        {
            using var head = await SendAsync(HttpMethod.Head, address, cancellationToken).ConfigureAwait(false);

            if (head.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var get = await SendAsync(HttpMethod.Get, address, cancellationToken).ConfigureAwait(false);

                return Judge(get);
            }

            return Judge(head);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Probe.Unknown;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri address, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static Probe Judge(HttpResponseMessage response)
    {
        if (RobotBlock.TurnsRobotsAway(response.StatusCode))
        {
            return Probe.Unknown;
        }

        if (!response.IsSuccessStatusCode)
        {
            return Probe.Missing;
        }

        var type = response.Content.Headers.ContentType?.MediaType;

        return type is not null && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            ? Probe.NotAnImage
            : Probe.Works;
    }

    /// <summary>Open Graph values by property, first one winning, as crawlers read them.</summary>
    private static Dictionary<string, string> OpenGraph(string html)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var meta in HtmlTags.Find(html, "meta"))
        {
            // Facebook's spec says property; many sites write name, and crawlers accept both.
            var key = meta.GetValueOrDefault("property") ?? meta.GetValueOrDefault("name");

            if (key is not null && key.StartsWith("og:", StringComparison.OrdinalIgnoreCase) && meta.TryGetValue("content", out var content))
            {
                values.TryAdd(key.Trim(), WebUtility.HtmlDecode(content));
            }
        }

        return values;
    }

    /// <summary>The <c>href</c> of the first icon the page declares, or <see langword="null"/> if none.</summary>
    private static string? IconOf(string html) =>
        HtmlTags.Find(html, "link")
            .Where(link => link.GetValueOrDefault("rel", string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(rel => rel.Equals("icon", StringComparison.OrdinalIgnoreCase) || rel.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase)))
            .Select(link => link.GetValueOrDefault("href"))
            .FirstOrDefault(href => !string.IsNullOrWhiteSpace(href));

    private static Uri? Resolve(Uri page, string href) =>
        Uri.TryCreate(page, WebUtility.HtmlDecode(href).Trim(), out var resolved) && resolved.Scheme is "http" or "https" or "data"
            ? resolved
            : null;
}
