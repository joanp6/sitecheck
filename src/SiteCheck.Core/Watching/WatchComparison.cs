using SiteCheck.Checks;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Watching;

/// <summary>Which way a check moved since the last run.</summary>
public enum ChangeDirection
{
    Worse,
    Better,
}

/// <summary>
/// One check on one site whose verdict moved since the last run.
/// </summary>
/// <param name="Site">The site.</param>
/// <param name="CheckName">The check.</param>
/// <param name="Before">
/// The last verdict known for it, or <see langword="null"/> on the first run that saw it.
/// </param>
/// <param name="After">The verdict now.</param>
/// <param name="Detail">What the check says now, for a person to act on.</param>
public sealed record Change(Site Site, string CheckName, CheckStatus? Before, CheckStatus After, string Detail)
{
    public ChangeDirection Direction => Severity(After) > Severity(Before ?? CheckStatus.Pass)
        ? ChangeDirection.Worse
        : ChangeDirection.Better;

    internal static int Severity(CheckStatus status) => status switch
    {
        CheckStatus.Pass => 0,
        CheckStatus.Warn => 1,
        CheckStatus.Fail => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Only findings about a site have a severity."),
    };
}

/// <summary>What changed since the last run, and the history to keep for the next one.</summary>
/// <param name="Changes">Every check that moved, worse ones first.</param>
/// <param name="History">The last known verdict of every check on every site still being watched.</param>
public sealed record WatchComparison(IReadOnlyList<Change> Changes, IReadOnlyList<SiteReport> History)
{
    public bool AnythingWorse => Changes.Any(change => change.Direction == ChangeDirection.Worse);

    /// <summary>
    /// Compares this run's <paramref name="current"/> reports against the <paramref name="previous"/> history.
    /// </summary>
    /// <remarks>
    /// Only findings about a site move: <see cref="CheckStatus.Pass"/>, <see cref="CheckStatus.Warn"/>
    /// and <see cref="CheckStatus.Fail"/>. An <see cref="CheckStatus.Error"/> or a
    /// <see cref="CheckStatus.Skip"/> says nothing about the site, so it is neither a change nor
    /// remembered: the history keeps the last real finding. Otherwise one bad night for our tooling
    /// on a failing site would send two false alerts, "better" going to Error and "worse" coming back.
    /// <para>
    /// A check with no history counts as having passed, so the first run reports everything already
    /// wrong. The alternative is that a problem present from day one is never reported at all.
    /// </para>
    /// <para>
    /// Staying the same is not a change, however the detail is worded: a site that stays broken
    /// is reported the day it breaks, not every day after.
    /// </para>
    /// </remarks>
    public static WatchComparison Of(IReadOnlyList<SiteReport> previous, IReadOnlyList<SiteReport> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var known = previous.ToDictionary(report => report.Site.Url.AbsoluteUri, StringComparer.Ordinal);
        var changes = new List<Change>();
        var history = new List<SiteReport>(current.Count);

        foreach (var report in current)
        {
            var lastResults = known.TryGetValue(report.Site.Url.AbsoluteUri, out var last)
                ? last.Results.ToDictionary(result => result.CheckName, StringComparer.Ordinal)
                : [];

            var remembered = new List<CheckResult>(report.Results.Count);

            foreach (var result in report.Results)
            {
                var before = lastResults.GetValueOrDefault(result.CheckName);

                if (!IsFinding(result.Status))
                {
                    // Keep the last real finding if there is one. If there is none, remember this
                    // anyway, so the history still lists every check the site has.
                    remembered.Add(before ?? result);
                    continue;
                }

                remembered.Add(result);

                var baseline = before is not null && IsFinding(before.Status) ? before.Status : (CheckStatus?)null;

                if (Change.Severity(result.Status) != Change.Severity(baseline ?? CheckStatus.Pass))
                {
                    changes.Add(new Change(report.Site, result.CheckName, baseline, result.Status, result.Detail));
                }
            }

            history.Add(report with { Results = remembered });
        }

        // Worse first: that is what the reader has to act on.
        var ordered = changes
            .OrderBy(change => change.Direction == ChangeDirection.Worse ? 0 : 1)
            .ThenByDescending(change => Change.Severity(change.After))
            .ToArray();

        return new WatchComparison(ordered, history);
    }

    private static bool IsFinding(CheckStatus status) => status is CheckStatus.Pass or CheckStatus.Warn or CheckStatus.Fail;
}
