using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// Answers HTTP requests from memory. A "slow" response is simulated by moving the
/// fake clock forward, so a four-second page costs the test suite nothing.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _respond;

    private StubHttpMessageHandler(Func<HttpResponseMessage> respond) => _respond = respond;

    public static StubHttpMessageHandler Responding(HttpStatusCode status, FakeTimeProvider clock, TimeSpan after) =>
        new(() =>
        {
            clock.Advance(after);
            return new HttpResponseMessage(status) { Content = new StringContent("<html lang=\"en\"></html>") };
        });

    public static StubHttpMessageHandler Throwing(Exception exception) => new(() => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_respond());
}
