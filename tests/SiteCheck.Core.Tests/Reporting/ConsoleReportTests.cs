using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;

namespace SiteCheck.Core.Tests.Reporting;

public sealed class ConsoleReportTests
{
    private static readonly Uri Site = new("https://example.test/");

    private static CheckResult Result(string name, CheckStatus status, string detail = "Detail.") =>
        new(name, status, detail, TimeSpan.FromMilliseconds(5));

    [Fact]
    public void ExitCodeFor_WhenEverythingPassesOrWarns_IsOk()
    {
        var results = new[] { Result("a", CheckStatus.Pass), Result("b", CheckStatus.Warn) };

        Assert.Equal(ConsoleReport.ExitOk, ConsoleReport.ExitCodeFor(results));
    }

    [Fact]
    public void ExitCodeFor_WhenACheckFails_IsFailed() =>
        Assert.Equal(
            ConsoleReport.ExitFailed,
            ConsoleReport.ExitCodeFor([Result("a", CheckStatus.Pass), Result("b", CheckStatus.Fail)]));

    [Fact]
    public void ExitCodeFor_WhenOnlyTheToolingFailed_IsToolErrorNotFailed() =>
        Assert.Equal(
            ConsoleReport.ExitToolError,
            ConsoleReport.ExitCodeFor([Result("a", CheckStatus.Pass), Result("b", CheckStatus.Error)]));

    [Fact]
    public void ExitCodeFor_WhenASiteDefectAndAToolErrorCoincide_ReportsTheDefect() =>
        Assert.Equal(
            ConsoleReport.ExitFailed,
            ConsoleReport.ExitCodeFor([Result("a", CheckStatus.Error), Result("b", CheckStatus.Fail)]));

    [Fact]
    public void ExitCodeFor_WithNoResults_IsOk() =>
        Assert.Equal(ConsoleReport.ExitOk, ConsoleReport.ExitCodeFor([]));

    [Fact]
    public void Render_ShowsTheSiteEachCheckAndATally()
    {
        var text = ConsoleReport.Render(
            Site,
            [
                Result("ssl-certificate", CheckStatus.Pass, "All good."),
                Result("load-time", CheckStatus.Fail, "Too slow."),
            ]);

        Assert.Contains("https://example.test/", text, StringComparison.Ordinal);
        Assert.Contains("PASS", text, StringComparison.Ordinal);
        Assert.Contains("ssl-certificate", text, StringComparison.Ordinal);
        Assert.Contains("All good.", text, StringComparison.Ordinal);
        Assert.Contains("FAIL", text, StringComparison.Ordinal);
        Assert.Contains("Too slow.", text, StringComparison.Ordinal);
        Assert.Contains("2 check(s): 1 passed, 0 warned, 1 failed, 0 errored.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ExplainsThatAnErrorIsNotAFindingAboutTheSite()
    {
        var text = ConsoleReport.Render(Site, [Result("a", CheckStatus.Error, "The check threw.")]);

        Assert.Contains("not a finding about the site", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OmitsTheToolingNoteWhenNothingErrored()
    {
        var text = ConsoleReport.Render(Site, [Result("a", CheckStatus.Pass)]);

        Assert.DoesNotContain("not a finding", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithNoResults_StillProducesAReport()
    {
        var text = ConsoleReport.Render(Site, []);

        Assert.Contains("0 check(s)", text, StringComparison.Ordinal);
    }
}
