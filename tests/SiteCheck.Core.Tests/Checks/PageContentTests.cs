using System.Net;
using System.Net.Http.Headers;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

/// <summary>
/// An address that serves a file is not a broken page. Run through real checks, one for each way
/// a check gets its page: on its own, and through <c>PageReader</c>.
/// </summary>
public sealed class PageContentTests
{
    private static readonly Uri Site = new("https://example.test/menu.pdf");

    private static StubHttpMessageHandler Serving(string? contentType) =>
        StubHttpMessageHandler.Routing(_ =>
        {
            var content = new ByteArrayContent("<html><head><title>Menu of the day</title></head></html>"u8.ToArray());
            content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    public async Task AFile_IsSkipped_NotGradedAsABrokenPage(string contentType)
    {
        using var client = new HttpClient(Serving(contentType));

        var ownFetch = await new MobileCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
        var throughReader = await new SearchSnippetCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        foreach (var outcome in new[] { ownFetch, throughReader })
        {
            Assert.Equal(CheckStatus.Skip, outcome.Status);
            Assert.Contains(contentType, outcome.Detail, StringComparison.Ordinal);
            Assert.Equal(200, outcome.StatusCode);
        }
    }

    [Theory]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/plain")] // mislabelled HTML exists, and browsers of old showed it anyway
    [InlineData(null)]
    public async Task APage_HoweverLabelled_IsRead(string? contentType)
    {
        using var client = new HttpClient(Serving(contentType));

        var outcome = await new SearchSnippetCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.NotEqual(CheckStatus.Skip, outcome.Status);

        // It warns about the missing description, which it could only know by reading the HTML.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("description", outcome.Detail, StringComparison.Ordinal);
    }
}
