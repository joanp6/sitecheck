using System.Collections.Concurrent;
using SiteCheck.Dns;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// Answers DNS questions from a table, so a check can be tested without a network.
/// </summary>
/// <remarks>
/// A name in the table exists and has the records listed for it, of either type. A name not in
/// the table does not exist (NXDOMAIN), which is how a real DNS says "no DMARC record".
/// </remarks>
internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Dictionary<(string Name, DnsRecordType Type), string[]> _records = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Exception? _failure;

    public ConcurrentQueue<string> Questions { get; } = [];

    public static FakeDnsResolver Failing(Exception failure) => new() { _failure = failure };

    public FakeDnsResolver With(string name, DnsRecordType type, params string[] records)
    {
        _names.Add(name);
        _records[(name.ToLowerInvariant(), type)] = records;
        return this;
    }

    public FakeDnsResolver Mx(string name, params string[] servers) => With(name, DnsRecordType.Mx, servers);

    public FakeDnsResolver Txt(string name, params string[] records) => With(name, DnsRecordType.Txt, records);

    public Task<DnsAnswer> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Questions.Enqueue($"{type} {name}");

        if (_failure is not null)
        {
            throw _failure;
        }

        return Task.FromResult(_names.Contains(name)
            ? new DnsAnswer(true, _records.GetValueOrDefault((name.ToLowerInvariant(), type), []))
            : new DnsAnswer(false, []));
    }
}
