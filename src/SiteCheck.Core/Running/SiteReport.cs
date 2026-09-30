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
