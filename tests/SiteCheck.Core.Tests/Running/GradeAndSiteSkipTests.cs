using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Running;

public sealed class GradeAndSiteSkipTests
{
    private static readonly Uri Address = new("https://salon.test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 6, 0, 0, TimeSpan.Zero);

    private static SiteReport Report(params CheckStatus[] statuses) =>
        new(new Site(Address), Now, [.. statuses.Select((status, i) => new CheckResult($"check-{i}", status, "detail", TimeSpan.FromSeconds(1)))]);

    private static CheckStatus[] Of(int pass = 0, int warn = 0, int fail = 0, int error = 0, int skip = 0) =>
    [
        .. Enumerable.Repeat(CheckStatus.Pass, pass), .. Enumerable.Repeat(CheckStatus.Warn, warn),
        .. Enumerable.Repeat(CheckStatus.Fail, fail), .. Enumerable.Repeat(CheckStatus.Error, error),
        .. Enumerable.Repeat(CheckStatus.Skip, skip),
    ];

    // ---- Grade ----

    [Theory]
    [InlineData(10, 0, 0, "A")]
    [InlineData(9, 1, 0, "B")]
    [InlineData(7, 3, 0, "B")]
    [InlineData(6, 4, 0, "C")]
    [InlineData(0, 10, 0, "C")]
    [InlineData(9, 0, 1, "D")]
    [InlineData(1, 8, 1, "D")] // one defect is a D however many warnings come with it
    [InlineData(8, 0, 2, "F")]
    [InlineData(0, 0, 10, "F")]
    public void Grade_FollowsARuleAPersonCanRecite(int pass, int warn, int fail, string expected)
    {
        Assert.Equal(expected, Report(Of(pass, warn, fail)).Grade);
    }

    [Fact]
    public void Grade_IgnoresErrorsAndSkips_WhileMostChecksReachedAVerdict()
    {
        Assert.Equal("A", Report(Of(pass: 6, error: 2, skip: 2)).Grade);
    }

    [Theory]
    [InlineData(4, 6)]  // a robot shield: most checks could not look
    [InlineData(0, 10)]
    public void Grade_WhenFewerThanHalfTheChecksReachedAVerdict_IsNotGiven(int pass, int skip)
    {
        // An A for a site nobody managed to look at would be a lie.
        Assert.Null(Report(Of(pass: pass, skip: skip)).Grade);
    }

    [Fact]
    public void Grade_WithNoChecksAtAll_IsNotGiven()
    {
        Assert.Null(Report().Grade);
    }

    [Fact]
    public void Grade_IsShownInTheConsoleSummary_AndWrittenToJson()
    {
        var report = Report(Of(pass: 9, warn: 1));

        Assert.Contains("skipped. Grade B.", ConsoleReport.Render([report]), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(JsonReport.Render([report]));
        Assert.Equal("B", json.RootElement.GetProperty("sites")[0].GetProperty("grade").GetString());
    }

    [Fact]
    public void Grade_WhenNotGiven_IsLeftOutRatherThanInvented()
    {
        var report = Report(Of(skip: 3));

        Assert.DoesNotContain("Grade", ConsoleReport.Render([report]), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(JsonReport.Render([report]));
        Assert.False(json.RootElement.GetProperty("sites")[0].TryGetProperty("grade", out _));
    }

    [Fact]
    public void Grade_IsShownInTheHtmlReport()
    {
        Assert.Contains("Grade D", HtmlReport.Render([Report(Of(pass: 5, fail: 1))], Now), StringComparison.Ordinal);
    }

    [Fact]
    public void AReportWrittenBeforeGradesExisted_StillReads()
    {
        // "New fields are only ever added": the grade is worked out, not required of the file.
        var read = JsonReport.Parse(JsonReport.Render([Report(Of(pass: 2))]).Replace("\"grade\": \"A\",", string.Empty, StringComparison.Ordinal));

        Assert.Equal("A", read[0].Grade);
    }

    // ---- Progress ----

    [Fact]
    public void ProgressLine_SaysWhichSiteHowItWentAndHowManyAreLeft()
    {
        var report = new SiteReport(new Site(Address, "Perruqueria Àngela"), Now, Report(Of(pass: 3, warn: 2, fail: 1, error: 1)).Results);

        Assert.Equal(
            "[3/12] Perruqueria Àngela: 1 failed, 2 warned, 1 errored (6.1 s)",
            ConsoleReport.ProgressLine(3, 12, report, TimeSpan.FromSeconds(6.14)));
    }

    [Fact]
    public void ProgressLine_WhenNothingIsWrong_SaysSoBriefly()
    {
        Assert.Equal("[1/2] salon.test: all fine (0.4 s)", ConsoleReport.ProgressLine(1, 2, Report(Of(pass: 3, skip: 1)), TimeSpan.FromSeconds(0.4)));
    }

    // ---- Per-site skip ----

    [Fact]
    public async Task AuditAsync_DoesNotRunOrReportAChecktheSiteSkips()
    {
        var kept = StubCheck.Returning("load-time", CheckOutcome.Pass("ok"));
        var skipped = StubCheck.Returning("booking-link", CheckOutcome.Warn("no booking"));
        var runner = new CheckRunner([kept, skipped], new FakeTimeProvider());
        var site = new Site(Address) { Skip = new HashSet<string> { "Booking-Link" } };

        var report = await runner.AuditAsync(site, TestContext.Current.CancellationToken);

        // Absent, not SKIP: for this site the check does not exist.
        Assert.Equal(["load-time"], report.Results.Select(result => result.CheckName));
        Assert.Equal(0, skipped.Invocations);
    }

    [Fact]
    public async Task AuditAsync_SkipsForOneSiteOnly()
    {
        var check = StubCheck.Returning("booking-link", CheckOutcome.Warn("no booking"));
        var runner = new CheckRunner([check], new FakeTimeProvider());

        await runner.AuditAsync(new Site(Address) { Skip = new HashSet<string> { "booking-link" } }, TestContext.Current.CancellationToken);
        var other = await runner.AuditAsync(new Site(new Uri("https://other.test/")), TestContext.Current.CancellationToken);

        Assert.Single(other.Results);
    }

    [Fact]
    public void SiteList_ReadsTheChecksASiteSkips()
    {
        var sites = SiteList.Parse("""{ "sites": [ { "url": "salon.test", "skip": ["booking-link", " cookies "] }, "other.test" ] }""");

        Assert.Equal(["booking-link", "cookies"], sites[0].Skip.Order());
        Assert.Empty(sites[1].Skip);
    }

    [Theory]
    [InlineData("""{ "sites": [ { "url": "salon.test", "skip": "booking-link" } ] }""")]
    [InlineData("""{ "sites": [ { "url": "salon.test", "skip": [1, 2] } ] }""")]
    public void SiteList_WhenSkipIsNotAListOfNames_SaysSo(string json)
    {
        var thrown = Assert.Throws<SiteListException>(() => SiteList.Parse(json));

        Assert.Contains("\"skip\" must be a list of check names", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sites_AreEqualWhenTheySkipTheSameChecks_InWhateverOrderOrCase()
    {
        var one = new Site(Address, "Salon") { Skip = new HashSet<string>(["a", "b"], StringComparer.OrdinalIgnoreCase) };
        var same = new Site(Address, "Salon") { Skip = new HashSet<string>(["B", "a"], StringComparer.OrdinalIgnoreCase) };
        var different = new Site(Address, "Salon") { Skip = new HashSet<string>(["a"], StringComparer.OrdinalIgnoreCase) };

        Assert.Equal(one, same);
        Assert.Equal(one.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(one, different);
        Assert.Equal(new Site(Address, "Salon"), new Site(Address, "Salon"));
    }
}
