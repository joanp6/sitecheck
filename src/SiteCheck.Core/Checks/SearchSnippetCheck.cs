using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the page gives Google a title and a description of its own to show in results.
/// </summary>
/// <remarks>
/// A search result is the business's shop window before anyone visits: the blue line is the
/// page's <c>&lt;title&gt;</c>, and the two grey lines under it are usually its
/// <c>&lt;meta name="description"&gt;</c>. Left to a template, they read "Home" and
/// "Just another WordPress site", and the shop next door wins the click.
/// <list type="bullet">
/// <item><see cref="CheckStatus.Fail"/>: no title, or an empty one. Google has to make one up and
/// the browser tab shows the bare address.</item>
/// <item><see cref="CheckStatus.Warn"/>: a title a template left behind ("Home", "Document",
/// "Untitled"…), a title longer than <see cref="TitleMax"/> characters, which Google cuts short, no
/// description, a template's or placeholder description, or one outside
/// <see cref="DescriptionMin"/>–<see cref="DescriptionMax"/> characters.</item>
/// </list>
/// <para>
/// The lengths are the usual guidance, not rules Google publishes: it cuts by pixel width, and it
/// may show other text from the page instead of the description anyway. That is why nothing about
/// the description is ever more than a warning.
/// </para>
/// </remarks>
public sealed partial class SearchSnippetCheck : ISiteCheck
{
    public const int TitleMax = 60;
    public const int DescriptionMin = 50;
    public const int DescriptionMax = 160;

    // Compared after trimming, ignoring case and a trailing punctuation mark. Names of the business
    // or the town are never here: "Mataró" alone is a poor title, but it is the owner's choice.
    private static readonly HashSet<string> TemplateTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "home page", "homepage", "index", "inicio", "página de inicio", "pagina de inicio", "inici",
        "pàgina d'inici", "untitled", "untitled document", "untitled page", "sin título", "sin titulo",
        "document", "new page", "my site", "my website", "mi sitio", "mi sitio web", "site title",
        "my wordpress blog", "wordpress", "welcome", "bienvenido", "bienvenidos", "benvinguts",
    };

    private static readonly string[] TemplateDescriptions =
    [
        "just another wordpress site",
        "otro sitio realizado con wordpress",
        "un altre lloc creat amb wordpress",
        "lorem ipsum",
    ];

    private readonly HttpClient _httpClient;

    public SearchSnippetCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "search-snippet";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page's title and description", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(page.Html);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(string html)
    {
        var readable = Invisible().Replace(html, " ");
        var head = HeadEnd().Match(readable) is { Success: true } end ? readable[..end.Index] : readable;

        var title = TitleText().Match(head) is { Success: true } match ? Clean(Tag().Replace(match.Groups["text"].Value, " ")) : null;

        if (string.IsNullOrEmpty(title))
        {
            return CheckOutcome.Fail(
                "The page has no title, so Google makes one up for its results and the browser tab shows the bare address. Give it a <title> with the business's name and what it does, such as \"Hair salon in Mataró | Name\".");
        }

        var findings = new List<CheckOutcome>();

        if (IsTemplateTitle(title))
        {
            findings.Add(CheckOutcome.Warn(
                $"The page's title is \"{title}\", which a template left behind: it is what Google shows as the headline of the result. Put the business's name and what it does in it."));
        }
        else if (title.Length > TitleMax)
        {
            findings.Add(CheckOutcome.Warn(
                $"The page's title is {Count(title.Length)} characters long; Google cuts it after about {TitleMax}, so the end is lost: \"{Shorten(title, TitleMax)}…\". Put the most important words first."));
        }

        var description = DescriptionOf(readable);

        if (string.IsNullOrEmpty(description))
        {
            findings.Add(CheckOutcome.Warn(
                "The page has no description, so Google picks a fragment of the page to show under the title. Add <meta name=\"description\"> with a sentence on what the business offers and where."));
        }
        else if (TemplateDescriptions.Any(template => description.Contains(template, StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(CheckOutcome.Warn(
                $"The page's description is \"{Shorten(description, 80)}\", which a template left behind: Google may show it under the title. Replace it with a sentence on what the business offers and where."));
        }
        else if (description.Length < DescriptionMin)
        {
            findings.Add(CheckOutcome.Warn(
                $"The page's description is only {Count(description.Length)} characters (\"{description}\"), too short to tell searchers why to choose the business. Aim for {DescriptionMin} to {DescriptionMax}."));
        }
        else if (description.Length > DescriptionMax)
        {
            findings.Add(CheckOutcome.Warn(
                $"The page's description is {Count(description.Length)} characters long; Google cuts it after about {DescriptionMax}, so the end is lost. Put what matters first."));
        }

        if (findings.Count == 0)
        {
            return CheckOutcome.Pass($"The page gives Google its own title, \"{title}\", and a description to show in results.");
        }

        return CheckOutcome.Warn(string.Join(" ", findings.Select(finding => finding.Detail)));
    }

    private static string? DescriptionOf(string html)
    {
        foreach (var meta in HtmlTags.Find(html, "meta"))
        {
            if (string.Equals(meta.GetValueOrDefault("name")?.Trim(), "description", StringComparison.OrdinalIgnoreCase))
            {
                return Clean(meta.GetValueOrDefault("content", string.Empty));
            }
        }

        return null;
    }

    private static bool IsTemplateTitle(string title) =>
        TemplateTitles.Contains(title.TrimEnd('.', '!', ' '));

    /// <summary>Decodes entities and collapses runs of whitespace, as a browser shows the text.</summary>
    private static string Clean(string text) =>
        Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..length].TrimEnd();

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    // Scripts, styles and comments can hold the text "<title>" without it being the page's title.
    [GeneratedRegex(@"<script\b.*?</script\s*>|<style\b.*?</style\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Invisible();

    // The title lives in the head. Looking only there keeps an icon's <svg><title> in the body from
    // being taken for it.
    [GeneratedRegex(@"</head\s*>|<body\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadEnd();

    [GeneratedRegex(@"<title\b[^>]*>(?<text>.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TitleText();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
