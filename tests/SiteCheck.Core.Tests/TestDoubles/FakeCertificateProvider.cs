using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using SiteCheck.Certificates;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// Hands the check a certificate without opening a connection.
/// </summary>
internal sealed class FakeCertificateProvider : ICertificateProvider
{
    private readonly CertificateInfo? _info;

    private FakeCertificateProvider(CertificateInfo? info) => _info = info;

    public int Invocations { get; private set; }

    /// <summary>
    /// Presents <paramref name="certificate"/> with <paramref name="policyErrors"/>, refusing
    /// combinations no real handshake produces.
    /// </summary>
    /// <remarks>
    /// A certificate outside its validity window always arrives as
    /// <see cref="SslPolicyErrors.RemoteCertificateChainErrors"/> — the TLS stack has no
    /// dedicated flag for expiry. Letting a test pair expired dates with
    /// <see cref="SslPolicyErrors.None"/> is what allowed a green suite to sit on top of
    /// https://github.com/joanp6/sitecheck/issues/1: every test described a handshake that
    /// cannot happen, so none of them exercised the one that does.
    /// <para>
    /// The reverse is not constrained, because it is real: a certificate can be well within
    /// its dates and still be self-signed or issued by an unknown root.
    /// </para>
    /// </remarks>
    public static FakeCertificateProvider Presenting(
        X509Certificate2 certificate,
        TimeProvider clock,
        SslPolicyErrors policyErrors = SslPolicyErrors.None)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow().UtcDateTime;
        var outsideValidity = now < certificate.NotBefore.ToUniversalTime()
                              || now >= certificate.NotAfter.ToUniversalTime();

        if (outsideValidity && !policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            throw new InvalidOperationException(
                $"This describes a handshake that cannot happen: the certificate is outside its " +
                $"validity window at {now:yyyy-MM-dd}, which always reaches us as " +
                $"{nameof(SslPolicyErrors.RemoteCertificateChainErrors)}, but the test asked for " +
                $"'{policyErrors}'. Pair the dates with that flag, or use dates inside the window.");
        }

        return new FakeCertificateProvider(new CertificateInfo(certificate, policyErrors));
    }

    /// <summary>A provider that fails loudly if the check tries to connect at all.</summary>
    public static FakeCertificateProvider NeverCalled() => new(info: null);

    public Task<CertificateInfo> GetAsync(Uri url, CancellationToken cancellationToken = default)
    {
        Invocations++;

        return _info is null
            ? throw new InvalidOperationException("The check should not have asked for a certificate.")
            : Task.FromResult(_info);
    }
}
