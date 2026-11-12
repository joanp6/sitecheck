using SiteCheck.Checks;
using SiteCheck.Sites;

namespace SiteCheck.Running;

/// <summary>
/// Everything one audit found about one site.
/// </summary>
/// <param name="Site">The site audited.</param>
/// <param name="CheckedAt">When the audit started.</param>
/// <param name="Results">One result per check, in the order they ran.</param>
public sealed record SiteReport(Site Site, DateTimeOffset CheckedAt, IReadOnlyList<CheckResult> Results)
{
    /// <summary>
    /// The site's overall verdict: the worst thing any check found.
    /// </summary>
    /// <remarks>
    /// A defect outranks a tooling error: if the site is known to be broken, that is the news, and
    /// "we could not tell" must not hide it. <see cref="CheckStatus.Skip"/> never counts, so a site
    /// where every check was skipped reads as <see cref="CheckStatus.Pass"/>: nothing was found wrong.
    /// </remarks>
    public CheckStatus Verdict => Worst(Results.Select(r => r.Status));

    /// <summary>
    /// The site in one letter, A to F, or <see langword="null"/> when too little could be checked
    /// to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For comparing sites at a glance, or the same site across months; the verdict says only
    /// "the worst thing found", which puts a site with one warning beside one with nine. It is
    /// deliberately a rule a person can recite, not a formula with weights nobody could defend:
    /// </para>
    /// <list type="table">
    /// <item><term>A</term><description>nothing to fix and nothing to look at</description></item>
    /// <item><term>B</term><description>no defects, up to 3 warnings</description></item>
    /// <item><term>C</term><description>no defects, 4 or more warnings</description></item>
    /// <item><term>D</term><description>1 defect</description></item>
    /// <item><term>F</term><description>2 or more defects</description></item>
    /// </list>
    /// <para>
    /// There is no E, as on a school report. Errors and skips count neither way, but when fewer
    /// than half the checks reached a verdict (a site behind a robot shield, say) there is no
    /// grade: an A for a site nobody managed to look at would be a lie.
    /// </para>
    /// </remarks>
    public string? Grade
    {
        get
        {
            var fails = Results.Count(result => result.Status == CheckStatus.Fail);
            var warns = Results.Count(result => result.Status == CheckStatus.Warn);
            var findings = fails + warns + Results.Count(result => result.Status == CheckStatus.Pass);

            if (findings == 0 || findings * 2 < Results.Count)
            {
                return null;
            }

            return (fails, warns) switch
            {
                ( >= 2, _) => "F",
                (1, _) => "D",
                (0, >= 4) => "C",
                (0, >= 1) => "B",
                _ => "A",
            };
        }
    }

    /// <summary>The worst of <paramref name="statuses"/>, ranked as <see cref="Verdict"/> ranks them.</summary>
    public static CheckStatus Worst(IEnumerable<CheckStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        return statuses.Aggregate(CheckStatus.Pass, (worst, next) => Rank(next) > Rank(worst) ? next : worst);
    }

    private static int Rank(CheckStatus status) => status switch
    {
        CheckStatus.Skip => 0,
        CheckStatus.Pass => 1,
        CheckStatus.Warn => 2,
        CheckStatus.Error => 3,
        CheckStatus.Fail => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown check status."),
    };
}
