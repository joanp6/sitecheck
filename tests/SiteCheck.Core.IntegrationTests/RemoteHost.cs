using System.Net.Sockets;

namespace SiteCheck.Core.IntegrationTests;

/// <summary>
/// Preflight for the third-party hosts these tests borrow.
/// </summary>
/// <remarks>
/// Turns "badssl.com is down today" into a skip with a sentence, instead of a
/// <see cref="SocketException"/> stack trace that reads like a defect in our code.
/// </remarks>
internal static class RemoteHost
{
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(5);

    public static async Task RequireReachableAsync(string host, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PreflightTimeout);

        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(host, 443, deadline.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            Assert.Skip(
                $"{host}:443 is not reachable from this machine ({Describe(ex)}). These tests borrow " +
                "third-party hosts and need outbound internet access; see docs/testing.md.");
        }
    }

    private static string Describe(Exception ex) => ex is SocketException socket
        ? socket.SocketErrorCode.ToString()
        : $"no answer within {PreflightTimeout.TotalSeconds:0} s";
}
