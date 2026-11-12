using System.Net.Sockets;
using SiteCheck.Http;
using SiteCheck.Tls;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the server still accepts the retired versions of the encryption behind https.
/// </summary>
/// <remarks>
/// <para>
/// The "s" in https is a protocol called TLS, and it has versions. TLS 1.0 (1999) and 1.1 (2006)
/// have known weaknesses, were formally retired in 2021 (RFC 8996), and no current browser will
/// use them. A server that still accepts them is not harming today's visitors, who all arrive
/// with 1.2 or 1.3. It is leaving open a door only old or malicious software knocks on, it fails
/// the audit any card processor runs (PCI DSS forbids both), and it usually means nobody has
/// touched the server's configuration in years, which is the more useful thing to learn.
/// </para>
/// <para>
/// So: a <see cref="CheckStatus.Warn"/>, never a <see cref="CheckStatus.Fail"/>.
/// </para>
/// <para>
/// It asks by offering each old version, and only that version, in the opening message of a
/// handshake, and seeing whether the server agrees. Nothing is encrypted or exchanged beyond
/// that first reply. See <see cref="TlsHello"/> for why the question cannot be put through the
/// operating system. <c>ssl-certificate</c> looks at the certificate; this looks at the protocol
/// underneath it, and neither says anything about the other.
/// </para>
/// </remarks>
public sealed class TlsConfigCheck : ISiteCheck
{
    private readonly ITlsProbe _probe;

    public TlsConfigCheck(ITlsProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        _probe = probe;
    }

    public string Name => "tls-config";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Given an http:// address, the https site is the one with a TLS configuration to look at,
        // as in SslCertificateCheck.
        var secure = url.Scheme == Uri.UriSchemeHttps
            ? url
            : new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        bool tls10, tls11;

        try
        {
            tls10 = await _probe.AcceptsAsync(secure, LegacyTlsVersion.Tls10, cancellationToken).ConfigureAwait(false);
            tls11 = await _probe.AcceptsAsync(secure, LegacyTlsVersion.Tls11, cancellationToken).ConfigureAwait(false);
        }
        catch (PrivateAddressException ex)
        {
            return CheckOutcome.Error(ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
        {
            // ssl-certificate already reports a site with no https as the defect it is.
            return CheckOutcome.Error(
                $"{url.Host} could not be reached over https, so sitecheck could not see which versions of TLS it accepts.");
        }

        var accepted = (tls10, tls11) switch
        {
            (true, true) => "TLS 1.0 and TLS 1.1",
            (true, false) => "TLS 1.0",
            (false, true) => "TLS 1.1",
            _ => null,
        };

        return accepted is null
            ? CheckOutcome.Pass("The server refuses the retired versions of TLS (1.0 and 1.1) and only speaks the current ones.")
            : CheckOutcome.Warn(
                $"The server still accepts {accepted}, retired in 2021 for known weaknesses. No current browser uses them, so visitors are not affected, "
                + "but it fails the security review card processors require and suggests the server's configuration has not been looked at in years. "
                + "Ask whoever hosts the site to allow TLS 1.2 and 1.3 only.");
    }
}
