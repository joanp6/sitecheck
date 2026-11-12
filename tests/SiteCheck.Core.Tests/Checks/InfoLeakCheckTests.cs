using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class InfoLeakCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private static async Task<CheckOutcome> RunWith(params string[] headers)
    {
        using var client = new HttpClient(PageWith.Headers("<html></html>", headers));
        return await new InfoLeakCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Server: Apache/2.4.41 (Ubuntu)", "Apache/2.4.41")]
    [InlineData("Server: nginx/1.18.0", "nginx/1.18.0")]
    [InlineData("X-Powered-By: PHP/7.4.3", "PHP/7.4.3")]
    [InlineData("X-AspNet-Version: 4.0.30319", "4.0.30319")]
    [InlineData("X-AspNetMvc-Version: 5.2", "5.2")]
    [InlineData("X-Generator: Drupal 9.5 (https://www.drupal.org)", "Drupal 9.5")]
    public async Task RunAsync_WhenAHeaderNamesAVersion_Warns_QuotingIt(string header, string quoted)
    {
        var outcome = await RunWith(header);

        // A risk, not a break-in: it must never turn a run red as a defect.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains(quoted, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server: nginx")]
    [InlineData("Server: cloudflare")]
    [InlineData("X-Powered-By: Express")]
    [InlineData("X-Powered-By: ASP.NET")]
    [InlineData("Via: 1.1 varnish")] // has a version, but is not one of the headers that name the software
    public async Task RunAsync_WhenTheSoftwareIsNamedWithoutAVersion_Passes(string header)
    {
        var outcome = await RunWith(header);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WithNoSuchHeaders_Passes()
    {
        Assert.Equal(CheckStatus.Pass, (await RunWith()).Status);
    }

    [Fact]
    public async Task RunAsync_ListsEveryHeaderThatLeaks()
    {
        var outcome = await RunWith("Server: Apache/2.4.41", "X-Powered-By: PHP/7.4.3");

        Assert.Contains("Server: Apache/2.4.41", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("X-Powered-By: PHP/7.4.3", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CutsAnEndlessHeaderShort()
    {
        var outcome = await RunWith("Server: Apache/2.4.41 " + new string('x', 500));

        Assert.True(outcome.Detail.Length < 500, "A header is the site's text; the report must not carry all of it.");
    }

    [Fact]
    public async Task RunAsync_WhenThePageCannotBeRead_IsNotAFinding()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("no", HttpStatusCode.Forbidden));

        var outcome = await new InfoLeakCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }
}
