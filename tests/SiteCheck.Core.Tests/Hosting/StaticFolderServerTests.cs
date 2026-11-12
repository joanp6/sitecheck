using System.Net;
using SiteCheck.Hosting;

namespace SiteCheck.Core.Tests.Hosting;

/// <summary>
/// Over a real socket, but only ever to this machine: the server listens on the loopback address,
/// so these need no network and belong with the unit tests.
/// </summary>
public sealed class StaticFolderServerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sitecheck-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _site;

    public StaticFolderServerTests()
    {
        // The site sits in a subfolder, with a file beside it that must never be served.
        _site = Path.Combine(_folder, "dist");
        Directory.CreateDirectory(Path.Combine(_site, "blog"));
        File.WriteAllText(Path.Combine(_folder, "secret.txt"), "outside the site");
        File.WriteAllText(Path.Combine(_site, "index.html"), "<html>home</html>");
        File.WriteAllText(Path.Combine(_site, "about.html"), "<html>about</html>");
        File.WriteAllText(Path.Combine(_site, "blog", "index.html"), "<html>blog</html>");
        File.WriteAllText(Path.Combine(_site, "style.css"), "body{}");
        File.WriteAllText(Path.Combine(_site, "data.bin"), "bytes");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string? Resolve(string path) => StaticFolderServer.Resolve(_site, path);

    private string InSite(params string[] parts) => Path.Combine([_site, .. parts]);

    // ---- Which file an address leads to ----

    [Fact]
    public void Resolve_ServesAFolderAsItsIndex()
    {
        Assert.Equal(InSite("index.html"), Resolve("/"));
        Assert.Equal(InSite("blog", "index.html"), Resolve("/blog/"));
        Assert.Equal(InSite("blog", "index.html"), Resolve("/blog"));
    }

    [Fact]
    public void Resolve_ServesAnAddressWithoutExtensionAsItsHtmlFile()
    {
        // As Cloudflare Pages, Netlify and GitHub Pages do, so links written that way work here too.
        Assert.Equal(InSite("about.html"), Resolve("/about"));
        Assert.Equal(InSite("about.html"), Resolve("/about.html"));
    }

    [Fact]
    public void Resolve_ReadsPercentEncoding()
    {
        File.WriteAllText(InSite("price list.html"), "<html></html>");

        Assert.Equal(InSite("price list.html"), Resolve("/price%20list.html"));
    }

    [Theory]
    [InlineData("/missing.html")]
    [InlineData("/missing")]
    [InlineData("/style.css/extra")]
    public void Resolve_WhenThereIsNoSuchFile_IsNothing(string path)
    {
        Assert.Null(Resolve(path));
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/blog/../../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]           // the same climb, percent-encoded
    [InlineData("/..%2fsecret.txt")]
    [InlineData("/..%5csecret.txt")]             // and with a Windows separator
    [InlineData("/blog/..%2f..%2fsecret.txt")]
    [InlineData("//secret.txt")]
    public void Resolve_NeverLeavesTheFolder(string path)
    {
        // The file exists and sits right beside the site. It must still not be reachable.
        Assert.Null(Resolve(path));
    }

    [Fact]
    public void Resolve_IsNotFooledByASiblingFolderWithALongerName()
    {
        // "dist-private" starts with "dist": a check on the text of the path alone would let it through.
        Directory.CreateDirectory(_site + "-private");
        File.WriteAllText(Path.Combine(_site + "-private", "keys.txt"), "secret");

        Assert.Null(Resolve("/../dist-private/keys.txt"));
    }

    // ---- Over HTTP ----

    [Fact]
    public async Task Server_ServesTheFolderOnThisMachineOnly()
    {
        await using var server = StaticFolderServer.Start(_site);
        using var client = new HttpClient { BaseAddress = server.Address };

        Assert.Equal("127.0.0.1", server.Address.Host);
        Assert.Equal("<html>home</html>", await client.GetStringAsync("/", Token));
        Assert.Equal("<html>about</html>", await client.GetStringAsync("/about", Token));
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/style.css", "text/css")]
    [InlineData("/data.bin", "application/octet-stream")]
    public async Task Server_SaysWhatKindOfFileItSends(string path, string expected)
    {
        await using var server = StaticFolderServer.Start(_site);
        using var client = new HttpClient { BaseAddress = server.Address };

        using var response = await client.GetAsync(path, Token);

        // Checks that read HTML skip an address that is not labelled as a page.
        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Server_AnswersHeadWithTheSizeAndNoBody()
    {
        // page-weight sizes files with HEAD.
        await using var server = StaticFolderServer.Start(_site);
        using var client = new HttpClient { BaseAddress = server.Address };

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/style.css"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task Server_AnswersAMissingFileWith404_AndTheSitesOwnPageWhenItHasOne()
    {
        await using var server = StaticFolderServer.Start(_site);
        using var client = new HttpClient { BaseAddress = server.Address };

        using var bare = await client.GetAsync("/nope", Token);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);

        File.WriteAllText(InSite("404.html"), "<html>lost</html>");

        using var styled = await client.GetAsync("/nope", Token);
        Assert.Equal(HttpStatusCode.NotFound, styled.StatusCode);
        Assert.Equal("<html>lost</html>", await styled.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Server_DoesNotServeWhatIsOutsideTheFolder()
    {
        await using var server = StaticFolderServer.Start(_site);
        using var client = new HttpClient();

        // Sent as written: HttpClient would otherwise tidy the ".." away before it left.
        using var response = await client.GetAsync(new Uri(server.Address, "/%2e%2e/secret.txt"), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("outside the site", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_RefusesAnythingButReading()
    {
        await using var server = StaticFolderServer.Start(_site);
        using var client = new HttpClient { BaseAddress = server.Address };

        using var response = await client.PostAsync("/", new StringContent("x"), Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Server_StopsAnsweringOnceDisposed()
    {
        var server = StaticFolderServer.Start(_site);
        var address = server.Address;
        await server.DisposeAsync();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync(address, Token));
    }

    [Fact]
    public void Start_WhenTheFolderDoesNotExist_SaysSo()
    {
        Assert.Throws<DirectoryNotFoundException>(() => StaticFolderServer.Start(Path.Combine(_folder, "nowhere")));
    }
}
