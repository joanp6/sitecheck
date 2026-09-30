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

    public string DisplayName => Name ?? Url.Host;
}
