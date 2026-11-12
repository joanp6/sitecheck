using System.Collections.Concurrent;
using System.Net;

namespace SiteCheck.Http;

/// <summary>
/// Fetches a site's page once per audit and hands every check that asks for it the same answer.
/// </summary>
/// <remarks>
/// <para>
/// Most checks read the page's HTML, and each used to ask the site for it: thirteen requests for
/// one page within a few seconds. That is slow, it lets two checks grade two different versions of
/// a page that changed in between, and it is exactly the burst that makes a firewall start
/// answering 403, which then turns the rest of the report into skips.
/// </para>
/// <para>
/// It sits in the <see cref="HttpClient"/>'s pipeline rather than in the checks, so a check keeps
/// asking for the page as if it were alone and stays testable alone. Only a <c>GET</c> for an
/// address named with <see cref="Share"/> is kept; links, images and anything asked with
/// <c>HEAD</c> go to the network as before.
/// </para>
/// <para>
/// What the site answers is kept, refusals included: a 403 or a 404 is what every check would
/// have got. What may be different a moment later is not kept, so the next check asks again: a
/// request that fails outright (no connection, a timeout) and a server error (5xx).
/// </para>
/// <para>
/// <c>load-time</c> must not go through this: it measures a first visit, and a remembered page
/// arrives in no time at all.
/// </para>
/// </remarks>
public sealed class PageSnapshotHandler : DelegatingHandler
{
    /// <summary>
    /// 5 MB. Real pages' HTML is a few hundred kB at most; past this the address is serving
    /// something else, and reading on would only fill memory.
    /// </summary>
    public const long DefaultMaxPageBytes = 5 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Slot> _shared = new(StringComparer.Ordinal);
    private readonly long _maxPageBytes;

    public PageSnapshotHandler(HttpMessageHandler innerHandler, long maxPageBytes = DefaultMaxPageBytes)
        : base(innerHandler)
    {
        _maxPageBytes = maxPageBytes > 0
            ? maxPageBytes
            : throw new ArgumentOutOfRangeException(nameof(maxPageBytes), maxPageBytes, "A page has to be allowed at least one byte.");
    }

    /// <summary>
    /// From now until the result is disposed, the first <c>GET</c> for <paramref name="page"/> is
    /// remembered and answers the ones after it.
    /// </summary>
    /// <remarks>
    /// Sites audited in parallel each share their own page here at once. One address must not be
    /// shared twice at the same time, since the first to finish would end it for both;
    /// <see cref="Running.SiteBatch"/> sees to that by never auditing one host twice at once.
    /// </remarks>
    /// <returns>Dispose it when the audit of the site ends, so the page is forgotten.</returns>
    public IDisposable Share(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var key = page.AbsoluteUri;
        _shared[key] = new Slot();

        return new Sharing(_shared, key);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Method != HttpMethod.Get
            || request.RequestUri is not { IsAbsoluteUri: true } address
            || !_shared.TryGetValue(address.AbsoluteUri, out var slot))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        Task<Snapshot> fetch;

        // The task is shared, not just its result, so two checks asking at the same moment still
        // cost one request.
        lock (slot)
        {
            fetch = slot.Fetch ??= FetchAsync(request, cancellationToken);
        }

        Snapshot snapshot;

        try
        {
            snapshot = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not kept: a connection that failed once may work for the next check, and a timeout
            // belongs to the request that waited for it.
            lock (slot)
            {
                if (ReferenceEquals(slot.Fetch, fetch) && fetch.IsCompleted)
                {
                    slot.Fetch = null;
                }
            }

            throw;
        }

        // Handed to whoever was waiting for it, but not kept: a 502 or 503 is how a server says
        // "not right now", and the next check, or this one tried again, deserves a fresh answer.
        if ((int)snapshot.StatusCode >= 500)
        {
            lock (slot)
            {
                if (ReferenceEquals(slot.Fetch, fetch))
                {
                    slot.Fetch = null;
                }
            }
        }

        return snapshot.ToResponse(request);
    }

    private async Task<Snapshot> FetchAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        return new Snapshot(
            response.StatusCode,
            response.ReasonPhrase,
            response.Version,

            // The inner handler moves the request to wherever redirects led; checks resolve
            // relative links against it.
            response.RequestMessage?.RequestUri ?? request.RequestUri!,
            [.. response.Headers.Select(header => KeyValuePair.Create(header.Key, header.Value.ToArray()))],
            [.. response.Content.Headers.Select(header => KeyValuePair.Create(header.Key, header.Value.ToArray()))],
            body);
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // The header lets us refuse before reading anything; the loop below catches a server that
        // sent no length, or a wrong one.
        if (content.Headers.ContentLength > _maxPageBytes)
        {
            throw TooLarge();
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _maxPageBytes)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    // A refusal of our own, not a fault of the site: see RefusedException.
    private PageTooLargeException TooLarge() =>
        new($"The page is larger than {_maxPageBytes / (1024d * 1024d):0.#} MB, which is more than sitecheck reads.");

    private sealed class Slot
    {
        public Task<Snapshot>? Fetch { get; set; }
    }

    private sealed class Sharing(ConcurrentDictionary<string, Slot> shared, string key) : IDisposable
    {
        public void Dispose() => shared.TryRemove(key, out _);
    }

    private sealed record Snapshot(
        HttpStatusCode StatusCode,
        string? ReasonPhrase,
        Version Version,
        Uri Address,
        KeyValuePair<string, string[]>[] Headers,
        KeyValuePair<string, string[]>[] ContentHeaders,
        byte[] Body)
    {
        /// <summary>A fresh response each time: the caller disposes it, and a body can be read once.</summary>
        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            // What the real handler does on a redirect, so a remembered answer looks like a live one.
            request.RequestUri = Address;

            var response = new HttpResponseMessage(StatusCode)
            {
                ReasonPhrase = ReasonPhrase,
                Version = Version,
                RequestMessage = request,
                Content = new ByteArrayContent(Body),
            };

            foreach (var (name, values) in Headers)
            {
                response.Headers.TryAddWithoutValidation(name, values);
            }

            foreach (var (name, values) in ContentHeaders)
            {
                // Left for the content to work out from the bytes it actually holds.
                if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    response.Content.Headers.TryAddWithoutValidation(name, values);
                }
            }

            return response;
        }
    }
}
