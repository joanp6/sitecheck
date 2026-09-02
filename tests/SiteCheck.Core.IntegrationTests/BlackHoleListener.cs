using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SiteCheck.Core.IntegrationTests;

/// <summary>
/// A local listener that completes the TCP handshake and then says nothing at all,
/// leaving a TLS client waiting for a ServerHello that never arrives.
/// </summary>
/// <remarks>
/// Models a host that is up but wedged. Local on purpose: there is no public host
/// that reliably behaves this way, and a test for hanging connections must not
/// itself depend on someone else's outage.
/// </remarks>
internal sealed class BlackHoleListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentBag<TcpClient> _accepted = [];
    private readonly CancellationTokenSource _stopping = new();

    public BlackHoleListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, port: 0);
        _listener.Start();

        Url = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        _ = AcceptAndIgnoreAsync();
    }

    /// <summary>The address of the listener, on an ephemeral port chosen by the OS.</summary>
    public Uri Url { get; }

    public void Dispose()
    {
        _stopping.Cancel();

        foreach (var client in _accepted)
        {
            client.Dispose();
        }

        _listener.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptAndIgnoreAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // Accepted, held open, and never read from or written to.
                _accepted.Add(await _listener.AcceptTcpClientAsync(_stopping.Token));
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
