using SiteCheck.Sites;

namespace SiteCheck.Core.Tests.Sites;

public sealed class SiteListTests
{
    [Fact]
    public void Parse_ReadsBothFormsOfEntry()
    {
        var sites = SiteList.Parse("""
            {
              // Comments and trailing commas are allowed: this file is edited by hand.
              "sites": [
                { "name": "Perruqueria Àngela", "url": "perruqueria.test" },
                "https://shop.test/menu",
              ]
            }
            """);

        Assert.Equal(
            [
                new Site(new Uri("https://perruqueria.test/"), "Perruqueria Àngela"),
                new Site(new Uri("https://shop.test/menu")),
            ],
            sites);
    }

    [Theory]
    [InlineData("""{ "sites": [ { "url": "a.test", "name": "  " } ] }""")]
    [InlineData("""{ "sites": [ { "url": "a.test" } ] }""")]
    public void Parse_TreatsABlankOrMissingNameAsNoName(string json) =>
        Assert.Null(Assert.Single(SiteList.Parse(json)).Name);

    [Fact]
    public void Site_FallsBackOnTheHostForItsDisplayName() =>
        Assert.Equal("a.test", new Site(new Uri("https://a.test/x")).DisplayName);

    [Fact]
    public void Parse_ReportsEveryProblemAtOnceWithItsPosition()
    {
        var failure = Assert.Throws<SiteListException>(() => SiteList.Parse("""
            { "sites": [ "a.test", "ftp://nope", { "name": "x" }, 42, { "url": "b.test", "name": 3 }, "https://a.test/" ] }
            """));

        Assert.Contains("entry 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("entry 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("entry 4", failure.Message, StringComparison.Ordinal);
        Assert.Contains("entry 5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("entry 6: https://a.test/ is already listed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("entry 1", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "\"sites\" array")]
    [InlineData("""{ "sites": "a.test" }""", "\"sites\" array")]
    [InlineData("""{ "sites": [] }""", "lists no sites")]
    public void Parse_ExplainsWhyTheFileIsUnusable(string json, string expected) =>
        Assert.Contains(expected, Assert.Throws<SiteListException>(() => SiteList.Parse(json)).Message, StringComparison.Ordinal);

    [Fact]
    public async Task LoadAsync_WhenTheFileIsMissing_SaysWhichFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sitecheck-missing-{Guid.NewGuid():N}.json");

        var failure = await Assert.ThrowsAsync<SiteListException>(() => SiteList.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sitecheck-sites-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "sites": [ "a.test" ] }""", TestContext.Current.CancellationToken);

        try
        {
            Assert.Single(await SiteList.LoadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
