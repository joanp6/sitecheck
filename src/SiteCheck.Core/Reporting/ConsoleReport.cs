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

    /// <summary>At least one check found a defect in the site.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// A check could not be evaluated and nothing failed. Kept apart from <see cref="ExitFailed"/>
    /// because it says something about the tooling, not the site. See <see cref="CheckStatus.Error"/>.
    /// </summary>
    public const int ExitToolError = 2;

    /// <summary>
    /// A defect outranks a tooling gap: if the site is already known to be broken, that is the
    /// news, and a script must not read it as "we could not tell".
    /// </summary>
    public static int ExitCodeFor(IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Any(r => r.Status == CheckStatus.Fail))
        {
            return ExitFailed;
        }

        return results.Any(r => r.Status == CheckStatus.Error) ? ExitToolError : ExitOk;
    }

    public static string Render(Uri url, IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(results);

        var nameWidth = results.Count == 0 ? 0 : results.Max(r => r.CheckName.Length);
        var report = new StringBuilder();

        report.AppendLine(CultureInfo.InvariantCulture, $"sitecheck report for {url}");
        report.AppendLine();

        foreach (var result in results)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {Label(result.Status),-5}  {result.CheckName.PadRight(nameWidth)}  {result.Detail}");
        }

        report.AppendLine();
        report.AppendLine(Summary(results));

        if (results.Any(r => r.Status == CheckStatus.Error))
        {
            report.AppendLine("ERROR means sitecheck itself could not evaluate a check. It is not a finding about the site.");
        }

        return report.ToString();
    }

    private static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Warn => "WARN",
        CheckStatus.Fail => "FAIL",
        CheckStatus.Error => "ERROR",
        CheckStatus.Skip => "SKIP",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown check status."),
    };

    private static string Summary(IReadOnlyList<CheckResult> results)
    {
        int Count(CheckStatus status) => results.Count(r => r.Status == status);

        return string.Create(CultureInfo.InvariantCulture,
            $"{results.Count} check(s): {Count(CheckStatus.Pass)} passed, {Count(CheckStatus.Warn)} warned, {Count(CheckStatus.Fail)} failed, {Count(CheckStatus.Error)} errored, {Count(CheckStatus.Skip)} skipped.");
    }
}
