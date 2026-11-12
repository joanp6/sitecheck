using SiteCheck.Dns;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the site's other spelling, with or without <c>www.</c>, leads somewhere, and
/// that it does not lead to an address someone else could claim.
/// </summary>
/// <remarks>
/// <para>
/// People type <c>salon.es</c> and <c>www.salon.es</c> interchangeably, and both are printed on
/// business cards. They are two separate entries in the DNS, set up separately, and it is common
/// for one to be forgotten: the audited address works and the other shows an error. That is a
/// <see cref="CheckStatus.Warn"/>: the visitors who type the other one find nothing.
/// </para>
/// <para>
/// Worse is the entry that was set up and then orphaned. A name is often an alias (a CNAME) for
/// an address at a hosting company, <c>salon.herokuapp.com</c> say. When the account there is
/// closed and the alias left behind, it points at a name that no longer exists, and on many
/// platforms anyone may register that name. They then control what <c>www.salon.es</c> shows:
/// the business's own address, serving a stranger's page. It is called a subdomain takeover, and
/// an alias to a name that does not exist is a <see cref="CheckStatus.Fail"/>.
/// </para>
/// <para>
/// Only the DNS is asked, through this machine's own servers; the site is not touched. Only the
/// <c>www</c> twin is looked at: other forgotten names (<c>shop.</c>, <c>old.</c>) can dangle the
/// same way, but finding them means guessing names, which this does not do. A subdomain
/// (<c>booking.salon.es</c>) or an address on a hosting platform has no twin to check and skips.
/// </para>
/// </remarks>
public sealed class WwwAddressCheck : ISiteCheck
{
    /// <summary>Second-level labels that are part of the suffix, as in <c>salon.co.uk</c> or <c>salon.com.es</c>.</summary>
    private static readonly string[] SuffixLabels = ["co", "com", "org", "net", "gov", "edu", "ac", "gob", "nom"];

    private readonly IDnsResolver _dns;

    public WwwAddressCheck(IDnsResolver dns)
    {
        ArgumentNullException.ThrowIfNull(dns);

        _dns = dns;
    }

    public string Name => "www-address";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.HostNameType is not UriHostNameType.Dns)
        {
            return CheckOutcome.Skip("The site is addressed by IP, so it has no www spelling.");
        }

        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();

        if (HostingPlatforms.Find(host) is { } platform)
        {
            return CheckOutcome.Skip($"The site lives at an address on {platform}, which has no www spelling of its own.");
        }

        if (Twin(host) is not { } twin)
        {
            return CheckOutcome.Skip($"{host} is not a domain with a www spelling to check.");
        }

        try
        {
            return await EvaluateAsync(host, twin, cancellationToken).ConfigureAwait(false);
        }
        catch (DnsLookupException ex)
        {
            return CheckOutcome.Error($"The DNS could not be asked about {twin}: {ex.Message}");
        }
    }

    private async Task<CheckOutcome> EvaluateAsync(string host, string twin, CancellationToken cancellationToken)
    {
        var alias = await _dns.QueryAsync(twin, DnsRecordType.Cname, cancellationToken).ConfigureAwait(false);

        if (!alias.NameExists)
        {
            return LeadsNowhere(host, twin);
        }

        if (alias.Records.Count > 0)
        {
            var target = alias.Records[0].TrimEnd('.').ToLowerInvariant();
            var there = await _dns.QueryAsync(target, DnsRecordType.A, cancellationToken).ConfigureAwait(false);

            if (!there.NameExists)
            {
                var claimable = HostingPlatforms.Find(target) is { } platform
                    ? $"That name is on {platform}, where anyone can sign up and may be able to register it"
                    : "Whoever registers that name";

                return CheckOutcome.Fail(
                    $"{twin} is set up as another name for {target}, which no longer exists. {claimable}, and would then decide what "
                    + $"{twin} shows: your address, their page. Remove the {twin} entry from the DNS, or point it at the site.");
            }

            // The alias leads to a name that exists; whether a page is served there is not this check's question.
            return Works(host, twin);
        }

        var v4 = await _dns.QueryAsync(twin, DnsRecordType.A, cancellationToken).ConfigureAwait(false);

        if (v4.Records.Count > 0)
        {
            return Works(host, twin);
        }

        var v6 = await _dns.QueryAsync(twin, DnsRecordType.Aaaa, cancellationToken).ConfigureAwait(false);

        return v6.Records.Count > 0 ? Works(host, twin) : LeadsNowhere(host, twin);
    }

    private static CheckOutcome Works(string host, string twin) =>
        CheckOutcome.Pass($"{twin} leads somewhere too, so visitors reach the site whichever way they type {host}.");

    private static CheckOutcome LeadsNowhere(string host, string twin) =>
        CheckOutcome.Warn(
            $"{host} works, but {twin} leads nowhere, so visitors who type that, or follow an old link or a business card with it, get an error. "
            + $"Add {twin} where the domain is managed, pointing at the same site.");

    /// <summary>
    /// The other spelling of <paramref name="host"/>, or <see langword="null"/> when it is a
    /// subdomain, which has none.
    /// </summary>
    private static string? Twin(string host)
    {
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            return host[4..].Contains('.', StringComparison.Ordinal) ? host[4..] : null;
        }

        var labels = host.Split('.');

        // salon.es, or salon.co.uk: a registered name. Anything longer is a subdomain of one.
        var registered = labels.Length == 2
                         || (labels.Length == 3 && labels[2].Length == 2 && SuffixLabels.Contains(labels[1], StringComparer.Ordinal));

        return registered ? "www." + host : null;
    }
}
