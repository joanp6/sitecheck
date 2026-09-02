using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SiteCheck.Certificates;

/// <summary>
/// The TLS certificate a host presented, together with the validation errors the
/// handshake reported for it.
/// </summary>
/// <remarks>
/// Carrying <paramref name="PolicyErrors"/> lets the check report an untrusted,
/// self-signed or wrong-host certificate without reimplementing chain validation.
/// </remarks>
/// <param name="Certificate">The server certificate. The caller owns it and must dispose it.</param>
/// <param name="PolicyErrors">What the TLS stack objected to, if anything.</param>
public sealed record CertificateInfo(X509Certificate2 Certificate, SslPolicyErrors PolicyErrors);
