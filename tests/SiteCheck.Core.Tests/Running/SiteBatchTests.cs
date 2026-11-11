using System.Collections.Concurrent;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Running;

public sealed class SiteBatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Site[] Sites(params string[] urls) => [.. urls.Select(url => new Site(new Uri(url)))];

    private static SiteReport Report(Site site) => new(site, Now, []);

    /// <summary>
    /// An audit that stays "in flight" until the test lets it go, so that what overlaps is decided
    /// by the code under test and observed exactly, not left to timing.
    /// </summary>
    private sealed class HeldAudits
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new();
        private readonly SemaphoreSlim _arrivals = new(0);

        public ConcurrentQueue<string> Started { get; } = [];

        public async Task<SiteReport> AuditAsync(Site site, CancellationToken cancellationToken)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _held[site.Url.AbsoluteUri] = gate;
            Started.Enqueue(site.Url.AbsoluteUri);
            _arrivals.Release();

            await gate.Task.WaitAsync(cancellationToken);

            return Report(site);
        }

        /// <summary>Waits until <paramref name="count"/> more audits have started.</summary>
        public async Task WaitForStartsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _arrivals.WaitAsync(TimeSpan.FromSeconds(10), Token), "An audit that should have started did not.");
            }
        }

        public void Finish(string url) => _held[url].SetResult();
    }

    [Fact]
    public async Task AuditAsync_ReturnsTheReportsInTheOrderTheSitesWereGiven_WhicheverFinishedFirst()
    {
        var sites = Sites("https://a.test/", "https://b.test/", "https://c.test/");
        var audits = new HeldAudits();

        var running = SiteBatch.AuditAsync(sites, audits.AuditAsync, concurrency: 3, Token);
        await audits.WaitForStartsAsync(3);

        // Last in, first out.
        audits.Finish("https://c.test/");
        audits.Finish("https://b.test/");
        audits.Finish("https://a.test/");
        var reports = await running;

        Assert.Equal(sites, reports.Select(report => report.Site));
    }

    [Fact]
    public async Task AuditAsync_NeverHasMoreSitesInFlightThanTheConcurrency()
    {
        var sites = Sites("https://a.test/", "https://b.test/", "https://c.test/");
        var audits = new HeldAudits();

        var running = SiteBatch.AuditAsync(sites, audits.AuditAsync, concurrency: 2, Token);
        await audits.WaitForStartsAsync(2);

        // Two are held open and the third has had every chance to start. It must not have.
        await Task.Delay(100, Token);
        Assert.Equal(2, audits.Started.Count);

        audits.Finish(audits.Started.First());
        await audits.WaitForStartsAsync(1);
        Assert.Equal(3, audits.Started.Count);

        foreach (var url in audits.Started.Skip(1))
        {
            audits.Finish(url);
        }

        Assert.Equal(3, (await running).Count);
    }

    [Fact]
    public async Task AuditAsync_AuditsTwoAddressesOnOneHostOneAfterTheOther()
    {
        // The second would otherwise load the server while the first is timing it.
        var sites = Sites("https://shop.test/", "https://other.test/", "https://SHOP.test/menu");
        var audits = new HeldAudits();

        var running = SiteBatch.AuditAsync(sites, audits.AuditAsync, concurrency: 3, Token);
        await audits.WaitForStartsAsync(2);

        await Task.Delay(100, Token);
        Assert.Equal(["https://other.test/", "https://shop.test/"], audits.Started.Order());

        audits.Finish("https://shop.test/");
        await audits.WaitForStartsAsync(1);
        Assert.Contains("https://shop.test/menu", audits.Started);

        audits.Finish("https://shop.test/menu");
        audits.Finish("https://other.test/");

        Assert.Equal(sites, (await running).Select(report => report.Site));
    }

    [Fact]
    public async Task AuditAsync_WithAConcurrencyOfOne_AuditsInTheOrderGiven()
    {
        var sites = Sites("https://a.test/", "https://b.test/", "https://c.test/");
        var order = new List<string>();

        await SiteBatch.AuditAsync(
            sites,
            (site, _) =>
            {
                order.Add(site.Url.Host);
                return Task.FromResult(Report(site));
            },
            concurrency: 1,
            Token);

        Assert.Equal(["a.test", "b.test", "c.test"], order);
    }

    [Fact]
    public async Task AuditAsync_WhenCancelled_StopsTheAuditsInFlight()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var audits = new HeldAudits();

        var running = SiteBatch.AuditAsync(Sites("https://a.test/", "https://b.test/"), audits.AuditAsync, concurrency: 2, cancellation.Token);
        await audits.WaitForStartsAsync(2);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AuditAsync_WithNoSites_ReturnsNothing()
    {
        var reports = await SiteBatch.AuditAsync([], (site, _) => Task.FromResult(Report(site)), concurrency: 4, Token);

        Assert.Empty(reports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AuditAsync_WithAConcurrencyBelowOne_Throws(int concurrency)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SiteBatch.AuditAsync(Sites("https://a.test/"), (site, _) => Task.FromResult(Report(site)), concurrency, Token));
    }
}
