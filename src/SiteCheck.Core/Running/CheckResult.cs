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
/// <param name="Elapsed">
/// How long the site took to answer, when the check measured it; see <see cref="CheckOutcome.Elapsed"/>.
/// Unlike <paramref name="Duration"/>, this is a finding about the site, not about our tooling.
/// </param>
/// <param name="StatusCode">
/// The HTTP status the site answered with, when the check made a request and got a response.
/// </param>
/// <param name="Bytes">
/// How many bytes the check weighed, when it weighed something; see <see cref="CheckOutcome.Bytes"/>.
/// </param>
public sealed record CheckResult(
    string CheckName,
    CheckStatus Status,
    string Detail,
    TimeSpan Duration,
    DateTimeOffset? ValidUntil = null,
    TimeSpan? Elapsed = null,
    int? StatusCode = null,
    long? Bytes = null);
