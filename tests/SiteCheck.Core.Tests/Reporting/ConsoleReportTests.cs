using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Reporting;

public sealed class ConsoleReportTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static CheckResult Result(string name, CheckStatus status, string detail = "Detail.") =>
        new(name, status, detail, TimeSpan.FromMilliseconds(5));

    private static SiteReport Report(params CheckResult[] results) =>
        new(new Site(new Uri("https://example.test/")), At, results);

    private static SiteReport Report(string url, string? name, params CheckStatus[] statuses) =>
        new(new Site(new Uri(url), name), At, [.. statuses.Select((s, i) => Result($"c{i}", s))]);

    [Theory]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Warn }, ConsoleReport.ExitOk)]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Skip }, ConsoleReport.ExitOk)]
    [InlineData(new[] { CheckStatus.Skip }, ConsoleReport.ExitOk)]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Fail }, ConsoleReport.ExitFailed)]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Error }, ConsoleReport.ExitToolError)]
    [InlineData(new[] { CheckStatus.Error, CheckStatus.Fail }, ConsoleReport.ExitFailed)] // a defect outranks a tooling gap
    [InlineData(new CheckStatus[0], ConsoleReport.ExitOk)]
    public void ExitCodeFor_OneSite(CheckStatus[] statuses, int expected) =>
        Assert.Equal(expected, ConsoleReport.ExitCodeFor([Report("https://a.test/", null, statuses)]));

    [Fact]
    public void ExitCodeFor_ManySites_TakesTheWorstSite() =>
        Assert.Equal(
            ConsoleReport.ExitFailed,
            ConsoleReport.ExitCodeFor(
            [
                Report("https://a.test/", null, CheckStatus.Pass),
                Report("https://b.test/", null, CheckStatus.Error),
                Report("https://c.test/", null, CheckStatus.Fail),
            ]));

    [Fact]
    public void Render_ShowsTheSiteEachCheckAndATally()
    {
        var text = ConsoleReport.Render(
        [
            Report(
                Result("ssl-certificate", CheckStatus.Pass, "All good."),
                Result("load-time", CheckStatus.Fail, "Too slow."),
                Result("domain-expiry", CheckStatus.Skip, "Not published.")),
        ]);

        Assert.Contains("https://example.test/", text, StringComparison.Ordinal);
        Assert.Contains("PASS", text, StringComparison.Ordinal);
        Assert.Contains("ssl-certificate", text, StringComparison.Ordinal);
        Assert.Contains("All good.", text, StringComparison.Ordinal);
        Assert.Contains("FAIL", text, StringComparison.Ordinal);
        Assert.Contains("SKIP", text, StringComparison.Ordinal);
        Assert.Contains("3 check(s): 1 passed, 0 warned, 1 failed, 0 errored, 1 skipped.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UsesTheSiteNameWhenThereIsOne()
    {
        var text = ConsoleReport.Render([Report("https://a.test/", "Perruqueria Àngela", CheckStatus.Pass)]);

        Assert.Contains("sitecheck report for Perruqueria Àngela (https://a.test/)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ManySites_AddsAnOverallTally()
    {
        var text = ConsoleReport.Render(
        [
            Report("https://a.test/", null, CheckStatus.Pass),
            Report("https://b.test/", null, CheckStatus.Pass, CheckStatus.Warn),
            Report("https://c.test/", null, CheckStatus.Fail),
        ]);

        Assert.Contains("https://a.test/", text, StringComparison.Ordinal);
        Assert.Contains("https://c.test/", text, StringComparison.Ordinal);
        Assert.Contains("3 site(s): 1 fine, 1 with warnings, 1 failing, 0 not fully checked.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OneSite_HasNoOverallTally() =>
        Assert.DoesNotContain("site(s)", ConsoleReport.Render([Report(Result("a", CheckStatus.Pass))]), StringComparison.Ordinal);

    [Fact]
    public void Render_ExplainsThatAnErrorIsNotAFindingAboutTheSite() =>
        Assert.Contains(
            "not a finding about the site",
            ConsoleReport.Render([Report(Result("a", CheckStatus.Error, "The check threw."))]),
            StringComparison.Ordinal);

    [Fact]
    public void Render_OmitsTheToolingNoteWhenNothingErrored() =>
        Assert.DoesNotContain("not a finding", ConsoleReport.Render([Report(Result("a", CheckStatus.Pass))]), StringComparison.Ordinal);

    [Fact]
    public void Render_WithNoResults_StillProducesAReport() =>
        Assert.Contains("0 check(s)", ConsoleReport.Render([Report()]), StringComparison.Ordinal);
}
