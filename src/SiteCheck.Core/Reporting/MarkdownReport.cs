using System.Globalization;
using System.Text;
using SiteCheck.Checks;
using SiteCheck.Running;

namespace SiteCheck.Reporting;

/// <summary>
/// Renders a run as Markdown, for pasting into an issue, a pull request, a wiki page or a chat.
/// </summary>
/// <remarks>
/// <para>
/// Problems first, like the HTML report, and one table per site. Checks that passed or did not
/// apply are folded into a count rather than listed: in an issue, twenty rows of "PASS" bury
/// the three that matter.
/// </para>
/// <para>
/// Details quote text from the audited page (a broken link, a cookie's name), and that page is
/// not ours. Everything is escaped so it cannot break the table, open a link, or smuggle HTML
/// into wherever this is pasted.
/// </para>
/// </remarks>
public static class MarkdownReport
{
    public static string Render(IReadOnlyList<SiteReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var text = new StringBuilder("# Website check\n");

        foreach (var report in reports)
        {
            var grade = report.Grade is { } letter ? $" · grade {letter}" : string.Empty;
            var title = report.Site.Name is null ? Escape(report.Site.Url.Host) : Escape(report.Site.Name);

            text.Append(CultureInfo.InvariantCulture, $"\n## {title} · {ConsoleReport.Label(report.Verdict)}{grade}\n\n");
            text.Append(CultureInfo.InvariantCulture, $"<{report.Site.Url.AbsoluteUri}>\n\n");

            CheckResult[] shown = [.. report.Results
                .Where(result => result.Status is CheckStatus.Fail or CheckStatus.Warn or CheckStatus.Error)
                .OrderBy(result => Urgency(result.Status))];

            if (shown.Length > 0)
            {
                text.Append("| Result | Check | What was found |\n|---|---|---|\n");

                foreach (var result in shown)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"| {ConsoleReport.Label(result.Status)} | `{result.CheckName}` | {Escape(result.Detail)} |\n");
                }

                text.Append('\n');
            }

            var passed = report.Results.Count(result => result.Status == CheckStatus.Pass);
            var skipped = report.Results.Count(result => result.Status == CheckStatus.Skip);

            text.Append(CultureInfo.InvariantCulture, $"{passed} check(s) passed");
            text.Append(skipped > 0 ? string.Create(CultureInfo.InvariantCulture, $", {skipped} did not apply.\n") : ".\n");
        }

        if (reports.Any(report => report.Results.Any(result => result.Status == CheckStatus.Error)))
        {
            text.Append("\nERROR means sitecheck itself could not evaluate a check. It is not a finding about the site.\n");
        }

        return text.ToString();
    }

    private static int Urgency(CheckStatus status) => status switch
    {
        CheckStatus.Fail => 0,
        CheckStatus.Warn => 1,
        _ => 2,
    };

    /// <summary>Makes <paramref name="text"/> inert inside a Markdown table cell.</summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 16);

        foreach (var character in text)
        {
            switch (character)
            {
                // A line break ends the table row.
                case '\r' or '\n':
                    escaped.Append(' ');
                    break;

                // Raw HTML is rendered by most Markdown viewers.
                case '<':
                    escaped.Append("&lt;");
                    break;

                case '>':
                    escaped.Append("&gt;");
                    break;

                case '&':
                    escaped.Append("&amp;");
                    break;

                // The rest are Markdown's own syntax: cell borders, emphasis, code, links, images.
                case '|' or '\\' or '`' or '*' or '_' or '[' or ']' or '!' or '#' or '~':
                    escaped.Append('\\').Append(character);
                    break;

                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }
}
