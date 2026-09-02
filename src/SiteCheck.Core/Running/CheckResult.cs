using SiteCheck.Checks;

namespace SiteCheck.Running;

/// <summary>
/// A check's verdict as recorded by <see cref="CheckRunner"/>: the outcome the
/// check produced, plus the name and timing the runner attaches to it.
/// </summary>
/// <param name="CheckName">The <see cref="ISiteCheck.Name"/> that produced this result.</param>
/// <param name="Status">The verdict.</param>
/// <param name="Detail">
/// Prose for a person to read. See <see cref="CheckOutcome.Detail"/>: it may be reworded at
/// any time, so nothing downstream may parse a fact back out of it.
/// </param>
/// <param name="Duration">How long the check took, measured by the runner.</param>
/// <param name="ValidUntil">
/// When the thing the check watches expires, or <see langword="null"/> if it does not expire.
/// This is the machine-readable half of what <paramref name="Detail"/> says in words.
/// </param>
public sealed record CheckResult(
    string CheckName,
    CheckStatus Status,
    string Detail,
    TimeSpan Duration,
    DateTimeOffset? ValidUntil = null);
