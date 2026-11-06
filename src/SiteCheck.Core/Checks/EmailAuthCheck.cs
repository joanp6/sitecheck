using SiteCheck.Dns;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether mail forged in the name of the site's domain would be stopped.
/// </summary>
/// <remarks>
/// Anyone can send an email whose From line says <c>info@the-business.es</c>. What stops it
/// arriving is two DNS records the domain publishes: SPF, which lists the servers allowed to send
/// its mail, and DMARC, which tells Gmail, Outlook and the rest what to do with mail that fails
/// the check. Without DMARC they deliver it, and the business's customers get a convincing "we
/// have changed our bank details" from an address they trust.
/// <list type="bullet">
/// <item><see cref="CheckStatus.Fail"/>: no DMARC record (or two, which receivers ignore), more
/// than one SPF record, which breaks SPF, or <c>+all</c>, which authorises the whole internet.</item>
/// <item><see cref="CheckStatus.Warn"/>: DMARC that only watches (<c>p=none</c>) or applies to
/// part of the mail (<c>pct</c> under 100), no policy, or no SPF.</item>
/// <item><see cref="CheckStatus.Pass"/>: DMARC that sends forgeries to spam or rejects them.
/// <c>~all</c> in SPF is fine then: DMARC decides, and it is what the big mail providers
/// recommend.</item>
/// <item><see cref="CheckStatus.Skip"/>: a domain with no mail at all (no MX, SPF or DMARC), or
/// an address on a hosting platform, whose mail settings are not the business's.</item>
/// </list>
/// <para>
/// Only the DNS is asked; the site is not touched. DKIM, the third piece, cannot be checked from
/// outside: its record lives under a name (the selector) only the sending service knows.
/// When the DNS cannot be asked, it is an <see cref="CheckStatus.Error"/>, never a finding.
/// </para>
/// </remarks>
public sealed class EmailAuthCheck : ISiteCheck
{
    // DMARCbis caps its walk at eight lookups; the domain's own is the first of them.
    private const int MaxTreeWalk = 7;

    private readonly IDnsResolver _dns;

    public EmailAuthCheck(IDnsResolver dns)
    {
        ArgumentNullException.ThrowIfNull(dns);

        _dns = dns;
    }

