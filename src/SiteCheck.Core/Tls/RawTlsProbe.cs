using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using SiteCheck.Http;

namespace SiteCheck.Tls;

/// <summary>
/// Asks a server whether it still accepts an old version of TLS.
/// </summary>
/// <remarks>
/// The seam that keeps <see cref="Checks.TlsConfigCheck"/> testable: the check asks this, tests
/// hand it a fake, and only <see cref="RawTlsProbe"/> opens a socket.
/// </remarks>
public interface ITlsProbe
{
    /// <exception cref="SocketException">The host could not be reached at all.</exception>
    /// <exception cref="TimeoutException">The host accepted the connection and then said nothing.</exception>
    /// <exception cref="PrivateAddressException">The host is on a private network, and those were not allowed.</exception>
    Task<bool> AcceptsAsync(Uri url, LegacyTlsVersion version, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends one hand-written ClientHello and reads the start of the reply. See <see cref="TlsHello"/>
/// for why this does not go through the operating system's TLS.
/// </summary>
[ExcludeFromCodeCoverage(Justification =
    "The I/O adapter behind ITlsProbe, like SslStreamCertificateProvider: no decisions here, only a " +
    "socket. What it sends and how the reply is read live in TlsHello, which is unit-tested, and the " +
    "whole exchange is tested over real sockets by SiteCheck.Core.IntegrationTests.")]
public sealed class RawTlsProbe : ITlsProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly bool _allowPrivateAddresses;

    /// <param name="allowPrivateAddresses">
    /// Also connect to this machine and private networks. Off by default; see <see cref="PublicNetworkGuard"/>.
    /// </param>
    public RawTlsProbe(bool allowPrivateAddresses = false) => _allowPrivateAddresses = allowPrivateAddresses;

    public async Task<bool> AcceptsAsync(Uri url, LegacyTlsVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            using var socket = await PublicNetworkGuard
                .ConnectAsync(url.IdnHost, url.Port, _allowPrivateAddresses, deadline.Token)
                .ConfigureAwait(false);

            await socket.SendAsync(TlsHello.ClientHello(url.IdnHost, version), deadline.Token).ConfigureAwait(false);

            var reply = new byte[TlsHello.ReplyBytesNeeded];
            var total = 0;

            try
            {
                int read;

                // TCP hands bytes over in whatever pieces they arrived in.
                while (total < reply.Length
                       && (read = await socket.ReceiveAsync(reply.AsMemory(total), deadline.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                }
            }
            catch (SocketException)
            {
                // Hanging up on the hello is one of the ways a server says no.
            }

            return TlsHello.Accepts(reply.AsSpan(0, total), version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{url.Host} did not answer the TLS hello within {Timeout.TotalSeconds:0.##} s.");
        }
    }
}
