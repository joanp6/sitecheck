using SiteCheck.Sites;

namespace SiteCheck.Running;

/// <summary>
/// Audits several sites at once, without ever auditing one server twice at the same time.
/// </summary>
/// <remarks>
/// <para>
/// One after another, thirty sites take thirty times as long as one, and nearly all of that is
/// spent waiting for other people's servers to answer. Different sites share nothing, so the
/// waiting can overlap.
/// </para>
/// <para>
/// Within a site the checks still run in order, for the reason <see cref="CheckRunner"/> gives:
/// one of them times the page, and its own neighbours hitting the same server would slow the
/// answer it measures. The same holds for two addresses on one host (<c>shop.test/</c> and
/// <c>shop.test/menu</c>), so sites are grouped by host and a group is audited in order, by one
/// worker. Only different hosts overlap.
/// </para>
/// <para>
/// Reports come back in the order the sites were given, whichever finished first: a report that
/// reshuffled itself on every run could not be compared with yesterday's by eye.
/// </para>
/// </remarks>
public static class SiteBatch
{
    /// <summary>
    /// Enough to make a long list quick, few enough that a home connection or a small CI runner
    /// does not become the thing being measured.
    /// </summary>
    public const int DefaultConcurrency = 4;

    /// <param name="sites">The sites, in the order their reports should come back.</param>
    /// <param name="audit">Audits one site. Called for several sites at once, never for two on the same host.</param>
    /// <param name="concurrency">How many hosts are audited at the same time. 1 audits them one after another.</param>
    /// <param name="cancellationToken">Stops the audits in flight and starts no more.</param>
    public static async Task<IReadOnlyList<SiteReport>> AuditAsync(
        IReadOnlyList<Site> sites,
        Func<Site, CancellationToken, Task<SiteReport>> audit,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        var reports = new SiteReport[sites.Count];

        // One group per server, however its name is written.
        var hosts = sites
            .Select((site, index) => (Site: site, Index: index))
            .GroupBy(entry => HostKey(entry.Site.Url), StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (host, token) =>
            {
                foreach (var (site, index) in host)
                {
                    // Each worker writes only its own slots, so the array needs no lock.
                    reports[index] = await audit(site, token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        return reports;
    }

    /// <summary>
    /// What makes two addresses the same server: the host, with or without <c>www.</c>, since
    /// both lead to one machine and a page list read from a sitemap may use either.
    /// </summary>
    private static string HostKey(Uri address) =>
        address.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address.IdnHost[4..] : address.IdnHost;
}
