namespace SiteCheck.Checks;

/// <summary>
/// Reports whether a visitor who arrives over plain http is sent on to the secure site.
/// </summary>
/// <remarks>
/// Old links, printed flyers and some apps still open <c>http://</c> addresses. A site that
/// answers them on http instead of redirecting is shown as "Not secure", and anything typed into
/// it travels in the clear, however good its certificate is.
/// <para>
/// The redirect is followed by <see cref="HttpClient"/> itself, so this works with the shared
/// client as long as it follows redirects, which is the default.
/// </para>
/// </remarks>
public sealed class HttpsRedirectCheck : ISiteCheck
{
    private readonly HttpClient _httpClient;

    public HttpsRedirectCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "https-redirect";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var insecure = new UriBuilder(url) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri;

        try
        {
            using var response = await _httpClient
                .GetAsync(insecure, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var landed = response.RequestMessage?.RequestUri ?? insecure;

            // Where the visitor ends up is the whole question. Whether the page there loads is
            // load-time's finding, so a redirect to a 404 still passes here.
            return landed.Scheme == Uri.UriSchemeHttps
                ? CheckOutcome.Pass($"Visitors who open http://{url.Host} are sent on to the secure site.")
                : CheckOutcome.Fail(
                    $"http://{url.Host} answers on the insecure address instead of sending visitors to https, so browsers mark it \"Not secure\". Ask your host to redirect http to https.");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Nothing listening on http is far less serious than an insecure answer: current
            // browsers try https first, and fall back to http only when that fails. Old links
            // and some apps do not, though, and find nothing at all.
            return CheckOutcome.Warn(
                $"http://{url.Host} does not answer. Browsers try https first, but old http:// links and some apps will find nothing. Ask your host to redirect http to https.");
        }
    }
}
