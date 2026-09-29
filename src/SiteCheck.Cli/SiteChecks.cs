using SiteCheck.Certificates;
using SiteCheck.Checks;
using SiteCheck.Running;

namespace SiteCheck.Cli;

/// <summary>
/// The one place that decides which checks a run includes and how they are wired.
/// </summary>
internal sealed class SiteChecks : IDisposable
{
    public const string PageSpeedKeyVariable = "SITECHECK_PAGESPEED_KEY";

    private readonly HttpClient _httpClient;
    private readonly HttpClient _firstVisitClient;
    private readonly HttpClient _pageSpeedClient;

    /// <param name="includePageSpeed">
    /// Adds <see cref="PageSpeedCheck"/>, which sends each address to Google and takes up to a
    /// minute per site, so it only runs when asked for.
    /// </param>
    public SiteChecks(bool includePageSpeed = false)
    {
        _httpClient = CreateClient(new SocketsHttpHandler());

        // LoadTimeCheck measures a first-time visitor, who has no open connection to the site. On
        // the shared client it would reuse the one the checks before it left open, skip the TCP
        // and TLS handshakes, and report a site ten times faster than anyone sees it (0.04 s
        // against 0.4 s, measured). A client that never reuses a connection keeps the number
        // honest whatever order the checks run in.
        _firstVisitClient = CreateClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero });

        // Google loads the whole page in a throttled browser before it answers.
        _pageSpeedClient = CreateClient(new SocketsHttpHandler(), TimeSpan.FromSeconds(120));

        List<ISiteCheck> checks =
        [
            new SslCertificateCheck(new SslStreamCertificateProvider(), TimeProvider.System),
            new HttpsRedirectCheck(_httpClient),
            new DomainExpiryCheck(_httpClient, TimeProvider.System),
            new LoadTimeCheck(_firstVisitClient, TimeProvider.System),
            new MobileCheck(_httpClient),
            new BrokenLinksCheck(_httpClient),
            new ContactFormCheck(_httpClient),
        ];

        if (includePageSpeed)
        {
            checks.Add(new PageSpeedCheck(_pageSpeedClient, Environment.GetEnvironmentVariable(PageSpeedKeyVariable)));
        }

        Runner = new CheckRunner(checks, TimeProvider.System);
    }

    public CheckRunner Runner { get; }

    public void Dispose()
    {
        _httpClient.Dispose();
        _firstVisitClient.Dispose();
        _pageSpeedClient.Dispose();
    }

    private static HttpClient CreateClient(SocketsHttpHandler handler, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };

        // Many sites answer 403 to a request with no User-Agent, which would grade a healthy site as
        // broken because of us. Say who we are and where to read about it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("sitecheck/0.1 (+https://github.com/joanp6/sitecheck)");

        return client;
    }
}
