using System.Net;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Http;

namespace SiteCheck.Core.Tests.Http;

public sealed class PageSnapshotHandlerTests
{
    private static readonly Uri Page = new("https://example.test/");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_ForASharedPage_ReachesTheSiteOnlyOnce()
    {
        var site = StubHttpMessageHandler.Serving("<html>hello</html>");
        using var client = ClientSharingThePage(site);

        var first = await client.GetStringAsync(Page, Token);
        var second = await client.GetStringAsync(Page, Token);

        Assert.Equal("<html>hello</html>", first);
        Assert.Equal(first, second);
        Assert.Equal(["GET https://example.test/"], site.Requests);
    }

    [Fact]
    public async Task Get_ForAnAddressNobodyShared_AlwaysReachesTheSite()
    {
        var site = StubHttpMessageHandler.Serving("<html></html>");
        using var client = ClientSharingThePage(site);
        var link = new Uri("https://example.test/contact");

        await client.GetStringAsync(link, Token);
        await client.GetStringAsync(link, Token);

        Assert.Equal(2, site.Requests.Count);
    }

    [Fact]
    public async Task Head_ForASharedPage_IsNotAnsweredFromTheSnapshot()
    {
        // A HEAD has no body to share, and the checks that send one are asking the server a
        // different question.
        var site = StubHttpMessageHandler.Serving("<html></html>");
        using var client = ClientSharingThePage(site);

        await client.GetStringAsync(Page, Token);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Page), Token);

        Assert.Equal(["GET https://example.test/", "HEAD https://example.test/"], site.Requests);
    }

    [Fact]
    public async Task Get_AfterTheSharingEnds_ReachesTheSiteAgain()
    {
        var site = StubHttpMessageHandler.Serving("<html></html>");
        var snapshots = new PageSnapshotHandler(site);
        using var client = new HttpClient(snapshots);

        using (snapshots.Share(Page))
        {
            await client.GetStringAsync(Page, Token);
        }

        await client.GetStringAsync(Page, Token);

        Assert.Equal(2, site.Requests.Count);
    }

    [Fact]
    public async Task Get_FromTheSnapshot_KeepsTheStatusHeadersAndWhereRedirectsLed()
    {
        var site = StubHttpMessageHandler.Routing(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                ReasonPhrase = "Go Away",
                Content = new StringContent("<html>no</html>", System.Text.Encoding.UTF8, "text/html"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://www.example.test/home"),
            };
            response.Headers.TryAddWithoutValidation("Content-Security-Policy", "upgrade-insecure-requests");
            return response;
        });
        using var client = ClientSharingThePage(site);

        using var live = await client.GetAsync(Page, Token);
        using var remembered = await client.GetAsync(Page, Token);

        foreach (var response in new[] { live, remembered })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("Go Away", response.ReasonPhrase);
            Assert.Equal("https://www.example.test/home", response.RequestMessage?.RequestUri?.AbsoluteUri);
            Assert.Equal(["upgrade-insecure-requests"], response.Headers.GetValues("Content-Security-Policy"));
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("<html>no</html>", await response.Content.ReadAsStringAsync(Token));
        }

        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task Get_WhenTheRequestFails_IsNotRemembered_SoTheNextOneTriesAgain()
    {
        var attempts = 0;
        var site = StubHttpMessageHandler.Routing(_ => ++attempts == 1
            ? throw new HttpRequestException("connection reset")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>back</html>") });
        using var client = ClientSharingThePage(site);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(Page, Token));
        var second = await client.GetStringAsync(Page, Token);

        Assert.Equal("<html>back</html>", second);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Get_WhenTheServerErrs_IsNotRemembered_SoTheNextOneAsksAgain(HttpStatusCode error)
    {
        var attempts = 0;
        var site = StubHttpMessageHandler.Routing(_ => ++attempts == 1
            ? new HttpResponseMessage(error) { Content = new StringContent("try later") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>back</html>") });
        using var client = ClientSharingThePage(site);

        using var first = await client.GetAsync(Page, Token);
        using var second = await client.GetAsync(Page, Token);
        using var third = await client.GetAsync(Page, Token);

        Assert.Equal(error, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // And the good answer, once it comes, is the one that stays.
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Get_WhenThePageDeclaresMoreThanTheLimit_IsRefusedAsUnreadable()
    {
        var site = StubHttpMessageHandler.Serving(new string('x', 2000));
        using var client = ClientSharingThePage(site, maxPageBytes: 1024);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(Page, Token));

        Assert.Contains("larger than", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_WhenThePageSendsNoLengthAndRunsPastTheLimit_IsRefusedAsUnreadable()
    {
        // A server that streams without a Content-Length cannot be refused up front; the limit
        // has to hold while reading.
        var site = StubHttpMessageHandler.Routing(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableStream(new byte[2000])),
        });
        using var client = ClientSharingThePage(site, maxPageBytes: 1024);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(Page, Token));
    }

    [Fact]
    public void Constructor_WithNoRoomForAPage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PageSnapshotHandler(StubHttpMessageHandler.Serving(string.Empty), maxPageBytes: 0));
    }

    /// <summary>A client over <paramref name="site"/> that shares <see cref="Page"/> for as long as the test runs.</summary>
    private static HttpClient ClientSharingThePage(HttpMessageHandler site, long maxPageBytes = PageSnapshotHandler.DefaultMaxPageBytes)
    {
        var snapshots = new PageSnapshotHandler(site, maxPageBytes);
        _ = snapshots.Share(Page);

        return new HttpClient(snapshots);
    }

    /// <summary>A body whose length cannot be known ahead, as with a chunked response.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
