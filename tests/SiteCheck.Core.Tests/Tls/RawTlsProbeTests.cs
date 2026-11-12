using System.Net;
using System.Net.Sockets;
using SiteCheck.Http;
using SiteCheck.Tls;

namespace SiteCheck.Core.Tests.Tls;

/// <summary>
/// The probe over a real socket, against a pretend server on this machine that answers a hello
/// with bytes the test chose. Loopback only, so these need no network.
/// </summary>
public sealed class RawTlsProbeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Listens on a free local port and, for each connection, reads the hello and does what
    /// <paramref name="answer"/> says with the socket.
    /// </summary>
    private sealed class PretendServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();

        public PretendServer(Func<Socket, CancellationToken, Task> answer)
        {
            _listener.Start();
            Url = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _ = ServeAsync(answer);
        }

        public Uri Url { get; }

        /// <summary>The first bytes each client sent.</summary>
        public List<byte[]> Hellos { get; } = [];

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }

        private async Task ServeAsync(Func<Socket, CancellationToken, Task> answer)
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptSocketAsync(_stopping.Token);

                    var hello = new byte[1024];
                    var read = await client.ReceiveAsync(hello, _stopping.Token);

                    lock (Hellos)
                    {
                        Hellos.Add(hello[..read]);
                    }

                    await answer(client, _stopping.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The test is over.
            }
        }
    }

    private static Func<Socket, CancellationToken, Task> Sending(params byte[] reply) =>
        async (client, token) => await client.SendAsync(reply, token);

    private static byte[] ServerHello(byte minor) => [0x16, 0x03, minor, 0x00, 0x51, 0x02, 0x00, 0x00, 0x4D, 0x03, minor, 0xAA, 0xBB];

    private static RawTlsProbe Probe(double timeoutSeconds = 5) =>
        new(allowPrivateAddresses: true, TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task AcceptsAsync_WhenTheServerChoosesTheOldVersion_IsTrue_HavingOfferedExactlyThat()
    {
        using var server = new PretendServer(Sending(ServerHello(0x01)));

        Assert.True(await Probe().AcceptsAsync(server.Url, LegacyTlsVersion.Tls10, Token));

        // What went over the wire is a handshake record carrying a ClientHello for TLS 1.0.
        var hello = Assert.Single(server.Hellos);
        Assert.Equal(0x16, hello[0]);
        Assert.Equal(0x01, hello[5]);
        Assert.Equal([0x03, 0x01], hello[9..11]);
    }

    [Fact]
    public async Task AcceptsAsync_WhenTheReplyArrivesInPieces_StillReadsIt()
    {
        // TCP delivers bytes in whatever pieces it likes; one read is not "the reply".
        using var server = new PretendServer(async (client, token) =>
        {
            var reply = ServerHello(0x02);
            await client.SendAsync(reply.AsMemory(0, 4), token);
            await Task.Delay(50, token);
            await client.SendAsync(reply.AsMemory(4), token);
        });

        Assert.True(await Probe().AcceptsAsync(server.Url, LegacyTlsVersion.Tls11, Token));
    }

    [Fact]
    public async Task AcceptsAsync_WhenTheServerAnswersWithAnAlert_IsFalse()
    {
        using var server = new PretendServer(Sending(0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x46));

        Assert.False(await Probe().AcceptsAsync(server.Url, LegacyTlsVersion.Tls10, Token));
    }

    [Fact]
    public async Task AcceptsAsync_WhenTheServerHangsUpOnTheHello_IsFalse()
    {
        // Closing the connection is one of the ways a server says no.
        using var server = new PretendServer((_, _) => Task.CompletedTask);

        Assert.False(await Probe().AcceptsAsync(server.Url, LegacyTlsVersion.Tls10, Token));
    }

    [Fact]
    public async Task AcceptsAsync_WhenTheServerChoosesAnotherVersion_IsFalse()
    {
        using var server = new PretendServer(Sending(ServerHello(0x01)));

        Assert.False(await Probe().AcceptsAsync(server.Url, LegacyTlsVersion.Tls11, Token));
    }

    [Fact]
    public async Task AcceptsAsync_WhenTheServerGoesSilent_GivesUpOnItsOwnAndSaysItTimedOut()
    {
        using var server = new PretendServer((_, token) => Task.Delay(Timeout.Infinite, token));

        await Assert.ThrowsAsync<TimeoutException>(() => Probe(timeoutSeconds: 0.3).AcceptsAsync(server.Url, LegacyTlsVersion.Tls10, Token));
    }

    [Fact]
    public async Task AcceptsAsync_WhenNothingListens_ThrowsSocketException()
    {
        // Take a free port, then let it go: nothing is there to answer.
        int port;

        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await Assert.ThrowsAsync<SocketException>(
            () => Probe().AcceptsAsync(new Uri($"https://127.0.0.1:{port}/"), LegacyTlsVersion.Tls10, Token));
    }

    [Fact]
    public async Task AcceptsAsync_ByDefault_RefusesThisMachine()
    {
        using var server = new PretendServer(Sending(ServerHello(0x01)));

        await Assert.ThrowsAsync<PrivateAddressException>(() => new RawTlsProbe().AcceptsAsync(server.Url, LegacyTlsVersion.Tls10, Token));
        Assert.Empty(server.Hellos);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithATimeoutThatIsNotPositive_Throws(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawTlsProbe(timeout: TimeSpan.FromSeconds(seconds)));
    }
}
