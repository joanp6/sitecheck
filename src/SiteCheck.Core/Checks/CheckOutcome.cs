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
/// <param name="Detail">A sentence a non-technical site owner can act on.</param>
public sealed record CheckOutcome(CheckStatus Status, string Detail)
{
    public static CheckOutcome Pass(string detail) => new(CheckStatus.Pass, detail);

    public static CheckOutcome Warn(string detail) => new(CheckStatus.Warn, detail);

    public static CheckOutcome Fail(string detail) => new(CheckStatus.Fail, detail);

    public static CheckOutcome Error(string detail) => new(CheckStatus.Error, detail);
}
