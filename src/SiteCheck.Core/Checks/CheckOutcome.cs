namespace SiteCheck.Checks;

/// <summary>
/// What a check concluded about a site, and why.
/// </summary>
/// <remarks>
/// Deliberately carries no timing: how long a check took is measured by
/// <see cref="Running.CheckRunner"/>, so that a new check cannot forget to do it
/// or report it inconsistently.
/// </remarks>
/// <param name="Status">The verdict.</param>
/// <param name="Detail">
/// A sentence a non-technical site owner can act on.
/// <para>
/// This is prose, written for a person, and it is free to be reworded at any time — a CSV
/// column, the weekly <c>watch</c> email, a line read out to a customer. Nothing may parse
/// it back apart. Anything a machine needs is carried as a value beside it, such as
/// <see cref="ValidUntil"/>; if a future check has a fact worth reporting, it gets a field
/// rather than a better-formatted sentence.
/// </para>
/// </param>
public sealed record CheckOutcome(CheckStatus Status, string Detail)
{
    /// <summary>
    /// When the thing this check watches stops being valid, for checks that watch something
    /// with an expiry date. <see langword="null"/> when the question does not apply.
    /// </summary>
    /// <remarks>
    /// Always an absolute instant, never a rendered date, so that a report can format it in
    /// whatever timezone its reader lives in without anyone re-parsing <see cref="Detail"/>.
    /// </remarks>
    public DateTimeOffset? ValidUntil { get; init; }

    public static CheckOutcome Pass(string detail) => new(CheckStatus.Pass, detail);

    public static CheckOutcome Warn(string detail) => new(CheckStatus.Warn, detail);

    public static CheckOutcome Fail(string detail) => new(CheckStatus.Fail, detail);

    public static CheckOutcome Error(string detail) => new(CheckStatus.Error, detail);
}
