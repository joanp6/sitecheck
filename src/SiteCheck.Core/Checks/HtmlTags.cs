using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reads the attributes of a kind of tag out of an HTML document.
/// </summary>
/// <remarks>
/// A pattern rather than an HTML parser, because a few attributes of a few tags do not justify a
/// dependency. The price is that markup inside an HTML comment or a script still counts as a tag.
/// Both patterns carry a match timeout so that a hostile page fails fast instead of stalling an
/// audit; the resulting <see cref="RegexMatchTimeoutException"/> is the caller's to interpret.
/// </remarks>
internal static partial class HtmlTags
{
    /// <summary>
    /// Every <paramref name="tagName"/> tag in document order, as its attributes.
    /// </summary>
    /// <remarks>
    /// Names are case-insensitive and values come back exactly as written, entities included.
    /// When an attribute appears twice the first wins, as it does in a browser. An attribute with
    /// no value is absent from the result.
    /// </remarks>
    public static IEnumerable<IReadOnlyDictionary<string, string>> Find(string html, string tagName)
    {
        foreach (Match tag in Tag().Matches(html))
        {
            if (!string.Equals(tag.Groups["name"].Value, tagName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match attribute in Attribute().Matches(tag.Groups["attributes"].Value))
            {
                var value = attribute.Groups["dq"].Success ? attribute.Groups["dq"].Value
                    : attribute.Groups["sq"].Success ? attribute.Groups["sq"].Value
                    : attribute.Groups["bare"].Value;

                attributes.TryAdd(attribute.Groups["key"].Value, value);
            }

            yield return attributes;
        }
    }

    // The lookahead after the name keeps <a-custom-element> from being read as an <a>. Quoted
    // values are matched whole, so a ">" inside href="a>b" does not end the tag early.
    [GeneratedRegex(
        """<(?<name>[a-zA-Z][a-zA-Z0-9]*)(?=[\s/>])(?<attributes>(?:[^>"']|"[^"]*"|'[^']*')*)>""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(
        """(?<key>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>[^\s"'>]+))""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribute();
}
