using SiteCheck.Checks;
using SiteCheck.Running;
using SiteCheck.Sites;
using SiteCheck.Watching;

namespace SiteCheck.Core.Tests.Watching;

public sealed class WatchReportTests
{
    private static readonly Site Salon = new(new Uri("https://salon.test/"), "Perruqueria Àngela");
    private static readonly DateTimeOffset At = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);

    private static SiteReport Run(params (string Check, CheckStatus Status, string Detail)[] results) =>
        new(Salon, At, [.. results.Select(r => new CheckResult(r.Check, r.Status, r.Detail, TimeSpan.Zero))]);

    private static WatchComparison Moved() => WatchComparison.Of(
        [Run(("ssl-certificate", CheckStatus.Pass, "ok"), ("load-time", CheckStatus.Fail, "slow"))],
        [Run(("ssl-certificate", CheckStatus.Warn, "Expires in 5 day(s)."), ("load-time", CheckStatus.Pass, "Loaded in 0.3 s."))]);

    [Fact]
    public void RenderText_WhenNothingMoved_SaysSoInOneLine()
    {
        var comparison = WatchComparison.Of([Run(("ssl", CheckStatus.Pass, "ok"))], [Run(("ssl", CheckStatus.Pass, "ok"))]);

        Assert.Equal($"sitecheck watch: no changes across 1 site(s).{Environment.NewLine}", WatchReport.RenderText(comparison));
    }

    [Fact]
    public void RenderText_NamesTheSiteTheCheckTheMoveAndWhatToDo()
    {
        var text = WatchReport.RenderText(Moved());

        Assert.Contains("1 got worse, 1 got better", text, StringComparison.Ordinal);
        Assert.Contains("WORSE   Perruqueria Àngela · ssl-certificate: PASS → WARN", text, StringComparison.Ordinal);
        Assert.Contains("Expires in 5 day(s).", text, StringComparison.Ordinal);
        Assert.Contains("BETTER  Perruqueria Àngela · load-time: FAIL → PASS", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("WORSE", StringComparison.Ordinal) < text.IndexOf("BETTER", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderText_MarksAProblemSeenForTheFirstTime()
    {
        var comparison = WatchComparison.Of([], [Run(("mobile-viewport", CheckStatus.Fail, "No viewport."))]);

        Assert.Contains("mobile-viewport: FAIL (first seen)", WatchReport.RenderText(comparison), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_ListsTheChangesAndWhereEverySiteStands()
    {
        var markdown = WatchReport.RenderMarkdown(Moved());

        Assert.Contains("| 🔴 | Perruqueria Àngela | `ssl-certificate` | PASS → WARN | Expires in 5 day(s). |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 🟢 | Perruqueria Àngela | `load-time` | FAIL → PASS |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Perruqueria Àngela | WARN | `ssl-certificate` |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_WhenNothingMoved_StillShowsWhereSitesStand()
    {
        var comparison = WatchComparison.Of([], [Run(("ssl", CheckStatus.Pass, "ok"))]);

        var markdown = WatchReport.RenderMarkdown(comparison);

        Assert.Contains("No changes since the last run.", markdown, StringComparison.Ordinal);
        Assert.Contains("| Perruqueria Àngela | PASS |  |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_KeepsPipesAndNewlinesInADetailFromBreakingTheTable()
    {
        var comparison = WatchComparison.Of([], [Run(("links", CheckStatus.Fail, "a | b\nc"))]);

        Assert.Contains("| a \\| b c |", WatchReport.RenderMarkdown(comparison), StringComparison.Ordinal);
    }
}
