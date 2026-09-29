using SiteCheck.Certificates;
using SiteCheck.Checks;
using SiteCheck.Running;

namespace SiteCheck.Cli;

/// <summary>
/// The one place that decides which checks a run includes and how they are wired.
/// </summary>
internal sealed class SiteChecks : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _firstVisitClient;

    public SiteChecks()
    {
        _httpClient = CreateClient(new SocketsHttpHandler());

        // LoadTimeCheck measures a first-time visitor, who has no open connection to the site. On
        // the shared client it would reuse the one the checks before it left open, skip the TCP
        // and TLS handshakes, and report a site ten times faster than anyone sees it (0.04 s
        // against 0.4 s, measured). A client that never reuses a connection keeps the number
        // honest whatever order the checks run in.
        _firstVisitClient = CreateClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero });

        Runner = new CheckRunner(
            [
                new SslCertificateCheck(new SslStreamCertificateProvider(), TimeProvider.System),
                new HttpsRedirectCheck(_httpClient),
                new DomainExpiryCheck(_httpClient, TimeProvider.System),
                new LoadTimeCheck(_firstVisitClient, TimeProvider.System),
                new MobileCheck(_httpClient),
                new BrokenLinksCheck(_httpClient),
            ],
            TimeProvider.System);
    }

    public CheckRunner Runner { get; }

    public void Dispose()
    {
        _httpClient.Dispose();
        _firstVisitClient.Dispose();
    }

    private static HttpClient CreateClient(SocketsHttpHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        // Many sites answer 403 to a request with no User-Agent, which would grade a healthy site as
        // broken because of us. Say who we are and where to read about it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("sitecheck/0.1 (+https://github.com/joanp6/sitecheck)");

        return client;
    }
}
