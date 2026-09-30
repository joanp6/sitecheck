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
    public void Render_OmitsFieldsThatDoNotApply()
    {
        var unnamed = Sample() with { Site = new Site(new Uri("https://a.test/")) };

        using var document = JsonDocument.Parse(JsonReport.Render([unnamed]));
        var site = document.RootElement.GetProperty("sites")[0];

        Assert.False(site.TryGetProperty("name", out _));
        Assert.False(site.GetProperty("results")[1].TryGetProperty("validUntil", out _));
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
    public void Parse_WithNoSites_ReadsAnEmptyRun() =>
        Assert.Empty(JsonReport.Parse(JsonReport.Render([])));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "format": 2, "sites": [] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/" } ] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "results": [ { "check": "x", "status": "maybe", "detail": "", "durationMs": 1 } ] } ] }""")]
    [InlineData("""{ "format": 1, "sites": [ { "url": "https://a.test/", "checkedAt": "2026-01-15T08:00:00Z", "results": [ { "check": "x", "status": "7", "detail": "", "durationMs": 1 } ] } ] }""")]
    public void Parse_RejectsAnythingThatIsNotAReportThisVersionWrote(string text) =>
        Assert.ThrowsAny<FormatException>(() => JsonReport.Parse(text));
}
