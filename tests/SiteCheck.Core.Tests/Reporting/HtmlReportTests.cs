using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Reporting;

public sealed class HtmlReportTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 15, 8, 30, 0, TimeSpan.Zero);

    private static SiteReport Report(string? name, params (string Check, CheckStatus Status, string Detail)[] results) =>
        new(new Site(new Uri("https://salon.test/"), name), At, [.. results.Select(r => new CheckResult(r.Check, r.Status, r.Detail, TimeSpan.Zero))]);

    [Fact]
    public void Render_IsACompleteSelfContainedPage()
    {
        var html = HtmlReport.Render([Report("Salon", ("load-time", CheckStatus.Pass, "Fast."))], At);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"viewport\"", html, StringComparison.Ordinal);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>" + "\n", html.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        // One file that survives being attached to an email: nothing fetched from elsewhere.
        Assert.DoesNotContain("<link", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UsesWordsAnOwnerUnderstands()
    {
        var html = HtmlReport.Render(
            [Report("Perruqueria Àngela",
                ("ssl-certificate", CheckStatus.Warn, "Expires in 5 day(s)."),
                ("domain-expiry", CheckStatus.Skip, "Not published."),
                ("load-time", CheckStatus.Fail, "Took 6 s."),
                ("some-future-check", CheckStatus.Pass, "Fine."))],
            At);

        Assert.Contains("Perruqueria &#192;ngela", html, StringComparison.Ordinal);
        Assert.Contains("Security certificate", html, StringComparison.Ordinal);
        Assert.Contains("Worth a look", html, StringComparison.Ordinal);
        Assert.Contains("Needs fixing", html, StringComparison.Ordinal);
        Assert.Contains("Not applicable", html, StringComparison.Ordinal);
        Assert.Contains("some-future-check", html, StringComparison.Ordinal); // unknown checks fall back on their name
        Assert.Contains("2026-01-15 08:30 UTC", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PutsWhatNeedsFixingFirst()
    {
        var html = HtmlReport.Render(
            [Report(null,
                ("ssl-certificate", CheckStatus.Pass, "a"),
                ("domain-expiry", CheckStatus.Skip, "b"),
                ("mobile-viewport", CheckStatus.Warn, "c"),
                ("load-time", CheckStatus.Fail, "d"))],
            At);

        var order = new[] { "Speed", "Phones", "Security certificate", "Domain registration" }
            .Select(title => html.IndexOf(title, StringComparison.Ordinal))
            .ToArray();

        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void Render_EncodesTextThatCameFromTheAuditedPage()
    {
        // A broken link's address comes from someone else's page. Unencoded, it is script in our report.
        var html = HtmlReport.Render(
            [Report("<b>Shop</b>", ("broken-links", CheckStatus.Warn, "Broken: https://x.test/\"><script>alert(1)</script>"))],
            At);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Shop&lt;/b&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { CheckStatus.Pass }, "Nothing needs fixing")]
    [InlineData(new[] { CheckStatus.Fail }, "Something needs fixing")]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Fail, CheckStatus.Fail }, "3 sites checked, 2 need fixing")]
    [InlineData(new[] { CheckStatus.Pass, CheckStatus.Warn }, "2 sites checked, nothing needs fixing")]
    [InlineData(new CheckStatus[0], "No sites were checked")]
    public void Render_OpensWithAOneLineAnswer(CheckStatus[] verdicts, string expected)
    {
        var reports = verdicts.Select(v => Report(null, ("load-time", v, "x"))).ToArray();

        Assert.Contains(expected, HtmlReport.Render(reports, At), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SaysAToolErrorIsNotTheSitesFault()
    {
        var html = HtmlReport.Render([Report(null, ("domain-expiry", CheckStatus.Error, "Registry down."))], At);

        Assert.Contains("Could not check", html, StringComparison.Ordinal);
        Assert.Contains("not with the site", html, StringComparison.Ordinal);
    }
}
