using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Running;

namespace SiteCheck.Core.Tests.Running;

public sealed class CheckRunnerTests
{
    private static readonly Uri Site = new("https://example.test/");

    [Fact]
    public async Task RunAsync_RunsEveryRegisteredCheckAgainstTheSite()
    {
        var first = StubCheck.Returning("first", CheckOutcome.Pass("ok"));
        var second = StubCheck.Returning("second", CheckOutcome.Fail("nope"));
        var runner = new CheckRunner([first, second], new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Invocations);
        Assert.Equal(1, second.Invocations);
        Assert.Equal(Site, first.LastUrl);
        Assert.Equal(["first", "second"], results.Select(r => r.CheckName));
        Assert.Equal([CheckStatus.Pass, CheckStatus.Fail], results.Select(r => r.Status));
    }

    [Fact]
    public async Task RunAsync_WithNoRegisteredChecks_ReturnsNothing()
    {
        var runner = new CheckRunner([], new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task RunAsync_TimesEachCheckSeparately()
    {
        var clock = new FakeTimeProvider();
        var runner = new CheckRunner(
            [
                StubCheck.Taking("quick", clock, TimeSpan.FromMilliseconds(200)),
                StubCheck.Taking("slow", clock, TimeSpan.FromSeconds(3)),
            ],
            clock);

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(200), results[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(3), results[1].Duration);
    }

    [Fact]
    public async Task RunAsync_WhenACheckThrows_RecordsAnErrorAndKeepsGoing()
    {
        var survivor = StubCheck.Returning("survivor", CheckOutcome.Pass("ok"));
        var runner = new CheckRunner(
            [StubCheck.Throwing("broken", new InvalidOperationException("bad regex")), survivor],
            new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Error, results[0].Status);
        Assert.Contains("InvalidOperationException", results[0].Detail, StringComparison.Ordinal);
        Assert.Contains("bad regex", results[0].Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Pass, results[1].Status);
        Assert.Equal(1, survivor.Invocations);
    }

    [Fact]
    public async Task RunAsync_WhenACheckTimesOutInternally_RecordsAnErrorRatherThanAbortingTheRun()
    {
        // A cancellation nobody asked for is a stuck check, not a cancelled run.
        var runner = new CheckRunner(
            [StubCheck.Throwing("stuck", new OperationCanceledException()), StubCheck.Returning("next", CheckOutcome.Pass("ok"))],
            new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Error, results[0].Status);
        Assert.Equal(CheckStatus.Pass, results[1].Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheRunIsCancelled_StopsBeforeTheNextCheck()
    {
        using var cancellation = new CancellationTokenSource();
        var skipped = StubCheck.Returning("skipped", CheckOutcome.Pass("ok"));
        var runner = new CheckRunner([StubCheck.Cancelling("canceller", cancellation), skipped], new FakeTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Site, cancellation.Token));

        Assert.Equal(0, skipped.Invocations);
    }
}
