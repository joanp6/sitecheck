using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class ExposedFilesCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string HomePage = "<!DOCTYPE html><html><head><title>Salon</title></head><body>DB_PASSWORD [core] CREATE TABLE ref: refs/</body></html>";

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("Not found") };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>A site where only the paths in <paramref name="files"/> exist.</summary>
    private static StubHttpMessageHandler SiteWith(params (string Path, string Body)[] files) =>
        StubHttpMessageHandler.Routing(request =>
            files.Where(file => request.RequestUri!.AbsolutePath == file.Path).Select(file => Ok(file.Body)).FirstOrDefault()
            ?? NotFound());

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler, Uri? url = null)
    {
        using var client = new HttpClient(handler);
        return await new ExposedFilesCheck(client).RunAsync(url ?? Site, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WhenNothingIsThere_Passes()
    {
        var outcome = await Run(SiteWith());

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheSiteAnswers200WithItsHomePageForEverything_Passes()
    {
        // The false positive this check must not have: plenty of sites, and every single-page app,
        // answer 200 to any address. Here the home page even contains the words the check looks
        // for, and still is not any of the files.
        var outcome = await Run(StubHttpMessageHandler.Routing(_ => Ok(HomePage)));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheSiteRefusesEveryRequest_Passes()
    {
        // 403 for /.env is the right answer, not a robot shield to skip over.
        var outcome = await Run(StubHttpMessageHandler.Routing(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("/.git/HEAD", "ref: refs/heads/main\n")]
    [InlineData("/.git/HEAD", "4b825dc642cb6eb9a060e54bf8d69288fbee4904\n")] // a detached HEAD
    [InlineData("/.git/config", "[core]\n\trepositoryformatversion = 0\n")]
    [InlineData("/.env", "APP_ENV=production\nDB_PASSWORD=hunter2\n")]
    [InlineData("/.env", "# settings\nexport STRIPE_SECRET=sk_live_x\n")]
    [InlineData("/wp-config.php.bak", "<?php\ndefine( 'DB_PASSWORD', 'hunter2' );")]
    [InlineData("/wp-config.php~", "<?php\ndefine( 'DB_PASSWORD', 'hunter2' );")]
    [InlineData("/.htpasswd", "admin:$apr1$Qx3f9$abcdefghijklmnopqrstu.\n")]
    [InlineData("/backup.sql", "-- MySQL dump 10.13\nCREATE TABLE `wp_users` (")]
    [InlineData("/dump.sql", "INSERT INTO users VALUES (1,'admin');")]
    public async Task RunAsync_WhenAFileThatHoldsSecretsComesBack_Fails_NamingWhereItIs(string path, string body)
    {
        var outcome = await Run(SiteWith((path, body)));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("https://salon.test" + path, outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverPutsWhatAFileSaysInTheReport()
    {
        // The report is emailed around and saved as CI output. The address is enough to act on.
        var outcome = await Run(SiteWith(("/.env", "DB_PASSWORD=hunter2\nSTRIPE_SECRET=sk_live_abc\n")));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.DoesNotContain("hunter2", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_live_abc", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenABackupArchiveComesBack_Fails()
    {
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00];
        var handler = StubHttpMessageHandler.Routing(request => request.RequestUri!.AbsolutePath == "/backup.zip" ? Ok(zip) : NotFound());

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("/backup.zip", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/phpinfo.php", "<html><head><title>phpinfo()</title></head>")]
    [InlineData("/info.php", "<html><head><title>PHP 8.2.1 - phpinfo()</title></head>")]
    [InlineData("/server-status", "<html><head><title>Apache Status</title></head><body><h1>Apache Server Status for salon.test</h1>")]
    [InlineData("/wp-content/uploads/", "<html><head><title>Index of /wp-content/uploads</title></head>")]
    public async Task RunAsync_WhenAPageOnlyDescribesTheServer_Warns(string path, string body)
    {
        var outcome = await Run(SiteWith((path, body)));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains(path, outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenSecretsAndDescriptionsAreBothThere_Fails_AndListsBoth()
    {
        var outcome = await Run(SiteWith(("/.git/HEAD", "ref: refs/heads/main\n"), ("/phpinfo.php", "<title>phpinfo()</title>")));

        Assert.Equal(CheckStatus.Fail, outcome.Status);
        Assert.Contains("/.git/HEAD", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("/phpinfo.php", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/.env", "<!DOCTYPE html><html><body>APP_ENV=production</body></html>")] // an error page that quotes the name
    [InlineData("/.env", "just some words, no settings")]
    [InlineData("/.git/HEAD", "<html>ref: refs/heads/main</html>")]
    [InlineData("/backup.sql", "<html><body>CREATE TABLE tutorial</body></html>")]
    [InlineData("/.htpasswd", "contact: info@salon.test")]
    [InlineData("/phpinfo.php", "<html><head><title>Page not found</title></head><body>phpinfo() is disabled</body>")]
    public async Task RunAsync_WhenTheAddressAnswersButIsNotTheFile_Passes(string path, string body)
    {
        var outcome = await Run(SiteWith((path, body)));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_AsksAtTheTopOfTheSite_WhereverTheAuditedPageIs()
    {
        var handler = SiteWith();

        await Run(handler, new Uri("https://salon.test/services/hair?x=1"));

        Assert.Contains("GET https://salon.test/.env", handler.Requests);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("/services/", request, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_OnlyEverSendsGet()
    {
        var handler = SiteWith();

        await Run(handler);

        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenTheSiteStopsAnswering_GivesUpEarly_AsAnErrorNotAFinding()
    {
        var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused"));

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Error, outcome.Status);

        // Three unanswered is the site being down; the other thirteen would only wait.
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RunAsync_WhenAFewRequestsGoUnanswered_SaysThoseFilesAreNotRuledOut()
    {
        var calls = 0;
        var handler = StubHttpMessageHandler.Routing(_ => ++calls is 2 or 5 ? throw new HttpRequestException("reset") : NotFound());

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("2 of the requests got no answer", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReadsOnlyTheStartOfAHugeAnswer()
    {
        // A site that answers every address with a file of gigabytes must not be downloaded.
        var endless = new EndlessStream();
        var handler = StubHttpMessageHandler.Routing(request => request.RequestUri!.AbsolutePath == "/.env"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) }
            : NotFound());

        var outcome = await Run(handler);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.InRange(endless.BytesRead, 1, 8192);
    }

    /// <summary>Never runs out, and counts how much of it was taken.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
