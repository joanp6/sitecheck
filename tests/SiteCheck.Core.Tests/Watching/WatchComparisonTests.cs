using SiteCheck.Checks;
using SiteCheck.Running;
using SiteCheck.Sites;
using SiteCheck.Watching;

namespace SiteCheck.Core.Tests.Watching;

public sealed class WatchComparisonTests
{
    private static readonly Site Shop = new(new Uri("https://shop.test/"), "Shop");
    private static readonly Site Salon = new(new Uri("https://salon.test/"));
    private static readonly DateTimeOffset Yesterday = new(2026, 1, 14, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = Yesterday.AddDays(1);

    private static SiteReport Run(Site site, DateTimeOffset at, params (string Check, CheckStatus Status)[] results) =>
        new(site, at, [.. results.Select(r => new CheckResult(r.Check, r.Status, $"{r.Check} is {r.Status}", TimeSpan.Zero))]);

    private static CheckStatus Remembered(WatchComparison comparison, Site site, string check) =>
        comparison.History.Single(r => r.Site == site).Results.Single(r => r.CheckName == check).Status;

    [Theory]
    [InlineData(CheckStatus.Pass, CheckStatus.Warn, ChangeDirection.Worse)]
    [InlineData(CheckStatus.Pass, CheckStatus.Fail, ChangeDirection.Worse)]
    [InlineData(CheckStatus.Warn, CheckStatus.Fail, ChangeDirection.Worse)]
    [InlineData(CheckStatus.Fail, CheckStatus.Warn, ChangeDirection.Better)]
    [InlineData(CheckStatus.Fail, CheckStatus.Pass, ChangeDirection.Better)]
    [InlineData(CheckStatus.Warn, CheckStatus.Pass, ChangeDirection.Better)]
    public void Of_ReportsAMoveBetweenFindings(CheckStatus before, CheckStatus after, ChangeDirection direction)
    {
        var comparison = WatchComparison.Of([Run(Shop, Yesterday, ("ssl", before))], [Run(Shop, Today, ("ssl", after))]);

        var change = Assert.Single(comparison.Changes);
        Assert.Equal(before, change.Before);
        Assert.Equal(after, change.After);
        Assert.Equal(direction, change.Direction);
        Assert.Equal("ssl is " + after, change.Detail);
        Assert.Equal(direction == ChangeDirection.Worse, comparison.AnythingWorse);
    }

    [Theory]
    [InlineData(CheckStatus.Pass)]
    [InlineData(CheckStatus.Warn)]
    [InlineData(CheckStatus.Fail)]
    public void Of_StayingTheSameIsNotAChange(CheckStatus status)
    {
        // A site that stays broken is reported the day it breaks, not every morning after.
        var comparison = WatchComparison.Of([Run(Shop, Yesterday, ("ssl", status))], [Run(Shop, Today, ("ssl", status))]);

        Assert.Empty(comparison.Changes);
        Assert.False(comparison.AnythingWorse);
    }

    [Fact]
    public void Of_OnTheFirstRun_ReportsWhatIsAlreadyWrongButNotWhatIsFine()
    {
        var comparison = WatchComparison.Of([], [Run(Shop, Today, ("ssl", CheckStatus.Pass), ("links", CheckStatus.Warn), ("mobile", CheckStatus.Fail))]);

        Assert.Equal(["mobile", "links"], comparison.Changes.Select(c => c.CheckName));
        Assert.All(comparison.Changes, change => Assert.Null(change.Before));
        Assert.True(comparison.AnythingWorse);
    }

    [Theory]
    [InlineData(CheckStatus.Error)]
    [InlineData(CheckStatus.Skip)]
    public void Of_AToolErrorOrSkipIsNeitherAChangeNorRemembered(CheckStatus nonFinding)
    {
        // The two false alerts this prevents: "better" going to Error, "worse" coming back.
        var night1 = WatchComparison.Of([Run(Shop, Yesterday, ("ssl", CheckStatus.Fail))], [Run(Shop, Today, ("ssl", nonFinding))]);

        Assert.Empty(night1.Changes);
        Assert.Equal(CheckStatus.Fail, Remembered(night1, Shop, "ssl"));

        var night2 = WatchComparison.Of(night1.History, [Run(Shop, Today.AddDays(1), ("ssl", CheckStatus.Fail))]);

        Assert.Empty(night2.Changes);
    }

    [Fact]
    public void Of_ComparesAgainstTheLastRealFindingAcrossAToolError()
    {
        var night1 = WatchComparison.Of([Run(Shop, Yesterday, ("ssl", CheckStatus.Fail))], [Run(Shop, Today, ("ssl", CheckStatus.Error))]);
        var night2 = WatchComparison.Of(night1.History, [Run(Shop, Today.AddDays(1), ("ssl", CheckStatus.Pass))]);

        var change = Assert.Single(night2.Changes);
        Assert.Equal(CheckStatus.Fail, change.Before);
        Assert.Equal(ChangeDirection.Better, change.Direction);
    }

    [Fact]
    public void Of_ACheckThatHasOnlyEverErredCountsAsFreshWhenItFirstAnswers()
    {
        var night1 = WatchComparison.Of([], [Run(Shop, Today, ("ssl", CheckStatus.Error))]);
        var night2 = WatchComparison.Of(night1.History, [Run(Shop, Today.AddDays(1), ("ssl", CheckStatus.Warn))]);

        Assert.Empty(night1.Changes);
        Assert.Equal(CheckStatus.Error, Remembered(night1, Shop, "ssl")); // still listed, so the history is complete
        Assert.Null(Assert.Single(night2.Changes).Before);
    }

    [Fact]
    public void Of_KeepsSitesApartAndMatchesThemByAddress()
    {
        var previous = new[] { Run(Shop, Yesterday, ("ssl", CheckStatus.Fail)), Run(Salon, Yesterday, ("ssl", CheckStatus.Pass)) };
        var renamed = new Site(Shop.Url, "Shop, renamed");

        var comparison = WatchComparison.Of(previous, [Run(renamed, Today, ("ssl", CheckStatus.Fail)), Run(Salon, Today, ("ssl", CheckStatus.Warn))]);

        var change = Assert.Single(comparison.Changes);
        Assert.Equal(Salon, change.Site);
    }

    [Fact]
    public void Of_ForgetsSitesNoLongerWatched()
    {
        var comparison = WatchComparison.Of(
            [Run(Shop, Yesterday, ("ssl", CheckStatus.Pass)), Run(Salon, Yesterday, ("ssl", CheckStatus.Pass))],
            [Run(Salon, Today, ("ssl", CheckStatus.Pass))]);

        Assert.Equal([Salon], comparison.History.Select(r => r.Site));
    }

    [Fact]
    public void Of_ANewCheckOnAKnownSiteIsTreatedLikeAFirstRun()
    {
        var comparison = WatchComparison.Of(
            [Run(Shop, Yesterday, ("ssl", CheckStatus.Pass))],
            [Run(Shop, Today, ("ssl", CheckStatus.Pass), ("domain-expiry", CheckStatus.Warn))]);

        var change = Assert.Single(comparison.Changes);
        Assert.Equal("domain-expiry", change.CheckName);
        Assert.Null(change.Before);
    }

    [Fact]
    public void Of_ListsWhatGotWorseFirst()
    {
        var comparison = WatchComparison.Of(
            [Run(Shop, Yesterday, ("a", CheckStatus.Fail), ("b", CheckStatus.Pass), ("c", CheckStatus.Pass))],
            [Run(Shop, Today, ("a", CheckStatus.Pass), ("b", CheckStatus.Warn), ("c", CheckStatus.Fail))]);

        Assert.Equal(["c", "b", "a"], comparison.Changes.Select(c => c.CheckName));
    }

    [Fact]
    public void Of_TheHistoryCarriesTheLatestRunDateAndName()
    {
        var renamed = new Site(Shop.Url, "New name");

        var comparison = WatchComparison.Of([Run(Shop, Yesterday, ("ssl", CheckStatus.Pass))], [Run(renamed, Today, ("ssl", CheckStatus.Pass))]);

        var remembered = Assert.Single(comparison.History);
        Assert.Equal(Today, remembered.CheckedAt);
        Assert.Equal("New name", remembered.Site.Name);
    }
}
