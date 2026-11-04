using System.Text.Json;
using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Reporting;

public sealed class JsonReportTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static SiteReport Sample() => new(
        new Site(new Uri("https://perruqueria.test/"), "Perruqueria Àngela"),
        At,
        [
            new CheckResult("ssl-certificate", CheckStatus.Warn, "Expires \"soon\".", TimeSpan.FromMilliseconds(212), Expiry),
            new CheckResult("domain-expiry", CheckStatus.Skip, "Not published.", TimeSpan.FromMilliseconds(40)),
        ]);

    private static SiteReport Blocked() => new(
        new Site(new Uri("https://blocked.test/")),
        At,
        [new CheckResult("load-time", CheckStatus.Fail, "The site answered 403 (Forbidden) after 0.09 s.", TimeSpan.FromMilliseconds(95), Elapsed: TimeSpan.FromMilliseconds(90), StatusCode: 403)]);

    [Fact]
    public void Render_WritesTheDocumentedFields()
    {
        using var document = JsonDocument.Parse(JsonReport.Render([Sample()]));
        var site = document.RootElement.GetProperty("sites")[0];
        var first = site.GetProperty("results")[0];

        // Pinned field by field: these names are a contract scripts and history files depend on.
        Assert.Equal(1, document.RootElement.GetProperty("format").GetInt32());
        Assert.Equal("Perruqueria Àngela", site.GetProperty("name").GetString());
        Assert.Equal("https://perruqueria.test/", site.GetProperty("url").GetString());
        Assert.Equal(At, site.GetProperty("checkedAt").GetDateTimeOffset());
        Assert.Equal("warn", site.GetProperty("verdict").GetString());
        Assert.Equal("ssl-certificate", first.GetProperty("check").GetString());
        Assert.Equal("warn", first.GetProperty("status").GetString());
        Assert.Equal("Expires \"soon\".", first.GetProperty("detail").GetString());
        Assert.Equal(212, first.GetProperty("durationMs").GetInt64());
        Assert.Equal(Expiry, first.GetProperty("validUntil").GetDateTimeOffset());
    }

    [Fact]
    public void Render_WritesTheMeasuredTimeAndStatusAsNumbers()
    {
        using var document = JsonDocument.Parse(JsonReport.Render([Blocked()]));
        var result = document.RootElement.GetProperty("sites")[0].GetProperty("results")[0];

        Assert.Equal(JsonValueKind.Number, result.GetProperty("elapsedMs").ValueKind);
        Assert.Equal(90, result.GetProperty("elapsedMs").GetInt64());
        Assert.Equal(403, result.GetProperty("statusCode").GetInt32());
        Assert.Equal(95, result.GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public void Parse_ReadsBackTheMeasuredTimeAndStatus()
    {
        var original = Blocked();

        var copy = Assert.Single(JsonReport.Parse(JsonReport.Render([original])));

        Assert.Equal(original.Results, copy.Results);
    }

    [Fact]
    public void Render_OmitsFieldsThatDoNotApply()
    {
        var unnamed = Sample() with { Site = new Site(new Uri("https://a.test/")) };

        using var document = JsonDocument.Parse(JsonReport.Render([unnamed]));
        var site = document.RootElement.GetProperty("sites")[0];

        Assert.False(site.TryGetProperty("name", out _));
        Assert.False(site.GetProperty("results")[1].TryGetProperty("validUntil", out _));
        Assert.False(site.GetProperty("results")[0].TryGetProperty("elapsedMs", out _));
        Assert.False(site.GetProperty("results")[0].TryGetProperty("statusCode", out _));
    }

    [Fact]
    public void Render_KeepsAccentsAndQuotesReadable()
    {
        // The history file is opened by people too. Valid JSON either way; this way readable.
        var text = JsonReport.Render([Sample()]);

        Assert.Contains("Perruqueria Àngela", text, StringComparison.Ordinal);
        Assert.Contains("Expires \\\"soon\\\".", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsBackWhatRenderWrote()
    {
        var original = Sample();

        var copy = Assert.Single(JsonReport.Parse(JsonReport.Render([original])));

        Assert.Equal(original.Site, copy.Site);
        Assert.Equal(original.CheckedAt, copy.CheckedAt);
        Assert.Equal(original.Results, copy.Results);
    }

    [Fact]
    public void Parse_ReadsAReportWrittenBeforeElapsedAndStatusExisted()
    {
        // Fields are only added within format 1, so history files from older versions must
        // still load, with the new values simply absent.
        const string old = """{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "verdict": "pass", "results": [ { "check": "load-time", "status": "pass", "detail": "The page loaded in 0.4 s.", "durationMs": 410 } ] } ] }""";

        var result = Assert.Single(Assert.Single(JsonReport.Parse(old)).Results);

        Assert.Null(result.Elapsed);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public void Parse_WithNoSites_ReadsAnEmptyRun() =>
        Assert.Empty(JsonReport.Parse(JsonReport.Render([])));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "format": 2, "sites": [] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/" } ] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "results": [ { "check": "x", "status": "maybe", "detail": "", "durationMs": 1 } ] } ] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "results": [ { "check": "x", "status": "7", "detail": "", "durationMs": 1 } ] } ] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "results": [ { "check": "x", "status": "pass", "detail": "", "durationMs": 1, "statusCode": "403" } ] } ] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "results": [ { "check": "x", "status": "pass", "detail": "", "durationMs": 1, "elapsedMs": 1.5 } ] } ] }""")]
    public void Parse_RejectsAnythingThatIsNotAReportThisVersionWrote(string text) =>
        Assert.ThrowsAny<FormatException>(() => JsonReport.Parse(text));
}
