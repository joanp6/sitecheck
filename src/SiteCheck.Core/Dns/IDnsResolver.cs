namespace SiteCheck.Dns;

/// <summary>The kinds of DNS record sitecheck asks for, by their number in the protocol.</summary>
public enum DnsRecordType
{
    /// <summary>Which servers receive the domain's mail.</summary>
    Mx = 15,

    /// <summary>Free text, where SPF and DMARC policies are published.</summary>
    Txt = 16,
}

/// <summary>What the DNS said about a name.</summary>
/// <param name="NameExists">
/// <see langword="false"/> when the DNS answered that the name does not exist (NXDOMAIN), which is
/// an answer, not a failure: it is how a domain says it has no DMARC record.
/// </param>
/// <param name="Records">
/// The records of the type asked for: a TXT record's text with its parts joined, as SPF and DMARC
/// read it, or an MX record's mail server name. Empty when the name has none.
/// </param>
public sealed record DnsAnswer(bool NameExists, IReadOnlyList<string> Records);

/// <summary>
/// Asks the DNS about a name.
/// </summary>
/// <remarks>
/// The seam that keeps checks testable: a check asks this, tests hand it a fake, and only
/// <see cref="UdpDnsResolver"/> touches the network.
/// </remarks>
public interface IDnsResolver
{
    /// <exception cref="DnsLookupException">No server gave an answer: unreachable, timed out, or failed.</exception>
    Task<DnsAnswer> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default);
}

/// <summary>The DNS could not be asked, as opposed to answering that a name has no records.</summary>
public sealed class DnsLookupException : Exception
{
    public DnsLookupException()
    {
    }

    public DnsLookupException(string message)
        : base(message)
    {
    }

    public DnsLookupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
