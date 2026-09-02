using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// Builds real certificates in memory, so the certificate policy can be exercised
/// against genuine <see cref="X509Certificate2"/> instances without touching a network.
/// </summary>
internal static class TestCertificates
{
    /// <summary>
    /// Creates a self-signed certificate valid for the given window. ECDSA rather than
    /// RSA purely for speed: these are generated once per test case.
    /// </summary>
    public static X509Certificate2 ValidBetween(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=example.test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
