using System.Xml.Linq;
using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Reporting;

public sealed class MarkdownAndJUnitReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 6, 0, 0, TimeSpan.Zero);

    private static CheckResult Result(string name, CheckStatus status, string detail = "detail", double seconds = 1) =>
        new(name, status, detail, TimeSpan.FromSeconds(seconds));

    private static SiteReport Report(string? name, params CheckResult[] results) =>
        new(new Site(new Uri("https://salon.test/"), name), Now, results);

    private static readonly SiteReport Mixed = Report(
        "Perruqueria Àngela",
        Result("load-time", CheckStatus.Pass, "Fast."),
        Result("cookies", CheckStatus.Warn, "One loose cookie."),
        Result("ssl-certificate", CheckStatus.Fail, "Expired."),
        Result("email-auth", CheckStatus.Error, "DNS did not answer."),
        Result("domain-expiry", CheckStatus.Skip, "No dates published."));

    // ---- Markdown ----

    [Fact]
    public void Markdown_HeadsEachSiteWithItsNameVerdictAndGrade()
    {
        var markdown = MarkdownReport.Render([Mixed]);

        Assert.Contains("## Perruqueria Àngela · FAIL · grade D", markdown, StringComparison.Ordinal);
        Assert.Contains("<https://salon.test/>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_ListsProblemsWorstFirst_AndOnlyCountsTheRest()
    {
        var markdown = MarkdownReport.Render([Mixed]);

        var fail = markdown.IndexOf("| FAIL | `ssl-certificate` | Expired. |", StringComparison.Ordinal);
        var warn = markdown.IndexOf("| WARN | `cookies` | One loose cookie. |", StringComparison.Ordinal);
        var error = markdown.IndexOf("| ERROR | `email-auth` |", StringComparison.Ordinal);

        Assert.True(fail >= 0 && fail < warn && warn < error, "Expected FAIL, then WARN, then ERROR.");

        // Twenty rows of PASS would bury the three that matter.
        Assert.DoesNotContain("`load-time`", markdown, StringComparison.Ordinal);
        Assert.Contains("1 check(s) passed, 1 did not apply.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_WhenNothingIsWrong_HasNoTable()
    {
        var markdown = MarkdownReport.Render([Report(null, Result("load-time", CheckStatus.Pass))]);

        Assert.DoesNotContain("|", markdown, StringComparison.Ordinal);
        Assert.Contains("## salon.test · PASS · grade A", markdown, StringComparison.Ordinal);
        Assert.Contains("1 check(s) passed.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_MakesTextFromTheAuditedPageInert()
    {
        // A detail quotes the page, and the page is not ours: it must not break the table, open a
        // link, or carry HTML into the issue this gets pasted in.
        var hostile = "a|b <script>alert(1)</script> [click](http://evil.test) `code`\nsecond line";

        var markdown = MarkdownReport.Render([Report("Evil | <b>Salon</b>", Result("broken-links", CheckStatus.Fail, hostile))]);

        Assert.DoesNotContain("<script>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", markdown, StringComparison.Ordinal);
        Assert.Contains(@"a\|b &lt;script&gt;", markdown, StringComparison.Ordinal);
        Assert.Contains(@"\[click\](http://evil.test)", markdown, StringComparison.Ordinal);
        Assert.Contains("second line |", markdown, StringComparison.Ordinal); // still one row
    }

    [Fact]
    public void Markdown_ExplainsErrorOnlyWhenThereIsOne()
    {
        Assert.Contains("ERROR means", MarkdownReport.Render([Mixed]), StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR means", MarkdownReport.Render([Report(null, Result("a", CheckStatus.Pass))]), StringComparison.Ordinal);
    }

    // ---- JUnit ----

    private static XElement Parsed(params SiteReport[] reports) => XDocument.Parse(JUnitReport.Render(reports)).Root!;

    [Fact]
    public void JUnit_IsOneSuitePerSiteAndOneCasePerCheck_WithCountsThatAddUp()
    {
        var root = Parsed(Mixed, Report(null, Result("load-time", CheckStatus.Pass)));

        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal("6", root.Attribute("tests")!.Value);
        Assert.Equal("1", root.Attribute("failures")!.Value);
        Assert.Equal("1", root.Attribute("errors")!.Value);
        Assert.Equal("1", root.Attribute("skipped")!.Value);

        var suites = root.Elements("testsuite").ToArray();

        Assert.Equal(["Perruqueria Àngela", "salon.test"], suites.Select(suite => suite.Attribute("name")!.Value));
        Assert.Equal(5, suites[0].Elements("testcase").Count());
        Assert.Equal("5", suites[0].Attribute("tests")!.Value);
        Assert.Equal("5", suites[0].Attribute("time")!.Value); // five checks of one second
        Assert.Equal("2026-11-11T06:00:00", suites[0].Attribute("timestamp")!.Value);
        Assert.Equal("https://salon.test/", suites[0].Element("properties")!.Element("property")!.Attribute("value")!.Value);
    }

    [Fact]
    public void JUnit_MapsEachVerdictToWhatACiServerShows()
    {
        var cases = Parsed(Mixed).Element("testsuite")!.Elements("testcase").ToDictionary(test => test.Attribute("name")!.Value);

        Assert.Equal("Expired.", cases["ssl-certificate"].Element("failure")!.Attribute("message")!.Value);
        Assert.Equal("DNS did not answer.", cases["email-auth"].Element("error")!.Attribute("message")!.Value);
        Assert.NotNull(cases["domain-expiry"].Element("skipped"));

        // A warning is fine today, so it must not turn the build red; it is still there to read.
        Assert.Null(cases["cookies"].Element("failure"));
        Assert.Equal("WARN: One loose cookie.", cases["cookies"].Element("system-out")!.Value);

        Assert.DoesNotContain(cases["load-time"].Elements(), child => child.Name.LocalName is "failure" or "error" or "skipped");
        Assert.All(cases.Values, test => Assert.Equal("salon.test", test.Attribute("classname")!.Value));
    }

    [Fact]
    public void JUnit_StaysWellFormedWhateverThePageSaid()
    {
        // Markup is escaped by the writer. Control characters cannot be written in XML at all,
        // and one of them in a quoted page must not cost the whole report.
        var hostile = "\"><failure/> & <b> \u0000\u0001\u001F tail";

        var test = Parsed(Report("A \u0002 B", Result("broken-links", CheckStatus.Fail, hostile))).Element("testsuite")!;

        Assert.Equal("A  B", test.Attribute("name")!.Value);
        Assert.Equal("\"><failure/> & <b>  tail", test.Element("testcase")!.Element("failure")!.Attribute("message")!.Value);
    }

    [Fact]
    public void JUnit_WithNoSites_IsStillAValidEmptyReport()
    {
        var root = Parsed();

        Assert.Equal("0", root.Attribute("tests")!.Value);
        Assert.Empty(root.Elements());
    }
}
