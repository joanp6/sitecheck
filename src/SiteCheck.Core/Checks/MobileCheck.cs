using System.Globalization;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the page tells phones to lay it out at the phone's own width.
/// </summary>
/// <remarks>
/// This reads the <c>viewport</c> declaration and nothing more. Without
/// <c>width=device-width</c> a phone renders the page at desktop width and shrinks it, which is
/// the difference between a readable site and one a visitor has to pinch and pan. It is the
/// cheapest signal that a site was built with phones in mind, but it is only a signal: a page can
/// declare it correctly and still have a layout that breaks on a small screen. Nothing here
/// renders the page, so nothing here can say otherwise.
/// </remarks>
public sealed partial class MobileCheck : ISiteCheck
{
    private readonly HttpClient _httpClient;

    public MobileCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "mobile-viewport";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        string html;

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return CheckOutcome.Fail(
                    $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}), so there was no page to check on a phone.");
            }

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

        return Evaluate(FindViewport(html));
    }

    private static CheckOutcome Evaluate(string? viewport)
    {
        if (viewport is null)
        {
            return CheckOutcome.Fail(
                "The page does not declare a mobile viewport, so phones show it at desktop width, shrunk. Add <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">.");
        }

        var directives = ParseDirectives(viewport);

        if (!FollowsDeviceWidth(directives))
        {
            return CheckOutcome.Fail(
                "The page declares a viewport but not width=device-width, so phones still do not lay it out at their own width.");
        }

        if (BlocksZoom(directives))
        {
            return CheckOutcome.Warn(
                "The page fits a phone screen but stops visitors zooming in, which people with poor eyesight rely on. Remove user-scalable=no and any low maximum-scale.");
        }

        return CheckOutcome.Pass("The page declares a mobile viewport that follows the device width.");
    }

    private static bool FollowsDeviceWidth(Dictionary<string, string> directives)
    {
        if (directives.TryGetValue("width", out var width))
        {
            return string.Equals(width, "device-width", StringComparison.OrdinalIgnoreCase);
        }

        // With no width, a mobile browser lays the page out at device-width divided by
        // initial-scale, so initial-scale=1 alone is the same thing. Wikipedia ships exactly
        // that, which is how a stricter rule was caught grading a mobile-first site as broken.
        // Any other scale gives a layout width nobody chose on purpose.
        return directives.TryGetValue("initial-scale", out var scale)
               && double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
               && parsed == 1;
    }

    private static bool BlocksZoom(Dictionary<string, string> directives)
    {
        if (directives.TryGetValue("user-scalable", out var scalable)
            && (string.Equals(scalable, "no", StringComparison.OrdinalIgnoreCase) || scalable == "0"))
        {
            return true;
        }

        // Below 2 a visitor cannot enlarge text to the size accessibility guidance asks a page to allow.
        return directives.TryGetValue("maximum-scale", out var maximum)
               && double.TryParse(maximum, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
               && scale < 2;
    }

    private static Dictionary<string, string> ParseDirectives(string content)
    {
        var directives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Browsers accept commas and semicolons, so both separate directives.
        foreach (var part in content.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);

            if (pair.Length == 2 && pair[0].Length > 0)
            {
                directives[pair[0]] = pair[1];
            }
        }

        return directives;
    }

    /// <summary>
    /// Returns the <c>content</c> of the first <c>&lt;meta name="viewport"&gt;</c>, or
    /// <see langword="null"/> if the page has none.
    /// </summary>
    /// <remarks>
    /// A pattern rather than an HTML parser, because one attribute of one tag does not justify a
    /// dependency. The price is that a tag inside an HTML comment still counts.
    /// </remarks>
    private static string? FindViewport(string html)
    {
        try
        {
            foreach (Match tag in MetaTag().Matches(html))
            {
                string? name = null;
                string? content = null;

                foreach (Match attribute in Attribute().Matches(tag.Value))
                {
                    var value = attribute.Groups["dq"].Success ? attribute.Groups["dq"].Value
                        : attribute.Groups["sq"].Success ? attribute.Groups["sq"].Value
                        : attribute.Groups["bare"].Value;

                    switch (attribute.Groups["key"].Value.ToLowerInvariant())
                    {
                        case "name":
                            name = value;
                            break;
                        case "content":
                            content = value;
                            break;
                    }
                }

                if (string.Equals(name, "viewport", StringComparison.OrdinalIgnoreCase))
                {
                    return content ?? string.Empty;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Treated as "not found" rather than as a tooling error: a page hostile enough to
            // stall the pattern is not one a phone would render well either.
        }

        return null;
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetaTag();

    [GeneratedRegex(
        """(?<key>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>[^\s"'>]+))""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribute();
}
