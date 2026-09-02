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

    public static FakeCertificateProvider Presenting(
        X509Certificate2 certificate,
        SslPolicyErrors policyErrors = SslPolicyErrors.None) =>
        new(new CertificateInfo(certificate, policyErrors));

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
