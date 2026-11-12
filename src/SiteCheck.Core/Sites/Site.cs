namespace SiteCheck.Sites;

/// <summary>
/// A website to audit, and what to call it in a report.
/// </summary>
/// <param name="Url">The page the checks run against.</param>
/// <param name="Name">
/// What the owner calls it, such as "Perruqueria Angela". Optional: a report falls back on the
/// host name, which is what the owner would have typed anyway.
/// </param>
public sealed record Site(Uri Url, string? Name = null)
{
    public Uri Url { get; } = Url ?? throw new ArgumentNullException(nameof(Url));

    public string? Name { get; } = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    /// <summary>
    /// Checks not to run against this site, by <see cref="Checks.ISiteCheck.Name"/>.
    /// </summary>
    /// <remarks>
    /// For the finding that is known and accepted: the shop that takes bookings by phone on
    /// purpose, the host whose headers the owner cannot change. Left in, it would warn every
    /// day, and a report with a warning nobody means to fix is a report people stop reading.
    /// A skipped check is absent from the report, as if it did not exist for this site.
    /// </remarks>
    public IReadOnlySet<string> Skip { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string DisplayName => Name ?? Url.Host;

    // Written out because a record compares a collection by reference, and two sites that skip
    // the same checks are the same site.
    public bool Equals(Site? other) =>
        other is not null && Url == other.Url && Name == other.Name && Skip.SetEquals(other.Skip);

    public override int GetHashCode() => HashCode.Combine(Url, Name, Skip.Count);
}
