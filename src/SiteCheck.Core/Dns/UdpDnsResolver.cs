using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SiteCheck.Dns;

/// <summary>
/// Asks the DNS servers this machine is configured with, over UDP, and over TCP when an answer
/// does not fit.
/// </summary>
/// <remarks>
/// Written here rather than taken from a package because two record types do not justify a
/// dependency, and rather than asked of a DNS-over-HTTPS service because that would send every
/// audited domain to a third party. The system's own resolvers are also the ones that work behind
/// a network that blocks outside DNS, as some company and hotel networks do.
/// <para>
/// Each server is tried in turn. A server that does not answer in time, refuses, or fails
/// (SERVFAIL) passes the question to the next; only when none answers is it a
/// <see cref="DnsLookupException"/>.
/// </para>
/// </remarks>
public sealed class UdpDnsResolver : IDnsResolver
{
    private const int MaxServers = 3;

    private readonly IReadOnlyList<IPEndPoint>? _servers;
    private readonly TimeSpan _timeout;

    /// <param name="servers">The servers to ask; the machine's own when <see langword="null"/>.</param>
    /// <param name="timeout">How long to wait for each server.</param>
    public UdpDnsResolver(IReadOnlyList<IPEndPoint>? servers = null, TimeSpan? timeout = null)
    {
        _servers = servers;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<DnsAnswer> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var servers = _servers ?? SystemServers();

        if (servers.Count == 0)
        {
            throw new DnsLookupException("This machine has no DNS server configured.");
        }

        var failures = new List<string>();

        foreach (var server in servers.Take(MaxServers))
        {
            var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            var query = DnsMessage.EncodeQuery(id, name, type);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                var response = await AskUdpAsync(server, query, id, type, timeout.Token).ConfigureAwait(false);

                if (response.Truncated)
                {
                    response = await AskTcpAsync(server, query, id, type, timeout.Token).ConfigureAwait(false);
                }

                switch (response.ResponseCode)
                {
                    case DnsMessage.ResponseCodeNoError:
                        return new DnsAnswer(NameExists: true, response.Records);
                    case DnsMessage.ResponseCodeNameError:
                        return new DnsAnswer(NameExists: false, []);
                    default:
                        failures.Add($"{server.Address} failed (code {response.ResponseCode})");
                        break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{server.Address} did not answer within {_timeout.TotalSeconds:0.#} s");
            }
            catch (Exception ex) when (ex is SocketException or IOException or FormatException)
            {
                failures.Add($"{server.Address}: {ex.Message}");
            }
        }

        throw new DnsLookupException($"No DNS server answered for {name}: {string.Join("; ", failures)}.");
    }

    private static async Task<DnsResponse> AskUdpAsync(IPEndPoint server, byte[] query, ushort id, DnsRecordType type, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(server.AddressFamily);

        await udp.SendAsync(query, server, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            // Anything not from the server, or not answering this question, is ignored: it is
            // either late, or someone trying to slip in a forged answer.
            if (!received.RemoteEndPoint.Equals(server) || received.Buffer.Length < 2
                || BinaryPrimitives.ReadUInt16BigEndian(received.Buffer) != id)
            {
                continue;
            }

            return DnsMessage.Decode(received.Buffer, type);
        }
    }

    private static async Task<DnsResponse> AskTcpAsync(IPEndPoint server, byte[] query, ushort id, DnsRecordType type, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, cancellationToken).ConfigureAwait(false);

        var stream = tcp.GetStream();

        // Over TCP each message is preceded by its length (RFC 1035, 4.2.2).
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);

        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);

        var answer = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(answer, cancellationToken).ConfigureAwait(false);

        var response = DnsMessage.Decode(answer, type);

        return response.Id == id
            ? response
            : throw new FormatException("The DNS server answered a different question over TCP.");
    }

    /// <summary>The DNS servers of the network interfaces that are up, IPv4 first.</summary>
    private static List<IPEndPoint> SystemServers() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().DnsAddresses)

            // Windows lists fec0:0:0:ffff::1-3 as placeholders on machines without IPv6 DNS;
            // nothing answers there, and waiting on them would cost every lookup seconds.
            .Where(address => !address.IsIPv6SiteLocal)
            .Distinct()
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(address => new IPEndPoint(address, 53))];
}
