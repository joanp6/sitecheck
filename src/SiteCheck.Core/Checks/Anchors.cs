using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>A link on the page: where it goes and the words that describe it.</summary>
/// <param name="Href">The address as written, entities decoded.</param>
/// <param name="Text">
/// What a visitor reads, plus the <c>title</c> and <c>aria-label</c> a screen reader reads for an
/// icon-only link. Entities decoded, tags removed, otherwise as written.
/// </param>
internal sealed record Anchor(string Href, string Text);

/// <summary>
/// Reads the links of a page with their words, for checks that recognise a link by what it says,
/// and puts words into one form so they compare in any language's spelling.
/// </summary>
/// <remarks>
/// <see cref="HtmlTags"/> gives attributes only; these checks need what is between
/// <c>&lt;a&gt;</c> and <c>&lt;/a&gt;</c> too. Links inside HTML comments are not read.
/// </remarks>
internal static partial class Anchors
{
    public static IEnumerable<Anchor> Find(string html)
    {
        foreach (Match anchor in AnchorTag().Matches(Comment().Replace(html, " ")))
        {
            var attributes = HtmlTags.Find(anchor.Value, "a").FirstOrDefault();
            var href = WebUtility.HtmlDecode(attributes?.GetValueOrDefault("href") ?? string.Empty).Trim();

            var text = string.Join(
                " ",
                WebUtility.HtmlDecode(Tag().Replace(anchor.Groups["text"].Value, " ")),
                attributes?.GetValueOrDefault("title") ?? string.Empty,
                attributes?.GetValueOrDefault("aria-label") ?? string.Empty);

            yield return new Anchor(href, Whitespace().Replace(text, " ").Trim());
        }
    }

    /// <summary>
    /// Lower case, accents dropped and separators turned to spaces, so "Política_de-Privacidad"
    /// reads "politica de privacidad" and "Demana cita" matches however it was typed.
    /// </summary>
    public static string Normalise(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c is '-' or '_' or '.' or ' ' ? ' ' : c);
        }

        return Whitespace().Replace(builder.ToString(), " ");
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Comment();

    [GeneratedRegex(
        """<a(?=[\s>])(?:[^>"']|"[^"]*"|'[^']*')*>(?<text>.*?)</a\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnchorTag();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
