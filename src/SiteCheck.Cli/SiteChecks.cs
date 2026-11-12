using SiteCheck.Certificates;
using SiteCheck.Checks;
using SiteCheck.Dns;
using SiteCheck.Http;
using SiteCheck.Running;
using SiteCheck.Sites;
using SiteCheck.Tls;

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
    private readonly PageSnapshotHandler _snapshots;
    private readonly CheckRunner _runner;

    /// <param name="includePageSpeed">
    /// Adds <see cref="PageSpeedCheck"/>, which sends each address to Google and takes up to a
    /// minute per site, so it only runs when asked for.
    /// </param>
    /// <param name="includeExposedFiles">
    /// Adds <see cref="ExposedFilesCheck"/>, which asks the site for files no visitor would, so it
    /// only runs when asked for.
    /// </param>
    /// <param name="allowPrivate">
    /// Also connect to this machine and private networks, for auditing a site of your own that is
    /// not on the internet. Off by default; see <see cref="PublicNetworkGuard"/>.
    /// </param>
    /// <param name="retry">
    /// Run a check that fails or errors once more before believing it. On by default; see <see cref="CheckRetry"/>.
    /// </param>
    public SiteChecks(bool includePageSpeed = false, bool includeExposedFiles = false, bool allowPrivate = false, bool retry = true)
    {
        // Every check that reads the page shares one download of it. See PageSnapshotHandler.
        _snapshots = new PageSnapshotHandler(CreateHandler(allowPrivate));
        _httpClient = CreateClient(_snapshots);

        // LoadTimeCheck measures a first-time visitor, who has no open connection to the site. On
        // the shared client it would reuse the one the checks before it left open, skip the TCP
        // and TLS handshakes, and report a site ten times faster than anyone sees it (0.04 s
        // against 0.4 s, measured). A client that never reuses a connection keeps the number
        // honest whatever order the checks run in. For the same reason it does not go through the
        // shared snapshot: a remembered page arrives in no time.
        var firstVisit = CreateHandler(allowPrivate);
        firstVisit.PooledConnectionLifetime = TimeSpan.Zero;
        _firstVisitClient = CreateClient(firstVisit);

        // Google loads the whole page in a throttled browser before it answers.
        _pageSpeedClient = CreateClient(CreateHandler(allowPrivate), TimeSpan.FromSeconds(120));

        List<ISiteCheck> checks =
        [
            new SslCertificateCheck(new SslStreamCertificateProvider(allowPrivateAddresses: allowPrivate), TimeProvider.System),
            new HttpsRedirectCheck(_httpClient),
            new DomainExpiryCheck(_httpClient, TimeProvider.System),
            new EmailAuthCheck(new UdpDnsResolver()),
            new LoadTimeCheck(_firstVisitClient, TimeProvider.System),
            new MobileCheck(_httpClient),
            new BrokenLinksCheck(_httpClient),
            new ContactFormCheck(_httpClient),
            new MixedContentCheck(_httpClient),
            new ClickToCallCheck(_httpClient),
            new SearchSnippetCheck(_httpClient),
            new LocalBusinessCheck(_httpClient),
            new LegalPagesCheck(_httpClient),
            new PageWeightCheck(_httpClient),
            new SharePreviewCheck(_httpClient),
            new BookingLinkCheck(_httpClient),
            new CopyrightYearCheck(_httpClient, TimeProvider.System),
            new SecurityHeadersCheck(_httpClient),
            new CookiesCheck(_httpClient),
            new InfoLeakCheck(_httpClient),
            new SubresourceIntegrityCheck(_httpClient),
            new CmsVersionCheck(_httpClient),
            new TlsConfigCheck(new RawTlsProbe(allowPrivate)),
            new WwwAddressCheck(new UdpDnsResolver()),
        ];

        // Asks for files no visitor would, which a firewall may take for an attack. See ExposedFilesCheck.
        if (includeExposedFiles)
        {
            checks.Add(new ExposedFilesCheck(_httpClient));
        }

        if (includePageSpeed)
        {
            checks.Add(new PageSpeedCheck(_pageSpeedClient, Environment.GetEnvironmentVariable(PageSpeedKeyVariable)));
        }

        _runner = new CheckRunner(checks, TimeProvider.System, retry ? CheckRetry.Once : CheckRetry.None);
    }

    /// <summary>Runs every check against <paramref name="site"/>, downloading its page once for all of them.</summary>
    public async Task<SiteReport> AuditAsync(Site site, CancellationToken cancellationToken)
    {
        using (_snapshots.Share(site.Url))
        {
            return await _runner.AuditAsync(site, cancellationToken);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _firstVisitClient.Dispose();
        _pageSpeedClient.Dispose();
    }

    // The default is 50 redirects. No real site needs more than a handful, and a chain that long
    // is a loop or a trap.
    private static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        MaxAutomaticRedirections = 10,

        // Every connection, redirects included, is refused unless it leads to the public internet.
        // Left unset, the handler connects wherever the address says. See PublicNetworkGuard.
        ConnectCallback = allowPrivate ? null : PublicNetworkGuard.ConnectAsync,
    };

    private static HttpClient CreateClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),

            // The default is 2 GB. Nothing sitecheck reads whole (a page, a registry's or Google's
            // JSON) comes near 5 MB, so an answer past it is refused rather than held in memory.
            // Requests that only look at the headers are not affected.
            MaxResponseContentBufferSize = PageSnapshotHandler.DefaultMaxPageBytes,
        };

        // Many sites answer 403 to a request with no User-Agent, which would grade a healthy site as
        // broken because of us. Say who we are and where to read about it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("sitecheck/0.1 (+https://github.com/joanp6/sitecheck)");

        return client;
    }
}
