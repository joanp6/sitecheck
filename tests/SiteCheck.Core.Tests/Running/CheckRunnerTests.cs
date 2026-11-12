using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Running;
using SiteCheck.Sites;

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
    public async Task RunAsync_CarriesTheMeasuredTimeAndStatusThroughToTheResult()
    {
        var outcome = CheckOutcome.Fail("Forbidden.") with { Elapsed = TimeSpan.FromMilliseconds(90), StatusCode = 403 };
        var runner = new CheckRunner([StubCheck.Returning("load-time", outcome)], new FakeTimeProvider());

        var result = Assert.Single(await runner.RunAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromMilliseconds(90), result.Elapsed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task RunAsync_CarriesTheWeightThroughToTheResult()
    {
        var outcome = CheckOutcome.Warn("Heavy.") with { Bytes = 3_456_789 };
        var runner = new CheckRunner([StubCheck.Returning("page-weight", outcome)], new FakeTimeProvider());

        var result = Assert.Single(await runner.RunAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal(3_456_789, result.Bytes);
    }

    [Fact]
    public async Task RunAsync_CarriesTheOutcomeValuesThroughToTheResult()
    {
        // The runner adds the name and the timing; it must not quietly drop anything the
        // check reported as a value. Detail is prose, ValidUntil is the data behind it.
        var expiry = new DateTimeOffset(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);
        var runner = new CheckRunner(
            [StubCheck.Returning("ssl", CheckOutcome.Warn("Expires soon.") with { ValidUntil = expiry })],
            new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(expiry, results[0].ValidUntil);
    }

    [Fact]
    public async Task RunAsync_WhenACheckReportsNoExpiry_LeavesItUnset()
    {
        var runner = new CheckRunner(
            [StubCheck.Returning("load-time", CheckOutcome.Pass("Fast."))],
            new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Null(results[0].ValidUntil);
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

    private static readonly CheckRetry OnceWithoutWaiting = new(Attempts: 2, Pause: TimeSpan.Zero);

    [Theory]
    [InlineData(CheckStatus.Fail)]
    [InlineData(CheckStatus.Error)]
    public async Task RunAsync_WhenBadNewsDoesNotRepeat_ReportsTheSecondLook(CheckStatus first)
    {
        // The dropped connection at 06:00: bad the first time, fine a moment later.
        var check = StubCheck.ReturningInTurn("flaky", new CheckOutcome(first, "no answer"), CheckOutcome.Pass("ok"));
        var runner = new CheckRunner([check], new FakeTimeProvider(), OnceWithoutWaiting);

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Pass, results[0].Status);
        Assert.Equal("ok", results[0].Detail);
        Assert.Equal(2, check.Invocations);
    }

    [Fact]
    public async Task RunAsync_WhenBadNewsRepeats_ReportsItAndStopsAsking()
    {
        var check = StubCheck.Returning("down", CheckOutcome.Fail("no answer"));
        var runner = new CheckRunner([check], new FakeTimeProvider(), OnceWithoutWaiting);

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, results[0].Status);
        Assert.Equal(2, check.Invocations);
    }

    [Theory]
    [InlineData(CheckStatus.Pass)]
    [InlineData(CheckStatus.Warn)]
    [InlineData(CheckStatus.Skip)]
    public async Task RunAsync_WhenTheVerdictIsNotBadNews_DoesNotAskTwice(CheckStatus status)
    {
        var check = StubCheck.Returning("settled", new CheckOutcome(status, "as it is"));
        var runner = new CheckRunner([check], new FakeTimeProvider(), OnceWithoutWaiting);

        await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(1, check.Invocations);
    }

    [Fact]
    public async Task RunAsync_WhenACheckThrowsOnceAndThenWorks_ReportsTheSecondLook()
    {
        var runs = 0;
        var check = StubCheck.Deciding("flaky", () => runs++ == 0 ? throw new IOException("reset") : CheckOutcome.Pass("ok"));
        var runner = new CheckRunner([check], new FakeTimeProvider(), OnceWithoutWaiting);

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Pass, results[0].Status);
    }

    [Fact]
    public async Task RunAsync_WaitsThePauseBeforeLookingAgain_AndCountsItInTheDuration()
    {
        var clock = new FakeTimeProvider();
        var check = StubCheck.ReturningInTurn("flaky", CheckOutcome.Fail("no answer"), CheckOutcome.Pass("ok"));
        var runner = new CheckRunner([check], clock, new CheckRetry(Attempts: 2, Pause: TimeSpan.FromSeconds(2)));

        var running = runner.RunAsync(Site, TestContext.Current.CancellationToken);

        // Asked once, and now waiting on the clock rather than hammering the site again at once.
        Assert.False(running.IsCompleted);
        Assert.Equal(1, check.Invocations);

        clock.Advance(TimeSpan.FromSeconds(2));
        var results = await running;

        Assert.Equal(2, check.Invocations);
        Assert.Equal(TimeSpan.FromSeconds(2), results[0].Duration);
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringThePause_StopsInsteadOfLookingAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var check = StubCheck.Returning("down", CheckOutcome.Fail("no answer"));
        var runner = new CheckRunner([check], new FakeTimeProvider(), new CheckRetry(Attempts: 2, Pause: TimeSpan.FromMinutes(1)));

        var running = runner.RunAsync(Site, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, check.Invocations);
    }

    [Fact]
    public async Task RunAsync_OnceEnoughChecksHaveFailedTwice_BelievesTheRestTheFirstTime()
    {
        // A site that is down fails everything. Two checks failing twice settles it here.
        StubCheck[] checks = [.. Enumerable.Range(0, 4).Select(i => StubCheck.Returning($"down-{i}", CheckOutcome.Fail("no answer")))];
        var runner = new CheckRunner(checks, new FakeTimeProvider(), new CheckRetry(Attempts: 2, Pause: TimeSpan.Zero, GiveUpAfter: 2));

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal([2, 2, 1, 1], checks.Select(c => c.Invocations));
        Assert.All(results, result => Assert.Equal(CheckStatus.Fail, result.Status));
    }

    [Fact]
    public async Task RunAsync_ChecksThatRecoverOnTheSecondLook_DoNotUseUpTheSecondLooks()
    {
        // Only bad news that repeats says "it is the site". A blip that clears must leave the
        // later checks their retry.
        StubCheck[] checks =
        [
            StubCheck.ReturningInTurn("flaky-1", CheckOutcome.Fail("no answer"), CheckOutcome.Pass("ok")),
            StubCheck.ReturningInTurn("flaky-2", CheckOutcome.Fail("no answer"), CheckOutcome.Pass("ok")),
            StubCheck.ReturningInTurn("flaky-3", CheckOutcome.Fail("no answer"), CheckOutcome.Pass("ok")),
        ];
        var runner = new CheckRunner(checks, new FakeTimeProvider(), new CheckRetry(Attempts: 2, Pause: TimeSpan.Zero, GiveUpAfter: 1));

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Equal(CheckStatus.Pass, result.Status));
    }

    [Fact]
    public async Task RunAsync_StartsEverySiteWithItsSecondLooksIntact()
    {
        var check = StubCheck.Returning("down", CheckOutcome.Fail("no answer"));
        var runner = new CheckRunner([check], new FakeTimeProvider(), new CheckRetry(Attempts: 2, Pause: TimeSpan.Zero, GiveUpAfter: 1));

        await runner.RunAsync(Site, TestContext.Current.CancellationToken);
        await runner.RunAsync(new Uri("https://other.test/"), TestContext.Current.CancellationToken);

        Assert.Equal(4, check.Invocations);
    }

    [Fact]
    public async Task RunAsync_ByDefault_RunsAFailingCheckOnce()
    {
        var check = StubCheck.Returning("down", CheckOutcome.Fail("no answer"));
        var runner = new CheckRunner([check], new FakeTimeProvider());

        await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(1, check.Invocations);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(2, -1)]
    public void CheckRetry_WithNoAttemptsOrANegativePause_Throws(int attempts, int pauseSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CheckRetry(attempts, TimeSpan.FromSeconds(pauseSeconds)));
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

    // The two tests below are a pair, and only mean something together. They pin the
    // distinction the filter on CheckRunner's catch clause exists to draw: a cancellation
    // nobody asked for is one stuck check, while a cancellation the caller asked for is the
    // end of the run. Collapse the catch into a plain `catch (Exception)` and the first
    // still passes; collapse it the other way, rethrowing every OperationCanceledException,
    // and one slow site aborts the whole audit and takes the other twenty-five results
    // with it. That is the failure these guard against.

    [Fact]
    public async Task RunAsync_WhenACheckCancelsItselfWhileTheRunIsHealthy_RecordsAnErrorAndKeepsGoing()
    {
        var survivor = StubCheck.Returning("next", CheckOutcome.Pass("ok"));
        var runner = new CheckRunner(
            [StubCheck.Throwing("stuck", new OperationCanceledException()), survivor],
            new FakeTimeProvider());

        var results = await runner.RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(CheckStatus.Error, results[0].Status);
        Assert.Equal(CheckStatus.Pass, results[1].Status);
        Assert.Equal(1, survivor.Invocations);
    }

    [Fact]
    public async Task RunAsync_WhenACheckThrowsAfterTheCallerCancelled_PropagatesInsteadOfRecordingAnError()
    {
        using var cancellation = new CancellationTokenSource();
        var skipped = StubCheck.Returning("skipped", CheckOutcome.Pass("ok"));
        var runner = new CheckRunner(
            [StubCheck.CancellingAndThrowing("canceller", cancellation), skipped],
            new FakeTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Site, cancellation.Token));

        Assert.Equal(0, skipped.Invocations);
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

    [Fact]
    public async Task AuditAsync_RecordsTheSiteAndWhenTheAuditStarted()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(startedAt);
        var site = new Site(Site, "Example");
        var runner = new CheckRunner([StubCheck.Taking("slow", clock, TimeSpan.FromSeconds(3))], clock);

        var report = await runner.AuditAsync(site, TestContext.Current.CancellationToken);

        // The start, not the end: a report dated after its own checks would read as if they ran later.
        Assert.Equal(startedAt, report.CheckedAt);
        Assert.Equal(site, report.Site);
        Assert.Equal(["slow"], report.Results.Select(r => r.CheckName));
    }

    [Theory]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Skip }, CheckStatus.Pass)]
    [InlineData(new[] { CheckStatus.Skip }, CheckStatus.Pass)]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Warn }, CheckStatus.Warn)]
    [InlineData(new[] { CheckStatus.Warn, CheckStatus.Error }, CheckStatus.Error)]
    [InlineData(new[] { CheckStatus.Error, CheckStatus.Fail, CheckStatus.Warn }, CheckStatus.Fail)]
    public void SiteReport_VerdictIsTheWorstFinding(CheckStatus[] statuses, CheckStatus expected)
    {
        var report = new SiteReport(
            new Site(Site),
            DateTimeOffset.UnixEpoch,
            [.. statuses.Select(s => new CheckResult("c", s, "d", TimeSpan.Zero))]);

        Assert.Equal(expected, report.Verdict);
    }
}
