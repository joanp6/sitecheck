namespace SiteCheck.Checks;

/// <summary>
/// A single, self-contained audit of a website.
/// </summary>
/// <remarks>
/// Adding a check to the tool means writing one implementation of this interface
/// and its tests. Nothing else in the library needs to change:
/// <see cref="Running.CheckRunner"/> discovers checks through this contract alone.
/// </remarks>
public interface ISiteCheck
{
    /// <summary>
    /// Stable identifier used in reports, for example <c>ssl-certificate</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluates <paramref name="url"/> and reports what was found.
    /// </summary>
    Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default);
}
