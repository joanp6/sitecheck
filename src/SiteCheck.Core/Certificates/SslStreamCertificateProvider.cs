using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace SiteCheck.Certificates;

/// <summary>
/// Opens a TLS connection and captures the certificate the server presents.
/// </summary>
/// <remarks>
/// Intentionally free of decision-making: this is the I/O adapter that the
/// <see cref="ICertificateProvider"/> seam exists to isolate. It has no unit tests
/// because there is no logic here to test without a real socket.
/// </remarks>
public sealed class SslStreamCertificateProvider : ICertificateProvider
{
    public async Task<CertificateInfo> GetAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(url.Host, url.Port, cancellationToken).ConfigureAwait(false);

        X509Certificate2? certificate = null;
        var policyErrors = SslPolicyErrors.None;

        using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, presented, _, errors) =>
            {
                // Accept whatever arrives. An expired or untrusted certificate is the
                // finding we are here to report, not a reason to abort the handshake.
                certificate = presented is null
                    ? null
                    : X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
                policyErrors = errors;
                return true;
            });

        await sslStream
            .AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = url.Host }, cancellationToken)
            .ConfigureAwait(false);

        return certificate is null
            ? throw new InvalidOperationException($"{url.Host} completed the TLS handshake without presenting a certificate.")
            : new CertificateInfo(certificate, policyErrors);
    }
}
