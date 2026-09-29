using System.Globalization;
using System.Text;
using SiteCheck.Checks;
using SiteCheck.Running;

namespace SiteCheck.Reporting;

/// <summary>
/// Renders a run as plain text for a terminal, and decides the process exit code.
/// </summary>
/// <remarks>
/// Plain text, no colour codes: the output is as likely to be piped into a file or an email as read
/// on screen, and escape sequences would turn into noise there.
/// </remarks>
public static class ConsoleReport
{
    /// <summary>Everything passed, or only warned.</summary>
    public const int ExitOk = 0;

    /// <summary>At least one check found a defect in a site.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// A check could not be evaluated and nothing failed. Kept apart from <see cref="ExitFailed"/>
    /// because it says something about the tooling, not the site. See <see cref="CheckStatus.Error"/>.
    /// </summary>
    public const int ExitToolError = 2;

    /// <summary>
    /// The exit code for a run over one or more sites, from the worst verdict among them.
    /// </summary>
    /// <remarks>See <see cref="SiteReport.Verdict"/> for why a defect outranks a tooling error.</remarks>
    public static int ExitCodeFor(IReadOnlyList<SiteReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        return SiteReport.Worst(reports.Select(r => r.Verdict)) switch
        {
            CheckStatus.Fail => ExitFailed,
            CheckStatus.Error => ExitToolError,
            _ => ExitOk,
        };
    }

    public static string Render(IReadOnlyList<SiteReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var text = new StringBuilder();

        foreach (var report in reports)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            RenderSite(text, report);
        }

        if (reports.Count > 1)
        {
            text.AppendLine();
            text.AppendLine(SitesSummary(reports));
        }

        if (reports.Any(r => r.Results.Any(result => result.Status == CheckStatus.Error)))
        {
            text.AppendLine("ERROR means sitecheck itself could not evaluate a check. It is not a finding about the site.");
        }

        return text.ToString();
    }

    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Warn => "WARN",
        CheckStatus.Fail => "FAIL",
        CheckStatus.Error => "ERROR",
        CheckStatus.Skip => "SKIP",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown check status."),
    };

    private static void RenderSite(StringBuilder text, SiteReport report)
    {
        var title = report.Site.Name is null ? report.Site.Url.ToString() : $"{report.Site.Name} ({report.Site.Url})";
        var nameWidth = report.Results.Count == 0 ? 0 : report.Results.Max(r => r.CheckName.Length);

        text.AppendLine(CultureInfo.InvariantCulture, $"sitecheck report for {title}");
        text.AppendLine();

        foreach (var result in report.Results)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {Label(result.Status),-5}  {result.CheckName.PadRight(nameWidth)}  {result.Detail}");
        }

        text.AppendLine();
        text.AppendLine(ChecksSummary(report.Results));
    }

    private static string ChecksSummary(IReadOnlyList<CheckResult> results)
    {
        int Count(CheckStatus status) => results.Count(r => r.Status == status);

        return string.Create(CultureInfo.InvariantCulture,
            $"{results.Count} check(s): {Count(CheckStatus.Pass)} passed, {Count(CheckStatus.Warn)} warned, {Count(CheckStatus.Fail)} failed, {Count(CheckStatus.Error)} errored, {Count(CheckStatus.Skip)} skipped.");
    }

    private static string SitesSummary(IReadOnlyList<SiteReport> reports)
    {
        int Count(CheckStatus verdict) => reports.Count(r => r.Verdict == verdict);

        return string.Create(CultureInfo.InvariantCulture,
            $"{reports.Count} site(s): {Count(CheckStatus.Pass)} fine, {Count(CheckStatus.Warn)} with warnings, {Count(CheckStatus.Fail)} failing, {Count(CheckStatus.Error)} not fully checked.");
    }
}
