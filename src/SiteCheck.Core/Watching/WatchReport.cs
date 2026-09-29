using System.Globalization;
using System.Text;
using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;

namespace SiteCheck.Watching;

/// <summary>
/// Tells a person what moved since the last run.
/// </summary>
public static class WatchReport
{
    /// <summary>Plain text: the console, and the message sent to the owner.</summary>
    public static string RenderText(WatchComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        if (comparison.Changes.Count == 0)
        {
            return $"sitecheck watch: no changes across {comparison.History.Count} site(s).{Environment.NewLine}";
        }

        var text = new StringBuilder();
        var worse = comparison.Changes.Count(c => c.Direction == ChangeDirection.Worse);
        var better = comparison.Changes.Count - worse;

        text.AppendLine(CultureInfo.InvariantCulture, $"sitecheck watch: {worse} got worse, {better} got better.");

        foreach (var change in comparison.Changes)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{(change.Direction == ChangeDirection.Worse ? "WORSE " : "BETTER")}  {change.Site.DisplayName} · {change.CheckName}: {Move(change)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"        {change.Detail}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Markdown for a GitHub Actions job summary: what moved, then where every site stands.
    /// </summary>
    public static string RenderMarkdown(WatchComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var markdown = new StringBuilder();

        markdown.AppendLine("## sitecheck watch");
        markdown.AppendLine();

        if (comparison.Changes.Count == 0)
        {
            markdown.AppendLine("No changes since the last run.");
        }
        else
        {
            markdown.AppendLine("| | Site | Check | Change | Detail |");
            markdown.AppendLine("|---|---|---|---|---|");

            foreach (var change in comparison.Changes)
            {
                markdown.AppendLine(CultureInfo.InvariantCulture,
                    $"| {(change.Direction == ChangeDirection.Worse ? "🔴" : "🟢")} | {Cell(change.Site.DisplayName)} | `{change.CheckName}` | {Move(change)} | {Cell(change.Detail)} |");
            }
        }

        markdown.AppendLine();
        markdown.AppendLine("### Where every site stands");
        markdown.AppendLine();
        markdown.AppendLine("| Site | Verdict | Warnings and failures |");
        markdown.AppendLine("|---|---|---|");

        foreach (var site in comparison.History)
        {
            var problems = site.Results
                .Where(r => r.Status is CheckStatus.Warn or CheckStatus.Fail)
                .Select(r => $"`{r.CheckName}`");

            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(site.Site.DisplayName)} | {ConsoleReport.Label(site.Verdict)} | {string.Join(", ", problems)} |");
        }

        return markdown.ToString();
    }

    private static string Move(Change change) =>
        change.Before is { } before
            ? $"{ConsoleReport.Label(before)} → {ConsoleReport.Label(change.After)}"
            : $"{ConsoleReport.Label(change.After)} (first seen)";

    // A pipe would end the cell and a newline the row; details are prose that could contain either.
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
