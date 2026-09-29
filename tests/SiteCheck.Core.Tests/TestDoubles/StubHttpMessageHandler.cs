using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// Answers HTTP requests from memory. A "slow" response is simulated by moving the
/// fake clock forward, so a four-second page costs the test suite nothing.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    private readonly ConcurrentQueue<string> _requests = [];

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Every request received, as <c>METHOD url</c>, in the order it arrived.</summary>
    public IReadOnlyCollection<string> Requests => _requests;

    public static StubHttpMessageHandler Responding(HttpStatusCode status, FakeTimeProvider clock, TimeSpan after) =>
        new(_ =>
        {
            clock.Advance(after);
            return new HttpResponseMessage(status) { Content = new StringContent("<html lang=\"en\"></html>") };
        });

    /// <summary>Serves exactly <paramref name="html"/>, for checks that read the page rather than time it.</summary>
    public static StubHttpMessageHandler Serving(string html, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(html) });

    /// <summary>Answers each request with whatever <paramref name="route"/> decides, for checks that follow links.</summary>
    public static StubHttpMessageHandler Routing(Func<HttpRequestMessage, HttpResponseMessage> route) => new(route);

    public static StubHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue($"{request.Method} {request.RequestUri}");

        var response = _respond(request);

        // What a real handler does, and what lets a check see the address a redirect ended at.
        response.RequestMessage ??= request;

        return Task.FromResult(response);
    }
}
