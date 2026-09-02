using SiteCheck.Checks;

namespace SiteCheck.Running;

/// <summary>
/// A check's verdict as recorded by <see cref="CheckRunner"/>: the outcome the
/// check produced, plus the name and timing the runner attaches to it.
/// </summary>
/// <param name="CheckName">The <see cref="ISiteCheck.Name"/> that produced this result.</param>
/// <param name="Status">The verdict.</param>
/// <param name="Detail">A sentence a non-technical site owner can act on.</param>
/// <param name="Duration">How long the check took, measured by the runner.</param>
public sealed record CheckResult(string CheckName, CheckStatus Status, string Detail, TimeSpan Duration);