    public string Name => "email-auth";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.HostNameType is not UriHostNameType.Dns)
        {
            return CheckOutcome.Skip("The site is addressed by IP, so there is no domain whose mail could be forged.");
        }

        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();

        if (HostingPlatforms.Find(host) is { } platform)
        {
            return CheckOutcome.Skip(
                $"The site lives at an address on {platform}, which belongs to the hosting platform, so its mail settings are not the business's.");
        }

        // The mail domain is the site's without "www.": info@salon.es, not info@www.salon.es.
        var domain = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

        if (!domain.Contains('.', StringComparison.Ordinal))
        {
            return CheckOutcome.Skip($"{domain} is not a domain anyone sends mail from.");
        }

        string[] spf;
        DmarcPolicy dmarc;
        bool receivesMail;

        try
        {
            var mx = await _dns.QueryAsync(domain, DnsRecordType.Mx, cancellationToken).ConfigureAwait(false);
            var txt = await _dns.QueryAsync(domain, DnsRecordType.Txt, cancellationToken).ConfigureAwait(false);

            // A "null MX" (a single "."; RFC 7505) is how a domain says it receives no mail.
            receivesMail = mx.Records.Any(server => server.Length > 0);
            spf = [.. txt.Records.Where(IsSpf)];
            dmarc = await DmarcAsync(domain, cancellationToken).ConfigureAwait(false);
        }
        catch (DnsLookupException ex)
        {
            return CheckOutcome.Error($"The DNS could not be asked about {domain}'s mail settings: {ex.Message}");
        }

        if (!receivesMail && spf.Length == 0 && dmarc.Records.Length == 0)
        {
            return CheckOutcome.Skip(
                $"{domain} has no mail set up (no MX, SPF or DMARC record), so there is no mailbox for forged mail to imitate. If it should never send mail, \"v=spf1 -all\" and a DMARC record with \"p=reject\" make that explicit.");
        }

        return Evaluate(domain, spf, dmarc);
    }

    /// <summary>The DMARC records that govern a domain, and where they were found.</summary>
    /// <param name="Records">The DMARC records found, none, one, or (wrongly) several.</param>
    /// <param name="Source">The domain whose <c>_dmarc</c> name holds them.</param>
    /// <param name="Inherited">They belong to a parent domain, so its <c>sp=</c> applies.</param>
    private sealed record DmarcPolicy(string[] Records, string Source, bool Inherited);

    /// <summary>
    /// The DMARC records for <paramref name="domain"/>: its own, or else the nearest parent's,
    /// found by walking up the DNS tree one label at a time.
    /// </summary>
    /// <remarks>
    /// The walk is how DMARC's revision (DMARCbis) finds the policy of a subdomain, replacing the
    /// public suffix list RFC 7489 relied on: shop.eu.salon.es with no record of its own falls
    /// under eu.salon.es if that has one, else salon.es, and so on up. It needs no list of
    /// suffixes, so co.uk and com.es need no special case. Receivers still on RFC 7489 differ
    /// only when an intermediate subdomain publishes a record of its own, which is rare. Like
    /// DMARCbis, the walk is capped, so a very long name costs at most eight lookups.
    /// </remarks>
    private async Task<DmarcPolicy> DmarcAsync(string domain, CancellationToken cancellationToken)
    {
        var own = await _dns.QueryAsync("_dmarc." + domain, DnsRecordType.Txt, cancellationToken).ConfigureAwait(false);
        var records = own.Records.Where(IsDmarc).ToArray();

        if (records.Length > 0)
        {
            return new DmarcPolicy(records, domain, Inherited: false);
        }

        var labels = domain.Split('.');

        for (var skip = 1; skip < labels.Length && skip <= MaxTreeWalk; skip++)
        {
            var parent = string.Join('.', labels[skip..]);
            var answer = await _dns.QueryAsync("_dmarc." + parent, DnsRecordType.Txt, cancellationToken).ConfigureAwait(false);
            var found = answer.Records.Where(IsDmarc).ToArray();

            if (found.Length > 0)
            {
                return new DmarcPolicy(found, parent, Inherited: true);
            }
        }

        return new DmarcPolicy([], domain, Inherited: false);
    }

    private static CheckOutcome Evaluate(string domain, string[] spf, DmarcPolicy dmarc)
    {
        var fails = new List<string>();
        var warns = new List<string>();

        // A record inherited from a parent says what to do with its subdomains in sp=, and
        // falls back to p= only when sp= is absent (RFC 7489, 6.3).
        var tag = dmarc.Inherited && dmarc.Records.Length == 1 && Tags(dmarc.Records[0]).ContainsKey("sp") ? "sp" : "p";
        var whose = dmarc.Inherited ? $"The DMARC record {domain} inherits from {dmarc.Source}" : $"{domain}'s DMARC record";
        var policy = dmarc.Records.Length == 1 ? Tags(dmarc.Records[0]).GetValueOrDefault(tag)?.ToLowerInvariant() : null;

        if (dmarc.Records.Length == 0)
        {
            fails.Add($"{domain} has no DMARC record, so Gmail, Outlook and other providers deliver mail forged in its name as if it were real. Anyone can email the business's customers from an address that looks like its own.");
        }
        else if (dmarc.Records.Length > 1)
        {
            fails.Add($"{dmarc.Source} has {dmarc.Records.Length} DMARC records; receivers ignore DMARC when there is more than one, so forged mail is delivered. Keep one.");
        }
        else
        {
            switch (policy)
            {
                case "reject" or "quarantine":
                    if (Tags(dmarc.Records[0]).TryGetValue("pct", out var pct) && int.TryParse(pct, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent is >= 0 and < 100)
                    {
                        warns.Add($"{whose} applies its policy ({tag}={policy}) to only {percent} % of forged mail (pct={percent}); the rest is delivered. Remove pct or set it to 100.");
                    }

                    break;
                case "none":
                    warns.Add($"{whose} says {tag}=none, which only reports forged mail and still delivers it. Once the reports show the real mail passing, change it to {tag}=quarantine or {tag}=reject.");
                    break;
                default:
                    warns.Add($"{whose} does not say what to do with forged mail (no valid {tag}=), so receivers treat it as none and deliver it.");
                    break;
            }
        }

        if (spf.Length > 1)
        {
            fails.Add($"{domain} has {spf.Length} SPF records; receivers treat that as an error, so SPF fails for all of its mail and the business's own messages are more likely to land in spam. Merge them into one.");
        }
        else if (spf.Length == 0)
        {
            warns.Add($"{domain} has no SPF record listing the servers allowed to send its mail, which makes its own messages more likely to land in spam.");
        }
        else if (AllQualifier(spf[0]) == '+')
        {
            fails.Add($"{domain}'s SPF record ends in +all, which authorises every server on the internet to send its mail. Use -all or ~all.");
        }

        var dkim = " DKIM, the third piece, cannot be checked from outside.";

        if (fails.Count > 0)
        {
            return CheckOutcome.Fail(string.Join(" ", fails.Concat(warns)) + dkim);
        }

        if (warns.Count > 0)
        {
            return CheckOutcome.Warn(string.Join(" ", warns) + dkim);
        }

        var outcome = policy == "reject" ? "rejected" : "sent to spam";
        var source = dmarc.Inherited ? $", inherited from {dmarc.Source}" : string.Empty;

        return CheckOutcome.Pass($"Mail forged in {domain}'s name is {outcome}: it publishes SPF and an enforced DMARC policy{source}.{dkim}");
    }

    private static bool IsSpf(string record) => StartsWithVersion(record, "v=spf1");

    private static bool IsDmarc(string record) => StartsWithVersion(record, "v=DMARC1");

    // "v=spf1" must be followed by a space or nothing: "v=spf10" is not SPF.
    private static bool StartsWithVersion(string record, string version)
    {
        var trimmed = record.TrimStart();

        return trimmed.StartsWith(version, StringComparison.OrdinalIgnoreCase)
               && (trimmed.Length == version.Length || trimmed[version.Length] is ' ' or ';');
    }

    /// <summary>The qualifier of the "all" mechanism (+, -, ~ or ?), or <see langword="null"/> when there is none.</summary>
    private static char? AllQualifier(string spf)
    {
        foreach (var term in spf.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return '+'; // no qualifier means pass
            }

            if (term.Length == 4 && term[0] is '+' or '-' or '~' or '?' && term[1..].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return term[0];
            }
        }

        return null;
    }

    private static Dictionary<string, string> Tags(string record) =>
        record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.OrdinalIgnoreCase);
}
