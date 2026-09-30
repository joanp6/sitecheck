using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SiteCheck.Checks;

/// <summary>
/// Asks Google's PageSpeed Insights how fast the page feels on a mid-range phone.
/// </summary>
/// <remarks>
/// <see cref="LoadTimeCheck"/> times the HTML arriving. This loads the whole page in a real Chrome,
/// images, scripts and all, on a throttled phone connection, which is what a visitor waits for.
/// Running Lighthouse through Google's API gives that without shipping a browser with the tool.
/// <para>
/// It is opt-in, for three reasons. It sends the site's address to Google. It takes ten seconds
/// to a minute per site. And its score moves a few points between runs of the same page, which is
/// why <c>watch</c> does not use it: a score wobbling around 90 would report "worse" and "better" on
/// alternate mornings.
/// </para>
/// <para>
/// The anonymous quota is shared by everyone calling the API and is, in practice, always spent, so
/// an API key is needed. It is free; docs/pagespeed.md says where to get one.
/// </para>
/// </remarks>
public sealed class PageSpeedCheck : ISiteCheck
{
    public static readonly Uri Endpoint = new("https://www.googleapis.com/pagespeedonline/v5/runPagespeed");

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;

    /// <param name="httpClient">
    /// Needs a timeout of a minute or more: the API loads the page before it answers.
    /// </param>
    /// <param name="apiKey">A Google API key with PageSpeed Insights enabled.</param>
    public PageSpeedCheck(HttpClient httpClient, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public string Name => "pagespeed";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var query = $"?url={Uri.EscapeDataString(url.AbsoluteUri)}&strategy=mobile&category=performance";

        if (_apiKey is not null)
        {
            query += "&key=" + Uri.EscapeDataString(_apiKey);
        }

        using var response = await _httpClient.GetAsync(new Uri(Endpoint, query), cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Everything here is about Google's side, or about a page Lighthouse could not load,
            // which load-time already reports as the site's failure. So it is a tooling Error,
            // thrown for the runner to record. The request address is left out of the message on
            // purpose: it carries the API key, and the message lands in reports and CI logs.
            throw new HttpRequestException(Explain(response.StatusCode, document.RootElement), inner: null, response.StatusCode);
        }

        var lighthouse = document.RootElement.GetProperty("lighthouseResult");
        var score = lighthouse.GetProperty("categories").GetProperty("performance").GetProperty("score");

        if (score.ValueKind != JsonValueKind.Number)
        {
            var reason = lighthouse.TryGetProperty("runtimeError", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : "no reason given";

            throw new HttpRequestException($"PageSpeed Insights could not score the page: {reason}");
        }

        var points = (int)Math.Round(score.GetDouble() * 100);
        var detail = $"Google rates the page {points}/100 for speed on a mid-range phone{Wait(lighthouse)}.";

        // Lighthouse's own bands: 90 and up is good, under 50 is poor.
        return points switch
        {
            >= 90 => CheckOutcome.Pass(detail),
            >= 50 => CheckOutcome.Warn(detail + " Heavy images and scripts are the usual cause."),
            _ => CheckOutcome.Fail(detail + " Many visitors on phones leave before it finishes. Heavy images and scripts are the usual cause."),
        };
    }

    private static string Wait(JsonElement lighthouse) =>
        lighthouse.TryGetProperty("audits", out var audits)
        && audits.TryGetProperty("largest-contentful-paint", out var lcp)
        && lcp.TryGetProperty("numericValue", out var milliseconds)
        && milliseconds.ValueKind == JsonValueKind.Number
            ? string.Create(CultureInfo.InvariantCulture, $"; its main content appears after {milliseconds.GetDouble() / 1000:0.#} s")
            : string.Empty;

    private string Explain(HttpStatusCode status, JsonElement root)
    {
        var google = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;

        if (status == HttpStatusCode.TooManyRequests)
        {
            return _apiKey is null
                ? "Google's shared anonymous quota for PageSpeed Insights is used up, as it nearly always is. Set SITECHECK_PAGESPEED_KEY to a free API key; see docs/pagespeed.md."
                : "The PageSpeed Insights quota for this API key is used up for today.";
        }

        return $"PageSpeed Insights answered {(int)status}: {google ?? "no reason given"}";
    }
}
