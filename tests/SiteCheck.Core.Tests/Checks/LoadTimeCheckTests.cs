using System.Net;
using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class LoadTimeCheckTests
{
    private static readonly Uri Site = new("https://example.test/");

    [Theory]
    [InlineData(0.4, CheckStatus.Pass)]
    [InlineData(1.49, CheckStatus.Pass)]
    [InlineData(1.5, CheckStatus.Warn)]     // the warning threshold itself already warns
    [InlineData(3.99, CheckStatus.Warn)]
    [InlineData(4, CheckStatus.Fail)]       // and the failure threshold itself already fails
    [InlineData(12, CheckStatus.Fail)]
    public async Task RunAsync_GradesThePageAgainstTheDefaultThresholds(double seconds, CheckStatus expected)
    {
        var clock = new FakeTimeProvider();
        using var client = new HttpClient(
            StubHttpMessageHandler.Responding(HttpStatusCode.OK, clock, TimeSpan.FromSeconds(seconds)));
        var check = new LoadTimeCheck(client, clock);

        var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(expected, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_ReportsTheMeasuredTimeInTheDetail()
    {
        var clock = new FakeTimeProvider();
        using var client = new HttpClient(
            StubHttpMessageHandler.Responding(HttpStatusCode.OK, clock, TimeSpan.FromSeconds(0.75)));
        var check = new LoadTimeCheck(client, clock);

        var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Contains("0.75 s", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HonoursCustomThresholds()
    {
        var clock = new FakeTimeProvider();
        using var client = new HttpClient(
            StubHttpMessageHandler.Responding(HttpStatusCode.OK, clock, TimeSpan.FromSeconds(0.6)));
        var check = new LoadTimeCheck(
            client,
            clock,
            new LoadTimeCheckOptions(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)));

        var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenTheSiteAnswersWithAnErrorStatus_FailsEvenIfItWasFast(HttpStatusCode status)
    {
        var clock = new FakeTimeProvider();
        using var client = new HttpClient(
            StubHttpMessageHandler.Responding(status, clock, TimeSpan.FromMilliseconds(80)));
        var check = new LoadTimeCheck(client, clock);

        var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains(((int)status).ToString(provider: null), outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheSiteCannotBeReached_Fails()
    {
        using var client = new HttpClient(
            StubHttpMessageHandler.Throwing(new HttpRequestException("No such host is known.")));
        var check = new LoadTimeCheck(client, new FakeTimeProvider());

        var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("could not be reached", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheRequestTimesOut_FailsRatherThanErroring()
    {
        // A site that never answers is broken for its visitors, so it is the site's
        // failure, not a gap in our tooling.
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var check = new LoadTimeCheck(client, new FakeTimeProvider());

        var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("did not respond within 10 s", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesInsteadOfBlamingTheSite()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()));
        var check = new LoadTimeCheck(client, new FakeTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.RunAsync(Site, cancellation.Token));
    }

    [Fact]
    public void Options_RejectAFailureThresholdThatIsNotAboveTheWarning() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoadTimeCheckOptions(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)));

    [Fact]
    public void Options_RejectANonPositiveWarningThreshold() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoadTimeCheckOptions(TimeSpan.Zero, TimeSpan.FromSeconds(4)));
}
