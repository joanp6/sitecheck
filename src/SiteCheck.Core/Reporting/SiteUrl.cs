namespace SiteCheck.Reporting;

/// <summary>
/// Turns what a person types into the address the checks run against.
/// </summary>
public static class SiteUrl
{
    /// <summary>
    /// Parses <paramref name="input"/> as a website address.
    /// </summary>
    /// <remarks>
    /// A bare host such as <c>example.com</c> is read as <c>https://example.com/</c>: a site owner
    /// types the name they know, not a scheme. Only http and https are accepted, because every
    /// check speaks one of the two and anything else (<c>ftp:</c>, <c>file:</c>) is a typo or worse.
    /// </remarks>
    public static bool TryParse(string? input, out Uri url)
    {
        url = null!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(parsed.Host)
            || !HasHostNameLabels(parsed.Host))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>
    /// Whether no part of <paramref name="host"/> begins or ends with a hyphen, which no host
    /// name may (RFC 952, RFC 1123).
    /// </summary>
    /// <remarks>
    /// .NET's own parser lets it through, and that is how a mistyped option became a site:
    /// <c>--no-rety</c> read as the host <c>--no-rety</c>, was audited, failed every check, and
    /// turned the run red with nothing to say why. Refusing it here makes a typo an error.
    /// </remarks>
    private static bool HasHostNameLabels(string host) =>
        host.Split('.').All(label => !label.StartsWith('-') && !label.EndsWith('-'));
}
