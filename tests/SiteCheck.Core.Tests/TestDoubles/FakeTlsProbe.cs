using SiteCheck.Tls;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// Stands in for a server's answer to an old TLS hello, without opening a socket.
/// </summary>
internal sealed class FakeTlsProbe : ITlsProbe
{
    private readonly HashSet<LegacyTlsVersion> _accepted = [];
    private Exception? _failure;

    public List<(Uri Url, LegacyTlsVersion Version)> Asked { get; } = [];

    public static FakeTlsProbe Failing(Exception failure) => new() { _failure = failure };

    public FakeTlsProbe Accepting(params LegacyTlsVersion[] versions)
    {
        _accepted.UnionWith(versions);
        return this;
    }

    public Task<bool> AcceptsAsync(Uri url, LegacyTlsVersion version, CancellationToken cancellationToken = default)
    {
        Asked.Add((url, version));

        return _failure is null ? Task.FromResult(_accepted.Contains(version)) : throw _failure;
    }
}
