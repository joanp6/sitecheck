using System.Net;
using System.Net.Sockets;

namespace SiteCheck.Http;

/// <summary>
/// sitecheck refused to open a connection because the address is not on the public internet.
/// </summary>
/// <remarks>
/// An <see cref="HttpRequestException"/> so that checks which already report "the page could not
/// be read" report this the same way, with the message saying why.
/// </remarks>
public sealed class PrivateAddressException(string message) : RefusedException(message);

/// <summary>
/// Opens connections only to addresses on the public internet.
/// </summary>
/// <remarks>
/// <para>
/// sitecheck follows whatever a page tells it to: redirects, links, images, form targets. A page
/// can therefore point it at <c>http://localhost:8080/admin</c>, at a router on
/// <c>192.168.1.1</c>, or at <c>169.254.169.254</c>, where cloud machines keep their credentials,
/// and learn from the report whether something answered. That is server-side request forgery
/// (SSRF), and it matters most where sitecheck runs unattended: on a CI runner, or called by
/// another program with addresses nobody read first.
/// </para>
/// <para>
/// The check happens at the moment of connecting, on the address actually dialled, not on the
/// name beforehand. Checking the name first and connecting afterwards would resolve it twice, and
/// a DNS server can answer a public address the first time and a private one the second.
/// It also means redirects are covered without any code for them: each hop opens a connection,
/// and each connection passes through here.
/// </para>
/// <para>
/// Behind an HTTP proxy the connection goes to the proxy, so it is the proxy's address that is
/// judged, and what the proxy then reaches is out of sitecheck's hands.
/// </para>
/// </remarks>
public static class PublicNetworkGuard
{
    /// <summary>For <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowPrivate: false, cancellationToken)
            .ConfigureAwait(false);

        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// Connects to <paramref name="host"/>, trying each of its addresses in turn.
    /// </summary>
    /// <exception cref="PrivateAddressException">None of the host's addresses is public, and private ones were not allowed.</exception>
    /// <exception cref="SocketException">The name does not resolve, or nothing answered.</exception>
    public static async Task<Socket> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // A Uri hands an IPv6 literal over in brackets.
        var literal = host.Trim('[', ']');

        var resolved = IPAddress.TryParse(literal, out var address)
            ? [address]
            // Spelled out: inside this assembly "Dns" alone is the SiteCheck.Dns namespace.
            : await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        var allowed = Allowed(host, resolved, allowPrivate);
        SocketException? last = null;

        foreach (var candidate in allowed)
        {
            var socket = new Socket(candidate.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(candidate, port), cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException ex)
            {
                // A host with several addresses is up if any of them answers.
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The addresses of <paramref name="host"/> that may be dialled.</summary>
    /// <exception cref="PrivateAddressException">There are addresses, and none may be dialled.</exception>
    public static IReadOnlyList<IPAddress> Allowed(string host, IReadOnlyList<IPAddress> resolved, bool allowPrivate)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (allowPrivate)
        {
            return resolved;
        }

        IPAddress[] open = [.. resolved.Where(IsPublic)];

        return open.Length > 0 || resolved.Count == 0
            ? open
            : throw new PrivateAddressException(
                $"{host} is at {resolved[0]}, an address on a private network or this machine, which sitecheck does not open "
                + "because a page could use it to probe what is behind your firewall. Pass --allow-private to audit a site on your own network.");
    }

    /// <summary>
    /// Whether <paramref name="address"/> is one any machine on the internet could reach, as
    /// opposed to this machine, its local network, or a range set aside for special use.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:10.0.0.1 is 10.0.0.1 in an IPv6 coat, and would otherwise walk past the IPv4 rules.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.IPv6None)
                || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal
                || address.IsIPv6Multicast);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);

        // The special-use ranges of RFC 6890 that lead somewhere other than the internet.
        return (b[0], b[1]) switch
        {
            (0, _) => false,                      // "this network"
            (10, _) => false,                     // private
            (100, >= 64 and <= 127) => false,     // carrier-grade NAT
            (127, _) => false,                    // loopback
            (169, 254) => false,                  // link-local, and cloud metadata at 169.254.169.254
            (172, >= 16 and <= 31) => false,      // private
            (192, 0) when b[2] == 0 => false,     // protocol assignments
            (192, 168) => false,                  // private
            (198, 18 or 19) => false,             // benchmarking
            ( >= 224, _) => false,                // multicast, reserved, broadcast
            _ => true,
        };
    }
}
